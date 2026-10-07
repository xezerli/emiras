using System.Text.Json;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace DentaCore.BuildingBlocks.Infrastructure.Messaging;

/// <summary>Aktiv tenant-ların siyahısı (platform.tenants).</summary>
public interface ITenantEnumerator
{
    Task<IReadOnlyList<TenantInfo>> ListActiveAsync(CancellationToken cancellationToken);
}

internal sealed class NpgsqlTenantEnumerator(NpgsqlDataSource platform) : ITenantEnumerator
{
    public async Task<IReadOnlyList<TenantInfo>> ListActiveAsync(CancellationToken cancellationToken)
    {
        await using var command = platform.CreateCommand(
            "SELECT id, slug, schema_name FROM platform.tenants WHERE status IN ('trial','active') AND deleted_at IS NULL ORDER BY slug");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<TenantInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TenantInfo(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }
}

/// <summary>
/// Transactional Outbox → RabbitMQ. Hər tenant üçün gözləyən sətirləri <c>FOR UPDATE SKIP LOCKED</c> ilə götürür:
/// bir neçə Workers nüsxəsi eyni sətri iki dəfə göndərmir. Göndərmə tranzaksiya daxilindədir: proses commit-dən əvvəl ölərsə
/// mesaj təkrar göndərilir (at-least-once), buna görə consumer-lər inbox ilə dublikatı atır.
/// Uğursuzluqda exponential backoff (5 san · 2^cəhd, ən çox 1 saat); <c>OutboxMaxAttempts</c>-dən sonra sətir "ölü" qalır və monitorinqdə görünməlidir.
/// </summary>
public sealed partial class OutboxProcessor(
    ITenantEnumerator tenants,
    ITenantDataSources dataSources,
    IEventPublisher publisher,
    IOptions<RabbitMqOptions> options,
    ILogger<OutboxProcessor> logger)
{
    private const string SelectSql = """
        SELECT id, type, routing_key, payload::text, occurred_at
        FROM outbox_messages
        WHERE processed_at IS NULL AND attempts < $1 AND next_attempt_at <= now()
        ORDER BY occurred_at, id
        LIMIT $2
        FOR UPDATE SKIP LOCKED
        """;

    /// <summary>Bütün tenant-lar üçün bir dövr. Göndərilən mesajların sayını qaytarır.</summary>
    public async Task<int> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var tenant in await tenants.ListActiveAsync(cancellationToken))
        {
            try
            {
                total += await ProcessTenantAsync(tenant, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Bir tenant-ın problemi (məs. sxem miqrasiyası gözləyir) qalanlarını dayandırmasın
                LogTenantFailed(logger, tenant.Slug, ex);
            }
        }

        return total;
    }

    private async Task<int> ProcessTenantAsync(TenantInfo tenant, CancellationToken cancellationToken)
    {
        var o = options.Value;
        await using var connection = await dataSources.Get(tenant.Schema).OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var batch = new List<(Guid Id, string Type, string RoutingKey, string Payload, DateTimeOffset OccurredAt)>();
        await using (var select = new NpgsqlCommand(SelectSql, connection, transaction))
        {
            select.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = o.OutboxMaxAttempts });
            select.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = o.OutboxBatchSize });
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                batch.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
            }
        }

        if (batch.Count == 0)
        {
            return 0;
        }

        var sent = new List<Guid>();
        foreach (var message in batch)
        {
            try
            {
                var envelope = new IntegrationEnvelope(
                    message.Id, message.Type, 1, tenant.Id, tenant.Slug, message.OccurredAt, message.RoutingKey, null,
                    JsonDocument.Parse(message.Payload).RootElement.Clone());
                await publisher.PublishAsync(envelope, cancellationToken);
                sent.Add(message.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await using var fail = new NpgsqlCommand(
                    "UPDATE outbox_messages SET attempts = attempts + 1, last_error = left($2, 500), next_attempt_at = now() + least(interval '1 hour', interval '5 seconds' * power(2, attempts)) WHERE id = $1",
                    connection, transaction);
                fail.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = message.Id });
                fail.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = ex.Message });
                await fail.ExecuteNonQueryAsync(cancellationToken);
                // Broker əlçatmazdırsa qalan mesajlara cəhd etməyin faydası yoxdur
                break;
            }
        }

        if (sent.Count > 0)
        {
            await using var done = new NpgsqlCommand("UPDATE outbox_messages SET processed_at = now(), last_error = NULL WHERE id = ANY($1)", connection, transaction);
            done.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = sent.ToArray() });
            await done.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return sent.Count;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox processing failed for tenant {Tenant}")]
    private static partial void LogTenantFailed(ILogger logger, string tenant, Exception exception);
}

public sealed class OutboxPublisherService(OutboxProcessor processor, IOptions<RabbitMqOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var sent = 0;
            try
            {
                sent = await processor.ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Mesaj çoxdursa gözləmədən davam, yoxsa interval qədər dayan
            if (sent == 0)
            {
                await Task.Delay(options.Value.OutboxPollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
