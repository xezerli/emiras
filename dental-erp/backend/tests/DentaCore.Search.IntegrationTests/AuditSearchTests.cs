using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using static DentaCore.Search.IntegrationTests.SearchFixture;

namespace DentaCore.Search.IntegrationTests;

[Collection(SearchDefinition.Name)]
public sealed class AuditSearchTests(SearchFixture f)
{
    private static readonly string[] Writer = ["patient:write@tenant", "patient:read@tenant"];
    private static readonly string[] AuditReader = ["audit:read@tenant"];

    /// <summary>Outbox-dakı audit.recorded sətirlərini (API əməliyyatlarının real nəticəsi) transport əvəzinə birbaşa consumer-ə verir.</summary>
    private async Task ConsumeOutboxAsync(string schema = SearchFixture.Demo, Guid? tenantId = null, string slug = "demo")
    {
        await using var connection = await f.Db.OpenTenantAsync(schema);
        await using var command = new Npgsql.NpgsqlCommand("SELECT id, payload::text, occurred_at FROM outbox_messages WHERE routing_key = 'audit.recorded' ORDER BY occurred_at", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var envelopes = new List<IntegrationEnvelope>();
        while (await reader.ReadAsync())
        {
            envelopes.Add(new IntegrationEnvelope(
                reader.GetGuid(0), "AuditRecorded", 1, tenantId ?? f.Db.DemoTenantId, slug, reader.GetFieldValue<DateTimeOffset>(2), "audit.recorded", null,
                JsonDocument.Parse(reader.GetString(1)).RootElement.Clone()));
        }

        foreach (var envelope in envelopes)
        {
            await f.ConsumeAuditAsync(envelope);
        }

        await f.RefreshAsync();
    }

    private Task<Guid> StaffAsync(string schema = SearchFixture.Demo) => f.Db.SeedUserAsync(schema, $"s-{Guid.NewGuid():N}@clinic.az");

    private static async Task<Guid> RegisterAsync(HttpClient client, Guid branch, string last)
    {
        var response = await client.PostAsJsonAsync("/v1/patients", new { branchId = branch, firstName = "Audit", lastName = last });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> AuditAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync("/v1/admin/audit?" + query);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await JsonAsync(response);
    }

    private static List<JsonElement> Items(JsonElement page) => page.GetProperty("items").EnumerateArray().ToList();

    [ElasticFact]
    public async Task Real_api_actions_become_searchable_audit_entries_filtered_by_user_entity_action_and_time()
    {
        var branch = await f.BranchAsync();
        var u1 = await StaffAsync();
        var u2 = await StaffAsync();
        using var client1 = f.Client(Writer, u1);
        using var client2 = f.Client(Writer, u2);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var p1 = await RegisterAsync(client1, branch, "Birinci" + Guid.NewGuid().ToString("N")[..5]);
        var p2 = await RegisterAsync(client1, branch, "İkinci" + Guid.NewGuid().ToString("N")[..5]);
        var p3 = await RegisterAsync(client2, branch, "Üçüncü" + Guid.NewGuid().ToString("N")[..5]);
        await ConsumeOutboxAsync();
        using var admin = f.Client(AuditReader);

        var byUser = Items(await AuditAsync(admin, $"userId={u1}"));
        Assert.Equal(2, byUser.Count);
        Assert.All(byUser, h => Assert.Equal(u1, h.GetProperty("userId").GetGuid()));
        Assert.Equal([p2, p1], byUser.Select(h => h.GetProperty("entityId").GetGuid()).ToArray());   // yenilər əvvəl

        Assert.Single(Items(await AuditAsync(admin, $"userId={u2}")));
        var byEntityId = Items(await AuditAsync(admin, $"entityId={p3}"));
        Assert.Equal(p3, Assert.Single(byEntityId).GetProperty("entityId").GetGuid());
        Assert.Equal("patient", byEntityId[0].GetProperty("entity").GetString());
        Assert.NotEmpty(Items(await AuditAsync(admin, $"userId={u1}&action={byUser[0].GetProperty("action").GetString()}&entity=patient")));
        Assert.Empty(Items(await AuditAsync(admin, $"userId={u1}&action=no.such.action")));
        var registerAction = byUser[0].GetProperty("action").GetString();
        var inWindow = Items(await AuditAsync(admin, $"from={Uri.EscapeDataString(before.ToString("O", CultureInfo.InvariantCulture))}&entity=patient&action={registerAction}&limit=100"));
        Assert.Equal(3, inWindow.Count(h => new[] { u1, u2 }.Contains(h.GetProperty("userId").GetGuid())));
        Assert.Empty(Items(await AuditAsync(admin, $"userId={u1}&to={Uri.EscapeDataString(before.AddMinutes(-5).ToString("O", CultureInfo.InvariantCulture))}")));
    }

    [ElasticFact]
    public async Task Hits_expose_only_who_what_and_when_never_the_details()
    {
        var branch = await f.BranchAsync();
        var user = await StaffAsync();
        using var client = f.Client(Writer, user);
        await RegisterAsync(client, branch, "Mexfi" + Guid.NewGuid().ToString("N")[..5]);
        await ConsumeOutboxAsync();
        using var admin = f.Client(AuditReader);

        var hit = Items(await AuditAsync(admin, $"userId={user}"))[0];

        Assert.Equal("action,auditId,entity,entityId,occurredAt,userId", string.Join(",", hit.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
    }

    [ElasticFact]
    public async Task Paging_walks_every_entry_newest_first_exactly_once()
    {
        var branch = await f.BranchAsync();
        var user = await StaffAsync();
        using var client = f.Client(Writer, user);
        var expected = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            expected.Add(await RegisterAsync(client, branch, "Sehife" + i + Guid.NewGuid().ToString("N")[..4]));
        }

        await ConsumeOutboxAsync();
        using var admin = f.Client(AuditReader);

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await AuditAsync(admin, $"userId={user}&limit=2" + (cursor is null ? string.Empty : $"&cursor={cursor}"));
            seen.AddRange(Items(page).Select(h => h.GetProperty("entityId").GetGuid()));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(expected.AsEnumerable().Reverse(), seen);   // 5 giriş, təkrarsız, tərs xronoloji
    }

    [ElasticFact]
    public async Task Redelivery_is_idempotent_and_an_existing_entry_is_never_rewritten()
    {
        var auditId = 9_000_000_000L + Random.Shared.Next(1, 1_000_000);
        var entityId = Guid.NewGuid();
        var user = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        IntegrationEnvelope Make(string action) => new(
            Guid.NewGuid(), "AuditRecorded", 1, f.Db.DemoTenantId, "demo", at, "audit.recorded", null,
            JsonSerializer.SerializeToElement(new { auditId, action, entity = "patient", entityId, userId = user, occurredAt = at, details = new { x = 1 } }, IntegrationEnvelope.Json));

        await f.ConsumeAuditAsync(Make("patient.read"));
        await f.ConsumeAuditAsync(Make("patient.read"));            // təkrar çatdırılma
        await f.ConsumeAuditAsync(Make("patient.delete"));          // eyni auditId, başqa məzmun: audit yenidən yazıla bilməz
        await f.RefreshAsync();
        using var admin = f.Client(AuditReader);

        var hits = Items(await AuditAsync(admin, $"entityId={entityId}"));

        Assert.Equal("patient.read", Assert.Single(hits).GetProperty("action").GetString());
    }

    [ElasticFact]
    public async Task Entries_are_split_into_monthly_indexes_and_searched_across_all_of_them()
    {
        var entityId = Guid.NewGuid();
        var user = Guid.NewGuid();
        var jan = new DateTimeOffset(2021, 1, 15, 10, 0, 0, TimeSpan.Zero);
        var feb = new DateTimeOffset(2021, 2, 15, 10, 0, 0, TimeSpan.Zero);
        long id = 8_000_000_000L + Random.Shared.Next(1, 1_000_000);
        foreach (var (at, n) in new[] { (jan, 0), (feb, 1) })
        {
            await f.ConsumeAuditAsync(new IntegrationEnvelope(
                Guid.NewGuid(), "AuditRecorded", 1, f.Db.DemoTenantId, "demo", at, "audit.recorded", null,
                JsonSerializer.SerializeToElement(new { auditId = id + n, action = "patient.read", entity = "patient", entityId, userId = user, occurredAt = at }, IntegrationEnvelope.Json)));
        }

        await f.RefreshAsync();
        using var admin = f.Client(AuditReader);

        var hits = Items(await AuditAsync(admin, $"entityId={entityId}"));

        Assert.Equal([id + 1, id], hits.Select(h => h.GetProperty("auditId").GetInt64()).ToArray());
        Assert.Equal(HttpStatusCode.OK, (await f.Es.GetAsync($"{f.Prefix}-audit-{f.Db.DemoTenantId:N}-2021.01")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Es.GetAsync($"{f.Prefix}-audit-{f.Db.DemoTenantId:N}-2021.02")).StatusCode);
        Assert.Single(Items(await AuditAsync(admin, $"entityId={entityId}&from=2021-02-01T00:00:00Z")));
    }

    [ElasticFact]
    public async Task Audit_search_is_tenant_scoped_only_and_never_crosses_tenants()
    {
        var demoBranch = await f.BranchAsync();
        var otherBranch = await f.BranchAsync(SearchFixture.Other);
        var demoUser = await StaffAsync();
        var otherUser = await StaffAsync(SearchFixture.Other);
        using var demoWriter = f.Client(Writer, demoUser);
        using var otherWriter = f.Client(Writer, otherUser, tenant: "other");
        var demoPatient = await RegisterAsync(demoWriter, demoBranch, "DemoA" + Guid.NewGuid().ToString("N")[..4]);
        var otherPatient = await RegisterAsync(otherWriter, otherBranch, "OtherA" + Guid.NewGuid().ToString("N")[..4]);
        await ConsumeOutboxAsync();
        await ConsumeOutboxAsync(SearchFixture.Other, f.Db.OtherTenantId, "other");
        using var demoAdmin = f.Client(AuditReader);
        using var otherAdmin = f.Client(AuditReader, tenant: "other");
        using var branchAdmin = f.Client(["audit:read@branch"], branches: [demoBranch]);
        using var nobody = f.Client(["patient:read@tenant"]);

        Assert.Equal(demoPatient, Assert.Single(Items(await AuditAsync(demoAdmin, $"userId={demoUser}"))).GetProperty("entityId").GetGuid());
        Assert.Empty(Items(await AuditAsync(demoAdmin, $"userId={otherUser}")));                   // başqa tenant-ın istifadəçisi görünmür
        Assert.Empty(Items(await AuditAsync(demoAdmin, $"entityId={otherPatient}")));
        Assert.Equal(otherPatient, Assert.Single(Items(await AuditAsync(otherAdmin, $"userId={otherUser}"))).GetProperty("entityId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await branchAdmin.GetAsync("/v1/admin/audit")).StatusCode);   // audit filial üzrə bölünmür
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/v1/admin/audit")).StatusCode);
        using var anonymous = f.Factory!.CreateClientFor("demo");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/admin/audit")).StatusCode);
    }

    [ElasticFact]
    public async Task Bad_filters_are_refused_and_reading_the_audit_is_itself_audited()
    {
        var admin = Guid.NewGuid();
        using var client = f.Client(AuditReader, admin);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/v1/admin/audit?limit=101")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/v1/admin/audit?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/v1/admin/audit?cursor=not-a-cursor")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/v1/admin/audit?from=2024-02-01T00:00:00Z&to=2024-01-01T00:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1/admin/audit?userId=nope")).StatusCode);

        await AuditAsync(client, "limit=1");
        Assert.True(await f.Db.ScalarAsync<long>(SearchFixture.Demo, "SELECT count(*) FROM audit_log WHERE action = 'audit.search' AND user_id = $1", admin) >= 1);
        Assert.Contains("no-store", (await client.GetAsync("/v1/admin/audit?limit=1")).Headers.CacheControl?.ToString());
    }
}
