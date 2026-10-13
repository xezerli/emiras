using System.Globalization;
using System.Text;
using System.Text.Json;
using DentaCore.Audit.Contracts;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace DentaCore.Search.Infrastructure;

/// <summary>
/// Audit hadisəsini Elasticsearch-ə yazır. Sənəd id-si <c>auditId</c>-dir və <c>_create</c> ilə yazılır: təkrar çatdırılma (at-least-once) sənədi
/// dəyişmir və ikinci sənəd yaratmır. Audit-in həqiqət mənbəyi PostgreSQL hash zənciridir; indeks yalnız axtarış üçün surətdir.
/// </summary>
[Consumes("search.audit", "audit.recorded")]
public sealed class AuditIndexConsumer(ElasticClient client, IOptions<ElasticOptions> options) : IIntegrationConsumer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task HandleAsync(IntegrationEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var audit = envelope.Deserialize<AuditRecordedV1>();
        var o = options.Value;
        var index = ElasticIndexes.Audit(o.IndexPrefix, envelope.TenantId, audit.OccurredAt);
        await client.EnsureIndexAsync(index, ElasticIndexes.AuditBody(o.Shards, o.Replicas), cancellationToken);
        var document = JsonSerializer.Serialize(
            new
            {
                tenantId = envelope.TenantId.ToString("N", CultureInfo.InvariantCulture), auditId = audit.AuditId, occurredAt = audit.OccurredAt, action = audit.Action,
                entity = audit.Entity, entityId = audit.EntityId, userId = audit.UserId, details = audit.Details,
            }, Json);
        await client.CreateIfAbsentAsync(index, audit.AuditId.ToString(CultureInfo.InvariantCulture), document, cancellationToken);
    }
}

/// <summary>
/// Pasiyent indeksi <c>change_log</c>-dan qidalanır (T011-in commit-təhlükəsiz xid kursoru): hadisələrə güvənmir, çünki hər dəyişiklik
/// (redaktə, soft-delete, birbaşa SQL) jurnala düşür. Kursor tenant sxemində saxlanır və yalnız Elasticsearch qəbul etdikdən sonra irəli gedir.
/// Kursor yoxdursa əvvəlcə tam doldurma (backfill) edilir. Yazılar sətirin <c>row_version</c>-u ilə "external" versiyalıdır:
/// təkrar, gecikmiş və ya sıradan çıxmış yazı daha yeni sənədi əzə bilməz.
/// </summary>
internal sealed class PatientIndexer(ITenantDataSources dataSources, ElasticClient client, IOptions<ElasticOptions> options)
{
    private const string CursorName = "patients-es";
    private const int BatchSize = 500;
    private const int OversizedGroup = 10_000;

    public sealed record Result(int Processed, bool HasMore);

