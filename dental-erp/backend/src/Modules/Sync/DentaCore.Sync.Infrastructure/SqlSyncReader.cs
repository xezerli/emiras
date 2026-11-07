using System.Text.Json;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.Sync.Application;
using Npgsql;
using NpgsqlTypes;

namespace DentaCore.Sync.Infrastructure;

/// <summary>
/// Pull: <c>change_log</c>-dan yalnız TRANZAKSİYASI BİTMİŞ dəyişiklikləri oxuyur (<c>xid &lt; pg_snapshot_xmin</c>), kursor xid sərhədidir.
/// seq commit sırasını əks etdirmir; seq ilə kursor götürülsəydi, kiçik seq-i alıb gec commit edən tranzaksiya klient üçün həmişəlik itərdi.
/// Hər sətir üçün CARİ vəziyyət istifadəçinin scope-u ilə yenidən oxunur: jurnaldan köhnə məzmun deyil, hazırkı məlumat və icazə tətbiq olunur.
/// </summary>
internal sealed class SqlSyncReader(ITenantDataSources dataSources, ITenantContext tenant, ICurrentUser user) : ISyncReader
{
    private const int MaxOversizedGroup = 10_000;

    public IReadOnlyList<string> Entities { get; } = SyncEntities.All.Select(e => e.Name).ToList();

    private sealed record LogRow(long Seq, string Entity, Guid EntityId, string Op, long Xid);

    public async Task<Result<SyncPullDto>> PullAsync(long cursor, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSources.Get(tenant.Schema).OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);

