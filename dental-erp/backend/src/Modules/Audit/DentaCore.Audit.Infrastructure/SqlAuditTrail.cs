using DentaCore.Audit.Application;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace DentaCore.Audit.Infrastructure;

/// <summary>
/// Audit zənciri PostgreSQL-də (T001: audit_log). Heş DB-də hesablanır, çünki jsonb və inet dəyərləri saxlanarkən
/// normallaşır (açar sırası, boşluq); tətbiq qatında hesablanan heş sonradan yoxlananda uyğun gəlməzdi.
/// Yazılar tenant üzrə advisory lock ilə ardıcıllaşdırılır (zəncirin budaqlanmaması üçün). Bu, tenant başına yazı
/// ötürmə qabiliyyətini məhdudlaşdırır (klinika miqyası üçün kifayətdir).
/// </summary>
internal sealed class SqlAuditTrail(ITenantDataSources dataSources, ITenantContext tenant) : IAuditTrail, IAuditChainVerifier
{
    // Kanonik forma: həm yazıda, həm yoxlamada eyni ifadə. Prefiks sütunların mənbəyini göstərir.
    private static string Canonical(string p) =>
        $"(EXTRACT(EPOCH FROM {p}.occurred_at) * 1000000)::bigint::text || '|' || coalesce({p}.user_id::text, '') || '|' || {p}.action || '|' || " +
        $"coalesce({p}.entity, '') || '|' || coalesce({p}.entity_id::text, '') || '|' || coalesce(host({p}.ip), '') || '|' || coalesce({p}.after_data::text, '')";

    private static readonly string InsertSql = $"""
        WITH n AS (
            SELECT $1::timestamptz AS occurred_at, $2::uuid AS user_id, $3::text AS action, $4::text AS entity,
                   $5::uuid AS entity_id, $6::inet AS ip, $7::jsonb AS after_data),
        prev AS (SELECT hash FROM audit_log ORDER BY id DESC LIMIT 1)
        INSERT INTO audit_log (occurred_at, user_id, action, entity, entity_id, ip, after_data, prev_hash, hash)
        SELECT n.occurred_at, n.user_id, n.action, n.entity, n.entity_id, n.ip, n.after_data,
               (SELECT hash FROM prev),
               digest(coalesce((SELECT hash FROM prev), '\x'::bytea) || convert_to({Canonical("n")}, 'UTF8'), 'sha256')
        FROM n
        """;

    private static readonly string VerifySql = $"""
        SELECT count(*) OVER () AS total, id FROM (
            SELECT a.id, a.hash, a.prev_hash,
                   lag(a.hash) OVER (ORDER BY a.id) AS expected_prev,
                   digest(coalesce(a.prev_hash, '\x'::bytea) || convert_to({Canonical("a")}, 'UTF8'), 'sha256') AS recomputed
            FROM audit_log a) s
        WHERE s.hash IS DISTINCT FROM s.recomputed OR s.prev_hash IS DISTINCT FROM s.expected_prev
        ORDER BY id LIMIT 1
        """;

    public async Task RecordAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await dataSources.Get(tenant.Schema).OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Tenant sxemi üzrə qlobal kilid: yalnız bir yazı əvvəlki heş-i oxuyub növbəti qeydi əlavə edə bilər
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(current_schema() || ':audit', 0))", connection, transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = new NpgsqlCommand(InsertSql, connection, transaction);
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = record.OccurredAt.UtcDateTime });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)record.UserId ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = record.Action });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)record.Entity ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)record.EntityId ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Inet, Value = ParseIp(record.Ip) });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)record.DetailsJson ?? DBNull.Value });
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AuditChainResult> VerifyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSources.Get(tenant.Schema).OpenConnectionAsync(cancellationToken);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM audit_log", connection);
        var total = (long)(await count.ExecuteScalarAsync(cancellationToken) ?? 0L);

        await using var command = new NpgsqlCommand(VerifySql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AuditChainResult(false, total, reader.GetInt64(1))
            : new AuditChainResult(true, total, null);
    }

    private static object ParseIp(string? ip) =>
        ip is not null && System.Net.IPAddress.TryParse(ip, out var parsed) ? parsed : DBNull.Value;
}

public static class AuditModule
{
    public static IServiceCollection AddAuditModule(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddTenancy(configuration);
        services.AddScoped<SqlAuditTrail>();
        services.AddScoped<IAuditTrail>(sp => sp.GetRequiredService<SqlAuditTrail>());
        services.AddScoped<IAuditChainVerifier>(sp => sp.GetRequiredService<SqlAuditTrail>());
        return services;
    }
}
