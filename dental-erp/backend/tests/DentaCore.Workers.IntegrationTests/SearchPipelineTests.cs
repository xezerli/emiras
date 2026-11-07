using System.Globalization;
using System.Text.Json;
using DentaCore.TestSupport;
using static DentaCore.Workers.IntegrationTests.MessagingFixture;

namespace DentaCore.Workers.IntegrationTests;

/// <summary>Bütün zəncir REAL komponentlərlə: PostgreSQL outbox/change_log → RabbitMQ → Workers → Elasticsearch.</summary>
[Collection(MessagingDefinition.Name)]
public sealed class SearchPipelineTests(MessagingFixture fx)
{
    private ApiFactory<Program> StartHost() => new(
        fx.Db,
        ("RabbitMq__Uri", fx.AmqpUri),
        ("RabbitMq__Exchange", fx.Exchange),
        ("RabbitMq__DeadLetterExchange", fx.DeadExchange),
        ("RabbitMq__QueuePrefix", fx.QueuePrefix),
        ("RabbitMq__OutboxPollInterval", "00:00:00.100"),
        ("Elasticsearch__Uri", fx.EsOrDead),
        ("Elasticsearch__IndexPrefix", fx.Run),
        ("Elasticsearch__IndexerIntervalSeconds", "1"));

    private HttpClient Es() => new() { BaseAddress = new Uri(fx.EsUri.TrimEnd('/') + "/") };

    private static async Task<JsonElement?> GetDocAsync(HttpClient es, string index, string id)
    {
        using var response = await es.GetAsync($"{index}/_doc/{id}");
        return response.IsSuccessStatusCode ? JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone() : null;
    }

    [PipelineFact]
    public async Task Audit_outbox_row_is_published_consumed_and_lands_in_the_tenants_monthly_index()
    {
        await TestData.ResetAsync(fx.Db);
        using var host = StartHost();
        using var client = host.CreateClient();
        using var es = Es();
        var auditId = 7_000_000_000L + Random.Shared.Next(1, 1_000_000);
        var entityId = Guid.NewGuid();
        var user = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;

        await TestData.AddOutboxAsync(fx.Db, "audit.recorded", new { auditId, action = "patient.read", entity = "patient", entityId, userId = user, occurredAt = at, details = new { fields = 3 } });

        var index = $"{fx.Run}-audit-{fx.Db.DemoTenantId:N}-{at.UtcDateTime:yyyy.MM}";
        JsonElement? doc = null;
        Assert.True(await WaitAsync(async () => (doc = await GetDocAsync(es, index, auditId.ToString(CultureInfo.InvariantCulture))) is not null, 30));
        var source = doc!.Value.GetProperty("_source");
        Assert.Equal("patient.read", source.GetProperty("action").GetString());
        Assert.Equal(entityId, source.GetProperty("entityId").GetGuid());
        Assert.Equal(fx.Db.DemoTenantId.ToString("N"), source.GetProperty("tenantId").GetString());
        Assert.True(await WaitAsync(async () => await fx.Db.ScalarAsync<DateTime?>(TestData.Schema, "SELECT processed_at FROM outbox_messages WHERE payload->>'entityId' = $1", entityId.ToString()) is not null));
    }

    [PipelineFact]
    public async Task Patient_changes_flow_from_postgres_to_the_index_without_any_event()
    {
        using var host = StartHost();
        using var client = host.CreateClient();
        using var es = Es();
        var branch = await fx.Db.SeedBranchAsync(TestData.Schema);
        var id = Guid.NewGuid();
        var index = $"{fx.Run}-patients-{fx.Db.DemoTenantId:N}";

        await fx.Db.ExecAsync(TestData.Schema, "INSERT INTO patients(id, branch_id, first_name, last_name) VALUES ($1, $2, 'Boru', 'Zəncir')", id, branch);
        Assert.True(await WaitAsync(async () => await GetDocAsync(es, index, id.ToString("D")) is not null, 30));

        await fx.Db.ExecAsync(TestData.Schema, "UPDATE patients SET last_name = 'Dəyişdi' WHERE id = $1", id);
        Assert.True(await WaitAsync(async () => (await GetDocAsync(es, index, id.ToString("D")))?.GetProperty("_source").GetProperty("lastName").GetString() == "Dəyişdi", 30));

        await fx.Db.ExecAsync(TestData.Schema, "UPDATE patients SET deleted_at = now() WHERE id = $1", id);
        Assert.True(await WaitAsync(async () => await GetDocAsync(es, index, id.ToString("D")) is null, 30));
    }
}