        long horizon;
        await using (var command = new NpgsqlCommand("SELECT pg_snapshot_xmin(pg_current_snapshot())::text::bigint", connection, transaction))
        {
            horizon = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        long purged;
        await using (var command = new NpgsqlCommand("SELECT COALESCE((SELECT value FROM sync_state WHERE key = 'change_log_purged_through'), 0)", connection, transaction))
        {
            purged = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        if (cursor < purged)
        {
            return Error.Gone("sync.resync_required", "The cursor is older than the retained change log. Take a snapshot and start over.");
        }

        if (cursor > horizon)
        {
            return Error.Gone("sync.resync_required", "The cursor is ahead of this server (restored backup or another environment). Take a snapshot and start over.");
        }

        var allowed = SyncEntities.All.Where(e => ScopeOf(e) is not null).Select(e => e.Name).ToArray();
        var rows = allowed.Length == 0 ? [] : await ReadLogAsync(connection, transaction, cursor, horizon, allowed, limit + 1, null, cancellationToken);

        var newCursor = horizon;
        var hasMore = false;
        if (rows.Count > limit)
        {
            hasMore = true;
            var cutXid = rows[limit].Xid;
            var page = rows.Where(r => r.Xid < cutXid).ToList();
            if (page.Count > 0)
            {
                rows = page;
                newCursor = cutXid;   // cutXid-in sətirləri növbəti səhifədədir
            }
            else
            {
                // Tək tranzaksiya limitdən çox sətir yazıb: bölmək olmaz (kursor xid dəqiqliyindədir), tam qaytarılır
                rows = await ReadLogAsync(connection, transaction, cutXid, cutXid + 1, allowed, MaxOversizedGroup, cutXid, cancellationToken);
                newCursor = cutXid + 1;
            }
        }

        var latest = rows.GroupBy(r => (r.Entity, r.EntityId)).Select(g => g.OrderBy(r => r.Xid).ThenBy(r => r.Seq).Last()).OrderBy(r => r.Xid).ThenBy(r => r.Seq).ToList();
        var changes = new List<SyncChangeDto>(latest.Count);
        foreach (var entityGroup in latest.GroupBy(r => r.Entity))
        {
            var entity = SyncEntities.All.Single(e => e.Name == entityGroup.Key);
            var ids = entityGroup.Select(r => r.EntityId).ToArray();
            var current = await LoadAsync(connection, transaction, entity, ids, cancellationToken);
            var alive = await AliveAsync(connection, transaction, entity, ids, cancellationToken);
            foreach (var row in entityGroup)
            {
                if (current.TryGetValue(row.EntityId, out var found))
                {
                    changes.Add(new SyncChangeDto(row.Seq, row.Entity, row.EntityId, row.Op == "I" ? "I" : "U", found.RowVersion, found.Data));
                }
                else if (!alive.Contains(row.EntityId))
                {
                    changes.Add(new SyncChangeDto(row.Seq, row.Entity, row.EntityId, "D", null, null));   // silinib: nişan (tombstone)
                }

                // mövcuddur, amma bu istifadəçinin scope-undan kənardadır: heç nə göndərilmir
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new SyncPullDto(changes.OrderBy(c => c.Seq).ToList(), newCursor, hasMore);
    }

    public async Task<Result<SnapshotDto>> SnapshotAsync(string entityName, Guid? after, int limit, CancellationToken cancellationToken)
    {
        var entity = SyncEntities.All.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return Error.Validation("sync.unknown_entity", $"Unknown entity. Available: {string.Join(", ", Entities)}.");
        }

        if (ScopeOf(entity) is null)
        {
            return Error.Forbidden("sync.entity_forbidden", "You are not allowed to sync this entity.");
        }

        await using var connection = await dataSources.Get(tenant.Schema).OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        long horizon;
        await using (var command = new NpgsqlCommand("SELECT pg_snapshot_xmin(pg_current_snapshot())::text::bigint", connection, transaction))
        {
            horizon = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        var (scopeSql, parameters) = Scope(entity);
        var sql = $"SELECT t.id, t.row_version, ({entity.Json})::text FROM {entity.Name} t {entity.Join} " +
                  $"WHERE {entity.Alive} AND {entity.Visible} AND ({scopeSql}) AND (@after::uuid IS NULL OR t.id > @after) ORDER BY t.id LIMIT @lim";
        await using var select = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            select.Parameters.Add(parameter);
        }

        select.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Uuid) { Value = (object?)after ?? DBNull.Value });
        select.Parameters.Add(new NpgsqlParameter("lim", NpgsqlDbType.Integer) { Value = limit + 1 });
        var items = new List<SnapshotItemDto>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new SnapshotItemDto(reader.GetGuid(0), reader.GetInt32(1), Parse(reader.GetString(2))));
            }
        }

        Guid? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            next = items[^1].EntityId;
        }

        await transaction.CommitAsync(cancellationToken);
        return new SnapshotDto(entity.Name, items, next, horizon);
    }

    private static async Task<List<LogRow>> ReadLogAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long fromXid, long toXidExclusive, string[] entities, int lim, long? onlyXid, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT seq, entity, entity_id, op, xid::text::bigint
            FROM change_log
            WHERE xid >= @from::text::xid8 AND xid < @to::text::xid8 AND entity = ANY(@entities) AND (@only::bigint IS NULL OR xid = @only::text::xid8)
            ORDER BY xid, seq LIMIT @lim
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Text) { Value = fromXid.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Text) { Value = toXidExclusive.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        command.Parameters.Add(new NpgsqlParameter("entities", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = entities });
        command.Parameters.Add(new NpgsqlParameter("only", NpgsqlDbType.Bigint) { Value = (object?)onlyXid ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("lim", NpgsqlDbType.Integer) { Value = lim });
        var rows = new List<LogRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LogRow(reader.GetInt64(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3), reader.GetInt64(4)));
        }

        return rows;
    }

    private async Task<Dictionary<Guid, (int RowVersion, JsonElement Data)>> LoadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, SyncEntity entity, Guid[] ids, CancellationToken cancellationToken)
    {
        var (scopeSql, parameters) = Scope(entity);
        var sql = $"SELECT t.id, t.row_version, ({entity.Json})::text FROM {entity.Name} t {entity.Join} WHERE t.id = ANY(@ids) AND {entity.Alive} AND {entity.Visible} AND ({scopeSql})";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
        var result = new Dictionary<Guid, (int, JsonElement)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetGuid(0)] = (reader.GetInt32(1), Parse(reader.GetString(2)));
        }

        return result;
    }

    /// <summary>Scope-dan asılı olmayaraq silinməyib: "scope xaricindədir" ilə "silinib"-i ayırmaq üçün.</summary>
    private static async Task<HashSet<Guid>> AliveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SyncEntity entity, Guid[] ids, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT t.id FROM {entity.Name} t WHERE t.id = ANY(@ids) AND {entity.Alive}", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
        var result = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetGuid(0));
        }

        return result;
    }

    private PermissionScope? ScopeOf(SyncEntity entity) => entity.Permissions.Select(p => user.ScopeOf(p)).Where(s => s is not null).Max();

    /// <summary>İstifadəçinin bu obyekt üçün scope predikatı (alias t / p) və parametrləri.</summary>
    private (string Sql, List<NpgsqlParameter> Parameters) Scope(SyncEntity entity)
    {
        var parameters = new List<NpgsqlParameter> { new("me", NpgsqlDbType.Uuid) { Value = user.UserId } };
        var best = entity.Permissions.Select(p => (Permission: p, Scope: user.ScopeOf(p))).Where(x => x.Scope is not null).OrderByDescending(x => x.Scope).First();
        switch (best.Scope)
        {
            case PermissionScope.Tenant:
                return ("true", parameters);
            case PermissionScope.Branch:
                var branches = user.BranchFilterFor(best.Permission);
                if (branches is null || entity.BranchExpr is null)
                {
                    return ("true", parameters);
                }

                parameters.Add(new NpgsqlParameter("branches", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = branches.ToArray() });
                return ($"{entity.BranchExpr} = ANY(@branches)", parameters);
            default:
                return (entity.OwnExpr, parameters);
        }
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
