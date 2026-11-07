using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.Sync.Application;
using Npgsql;
using NpgsqlTypes;

namespace DentaCore.Sync.Infrastructure;

internal sealed class SqlClientChangeStore(ITenantDataSources dataSources, ITenantContext tenant) : ISyncClientChangeStore
{
    public async Task<StoredChange?> FindAsync(Guid clientChangeId, CancellationToken cancellationToken)
    {
        await using var command = dataSources.Get(tenant.Schema).CreateCommand("SELECT user_id, outcome, response::text FROM sync_client_changes WHERE client_change_id = $1");
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = clientChangeId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new StoredChange(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    public async Task SaveAsync(Guid clientChangeId, Guid deviceId, Guid userId, string entity, Guid entityId, string outcome, string responseJson, CancellationToken cancellationToken)
    {
        await using var command = dataSources.Get(tenant.Schema).CreateCommand(
            "INSERT INTO sync_client_changes(client_change_id, device_id, user_id, entity, entity_id, outcome, response) VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT DO NOTHING");
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = clientChangeId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = deviceId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = entity });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = entityId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = outcome });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = responseJson });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
