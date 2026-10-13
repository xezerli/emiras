using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DentaCore.TestSupport;

namespace DentaCore.Sync.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class SyncDefinition : ICollectionFixture<SyncFixture>
{
    public const string Name = "sync";
}

/// <summary>Real PostgreSQL üzərində ClinicCore host-u (sync orada işləyir).</summary>
public sealed class SyncFixture : IAsyncLifetime, IDisposable
{
    public const string Demo = "t_demo";

    private readonly TestKeys _keys = new();

    public PostgresFixture Db { get; } = new();

    public ApiFactory<Program>? Factory { get; private set; }

    public async Task InitializeAsync()
    {
        await Db.InitializeAsync();
        if (!string.IsNullOrEmpty(Db.ConnectionString))
        {
            Factory = new ApiFactory<Program>(Db, ("Jwt__PublicKeyPem", _keys.PublicPem));
        }
    }

    public async Task DisposeAsync()
    {
        Factory?.Dispose();
        await Db.DisposeAsync();
    }

    public void Dispose() => _keys.Dispose();

    public HttpClient Client(string[] permissions, Guid? userId = null, Guid[]? branches = null, string tenant = "demo")
    {
        var client = Factory!.CreateClientFor(tenant);
        var tenantId = tenant == "demo" ? Db.DemoTenantId : Db.OtherTenantId;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _keys.Mint(userId ?? Guid.NewGuid(), tenantId, permissions, branches?.Select(b => b.ToString())));
        return client;
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public Task<Guid> BranchAsync() => Db.SeedBranchAsync(Demo, "B" + Guid.NewGuid().ToString("N")[..6]);

    public Task<Guid> DoctorAsync() => Db.SeedUserAsync(Demo, $"dr-{Guid.NewGuid():N}@clinic.az", "Dr Test", "doctor");

    public async Task<Guid> PatientAsync(Guid branch, string lastName = "Sync", Guid? createdBy = null)
    {
        var id = Guid.NewGuid();
        await Db.ExecAsync(
            Demo, "INSERT INTO patients(id, branch_id, first_name, last_name, created_by, phone_enc, email_enc, address, notes) VALUES ($1, $2, 'Test', $3, $4, '\\xdeadbeef'::bytea, '\\xcafe'::bytea, '{\"city\":\"Bakı\"}'::jsonb, 'gizli qeyd')",
            id, branch, lastName, (object?)createdBy ?? DBNull.Value);
        return id;
    }

    public Task AppointmentAsync(Guid branch, Guid patient, Guid provider, string status = "booked") =>
        Db.ExecAsync(
            Demo, "INSERT INTO appointments(branch_id, patient_id, provider_id, period, status) VALUES ($1, $2, $3, tstzrange(now() + interval '1 day', now() + interval '1 day 30 minutes'), $4)",
            branch, patient, provider, status);

    /// <summary>Cari son kursor: bütün gözləyən dəyişiklikləri oxuyub sona çatır. Testlər dəyişiklikdən ƏVVƏL bunu çağırıb öz pəncərəsini ayırır.</summary>
    public static async Task<long> NowAsync(HttpClient client)
    {
        long cursor = 0;
        for (var i = 0; i < 200; i++)
        {
            var page = await JsonAsync(await client.GetAsync($"/v1/sync/pull?cursor={cursor}&limit=2000"));
            cursor = page.GetProperty("cursor").GetInt64();
            if (!page.GetProperty("hasMore").GetBoolean())
            {
                return cursor;
            }
        }

        throw new InvalidOperationException("Pull did not converge.");
    }

    public static async Task<(JsonElement Page, List<JsonElement> Changes)> PullAsync(HttpClient client, long cursor, int limit = 2000)
    {
        var response = await client.GetAsync($"/v1/sync/pull?cursor={cursor}&limit={limit}");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var page = await JsonAsync(response);
        return (page, page.GetProperty("changes").EnumerateArray().ToList());
    }

    public static Task<HttpResponseMessage> PushAsync(HttpClient client, Guid device, params object[] changes) =>
        client.PostAsJsonAsync("/v1/sync/push", new { deviceId = device, changes });
}