    public async Task<Result> RunAsync(TenantInfo tenant, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var index = ElasticIndexes.Patients(o.IndexPrefix, tenant.Id);
        await client.EnsureIndexAsync(index, ElasticIndexes.PatientsBody(o.Shards, o.Replicas), cancellationToken);

        await using var connection = await dataSources.Get(tenant.Schema).OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        long horizon;
        await using (var command = new NpgsqlCommand("SELECT pg_snapshot_xmin(pg_current_snapshot())::text::bigint", connection, transaction))
        {
            horizon = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        long? cursor = null;
        await using (var command = new NpgsqlCommand("SELECT cursor FROM indexer_cursors WHERE name = $1", connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = CursorName });
            cursor = await command.ExecuteScalarAsync(cancellationToken) as long?;
        }

        if (cursor is null)
        {
            var count = await BackfillAsync(connection, transaction, tenant, index, cancellationToken);
            await SaveCursorAsync(connection, transaction, horizon, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new Result(count, false);
        }

        var rows = await ReadLogAsync(connection, transaction, cursor.Value, horizon, BatchSize + 1, null, cancellationToken);
        var newCursor = horizon;
        var hasMore = false;
        if (rows.Count > BatchSize)
        {
            hasMore = true;
            var cut = rows[BatchSize].Xid;
            var page = rows.Where(r => r.Xid < cut).ToList();
            if (page.Count > 0)
            {
                rows = page;
                newCursor = cut;
            }
            else
            {
                rows = await ReadLogAsync(connection, transaction, cut, cut + 1, OversizedGroup, cut, cancellationToken);
                newCursor = cut + 1;
            }
        }

        var ids = rows.Select(r => r.EntityId).Distinct().ToArray();
        if (ids.Length > 0)
        {
            var current = await LoadAsync(connection, transaction, ids, cancellationToken);
            var ndjson = new StringBuilder();
            foreach (var id in ids)
            {
                if (current.TryGetValue(id, out var row))
                {
                    AppendIndex(ndjson, index, tenant.Id, row);
                }
                else
                {
                    ndjson.Append(JsonSerializer.Serialize(new { delete = new { _index = index, _id = id.ToString("D", CultureInfo.InvariantCulture) } })).Append('\n');
                }
            }

            await client.BulkAsync(ndjson.ToString(), cancellationToken);
        }

        await SaveCursorAsync(connection, transaction, newCursor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Result(ids.Length, hasMore);
    }

    private sealed record PatientRow(Guid Id, Guid BranchId, long ChartNo, string FirstName, string LastName, string? FatherName, string Status, int RowVersion);

    private sealed record LogRow(Guid EntityId, long Xid);

    private async Task<int> BackfillAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantInfo tenant, string index, CancellationToken cancellationToken)
    {
        var total = 0;
        Guid? after = null;
        while (true)
        {
            var rows = new List<PatientRow>();
            await using (var command = new NpgsqlCommand(
                "SELECT id, branch_id, chart_no, first_name, last_name, father_name, status, row_version FROM patients WHERE deleted_at IS NULL AND ($1::uuid IS NULL OR id > $1) ORDER BY id LIMIT $2",
                connection, transaction))
            {
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)after ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = BatchSize });
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(Read(reader));
                }
            }

            if (rows.Count == 0)
            {
                return total;
            }

            var ndjson = new StringBuilder();
            foreach (var row in rows)
            {
                AppendIndex(ndjson, index, tenant.Id, row);
            }

            await client.BulkAsync(ndjson.ToString(), cancellationToken);
            total += rows.Count;
            after = rows[^1].Id;
        }
    }

    private static async Task<List<LogRow>> ReadLogAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long fromXid, long toXidExclusive, int limit, long? onlyXid, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT entity_id, xid::text::bigint FROM change_log WHERE entity = 'patients' AND xid >= $1::text::xid8 AND xid < $2::text::xid8 AND ($3::bigint IS NULL OR xid = $3::text::xid8) ORDER BY xid, seq LIMIT $4",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = fromXid.ToString(CultureInfo.InvariantCulture) });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = toXidExclusive.ToString(CultureInfo.InvariantCulture) });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)onlyXid ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = limit });
        var rows = new List<LogRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LogRow(reader.GetGuid(0), reader.GetInt64(1)));
        }

        return rows;
    }

    private static async Task<Dictionary<Guid, PatientRow>> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid[] ids, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT id, branch_id, chart_no, first_name, last_name, father_name, status, row_version FROM patients WHERE id = ANY($1) AND deleted_at IS NULL", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = ids });
        var result = new Dictionary<Guid, PatientRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = Read(reader);
            result[row.Id] = row;
        }

        return result;
    }

    private static PatientRow Read(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetInt32(7));

    private static void AppendIndex(StringBuilder ndjson, string index, Guid tenantId, PatientRow row)
    {
        ndjson.Append(JsonSerializer.Serialize(new { index = new { _index = index, _id = row.Id.ToString("D", CultureInfo.InvariantCulture), version = row.RowVersion, version_type = "external_gte" } })).Append('\n');
        ndjson.Append(JsonSerializer.Serialize(new
        {
            tenantId = tenantId.ToString("N", CultureInfo.InvariantCulture),
            patientId = row.Id.ToString("D", CultureInfo.InvariantCulture),
            branchId = row.BranchId.ToString("N", CultureInfo.InvariantCulture),
            chartNo = row.ChartNo,
            chartNoText = row.ChartNo.ToString(CultureInfo.InvariantCulture),
            status = row.Status,
            firstName = row.FirstName,
            lastName = row.LastName,
            fatherName = row.FatherName,
            fullName = string.Join(' ', new[] { row.LastName, row.FirstName, row.FatherName }.Where(s => !string.IsNullOrWhiteSpace(s))),
        })).Append('\n');
    }

    private static async Task SaveCursorAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long cursor, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO indexer_cursors(name, cursor) VALUES ($1, $2) ON CONFLICT (name) DO UPDATE SET cursor = greatest(indexer_cursors.cursor, EXCLUDED.cursor), updated_at = now()",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = CursorName });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = cursor });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Hər tenant üçün dövri indeksləmə. Bir tenant-ın (və ya Elasticsearch-in) xətası digərlərini dayandırmır; kursor irəli getmədiyi üçün növbəti dövrdə təkrar cəhd olunur.</summary>
public sealed partial class PatientIndexerService(IServiceScopeFactory scopes, ITenantEnumerator tenants, IOptions<ElasticOptions> options, ILogger<PatientIndexerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, options.Value.IndexerIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Bütün tenant-lar üçün bir dövr (testlər birbaşa çağırır).</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TenantInfo> list;
        try
        {
            list = await tenants.ListActiveAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, "*", ex);
            return;
        }

        foreach (var tenant in list)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var indexer = scope.ServiceProvider.GetRequiredService<PatientIndexer>();
                PatientIndexer.Result result;
                do
                {
                    result = await indexer.RunAsync(tenant, cancellationToken);
                }
                while (result.HasMore && !cancellationToken.IsCancellationRequested);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, tenant.Slug, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Patient indexing failed for tenant {Tenant}; will retry")]
    private static partial void LogFailed(ILogger logger, string tenant, Exception exception);
}
