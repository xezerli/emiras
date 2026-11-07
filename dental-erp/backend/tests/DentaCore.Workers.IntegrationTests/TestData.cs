using System.Text;
using System.Text.Json;
using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using DentaCore.TestSupport;
using Npgsql;
using NpgsqlTypes;
using RabbitMQ.Client;

namespace DentaCore.Workers.IntegrationTests;

internal static class TestData
{
    public const string Schema = "t_demo";

    public static async Task ResetAsync(PostgresFixture db) =>
        await db.ExecAsync(Schema, "DELETE FROM outbox_messages; DELETE FROM inbox_messages");

    public static async Task<Guid> AddOutboxAsync(PostgresFixture db, string routingKey, object payload, Guid? id = null)
    {
        var messageId = id ?? Guid.NewGuid();
        await using var conn = await db.OpenTenantAsync(Schema);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO outbox_messages(id, type, routing_key, payload) VALUES ($1, $2, $3, $4)", conn);
        cmd.Parameters.AddWithValue(messageId);
        cmd.Parameters.AddWithValue("TestEvent");
        cmd.Parameters.AddWithValue(routingKey);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(payload, IntegrationEnvelope.Json) });
        await cmd.ExecuteNonQueryAsync();
        return messageId;
    }

    public static async Task<Guid> AddPatientAsync(PostgresFixture db)
    {
        var branch = await db.SeedBranchAsync(Schema);
        var id = Guid.NewGuid();
        await db.ExecAsync(Schema, "INSERT INTO patients(id, branch_id, first_name, last_name) VALUES ($1, $2, 'Test', 'Pasiyent')", id, branch);
        return id;
    }

    public static IntegrationEnvelope Envelope(Guid tenantId, string routingKey, object payload, Guid? id = null, string tenantSlug = "demo") =>
        new(id ?? Guid.NewGuid(), "TestEvent", 1, tenantId, tenantSlug, DateTimeOffset.UtcNow, routingKey, null,
            JsonSerializer.SerializeToElement(payload, IntegrationEnvelope.Json));

    public static async Task<IChannel> OpenChannelAsync(MessagingFixture fx)
    {
        var connection = await new ConnectionFactory { Uri = new Uri(fx.AmqpUri) }.CreateConnectionAsync();
        return await connection.CreateChannelAsync();
    }

    /// <summary>Test növbəsi elan edib exchange-ə bağlayır (publisher-dən əvvəl elan olunmalıdır, yoxsa mesaj itir).</summary>
    public static async Task BindProbeAsync(IChannel channel, MessagingFixture fx, string queue, string routingKey)
    {
        await channel.ExchangeDeclareAsync(fx.Exchange, ExchangeType.Topic, durable: true);
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: true);
        await channel.QueueBindAsync(queue, fx.Exchange, routingKey);
    }

    public static async Task<(BasicGetResult Result, string Body)?> GetAsync(IChannel channel, string queue, int timeoutSeconds = 20)
    {
        BasicGetResult? result = null;
        await MessagingFixture.WaitAsync(async () =>
        {
            result = await channel.BasicGetAsync(queue, autoAck: true);
            return result is not null;
        }, timeoutSeconds);
        return result is null ? null : (result, Encoding.UTF8.GetString(result.Body.Span));
    }
}
