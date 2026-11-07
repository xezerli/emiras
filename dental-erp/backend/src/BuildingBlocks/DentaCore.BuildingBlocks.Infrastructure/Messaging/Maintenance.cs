using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DentaCore.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// Hər tenant üçün dövri texniki işlər:
/// audit_log partition-ları (cari + 2 ay irəli; olmasa audit yazısı uğursuz olur və fail-closed səbəbindən sorğular xəta verir),
/// işlənmiş outbox sətirlərinin təmizlənməsi (7 gün), köhnə refresh token və change_log sətirləri.
/// Hamısı idempotentdir və təhlükəsiz təkrar işləyir.
/// </summary>
public sealed partial class TenantMaintenance(ITenantEnumerator tenants, ITenantDataSources dataSources, ILogger<TenantMaintenance> logger)
{
    private static readonly string[] Statements =
    [
        "SELECT audit_create_partition(current_date)",
        "SELECT audit_create_partition((current_date + interval '1 month')::date)",
        "SELECT audit_create_partition((current_date + interval '2 month')::date)",
        "DELETE FROM outbox_messages WHERE id IN (SELECT id FROM outbox_messages WHERE processed_at < now() - interval '7 days' LIMIT 5000)",
        "DELETE FROM refresh_tokens WHERE id IN (SELECT id FROM refresh_tokens WHERE expires_at < now() - interval '30 days' LIMIT 5000)",
        // Jurnal təmizlənəndə sərhəd yazılır: kursoru ondan kiçik klient sinxronu tam yeniləməlidir (410)
        """
        WITH d AS (DELETE FROM change_log WHERE seq IN (SELECT seq FROM change_log WHERE changed_at < now() - interval '30 days' ORDER BY seq LIMIT 5000) RETURNING xid)
        INSERT INTO sync_state(key, value)
        SELECT 'change_log_purged_through', max(xid::text::bigint) + 1 FROM d HAVING count(*) > 0
        ON CONFLICT (key) DO UPDATE SET value = greatest(sync_state.value, EXCLUDED.value)
        """,
        "DELETE FROM sync_client_changes WHERE client_change_id IN (SELECT client_change_id FROM sync_client_changes WHERE created_at < now() - interval '90 days' LIMIT 5000)",
    ];

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var tenant in await tenants.ListActiveAsync(cancellationToken))
        {
            try
            {
                await using var connection = await dataSources.Get(tenant.Schema).OpenConnectionAsync(cancellationToken);
                foreach (var sql in Statements)
                {
                    await using var command = new NpgsqlCommand(sql, connection);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, tenant.Slug, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Maintenance failed for tenant {Tenant}")]
    private static partial void LogFailed(ILogger logger, string tenant, Exception exception);
}

public sealed class TenantMaintenanceService(TenantMaintenance maintenance) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            await maintenance.RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
