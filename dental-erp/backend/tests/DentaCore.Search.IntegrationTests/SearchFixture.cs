using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using DentaCore.Search.Infrastructure;
using DentaCore.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DentaCore.Search.IntegrationTests;

/// <summary>REAL Elasticsearch tələb edir (DENTACORE_TEST_ES, məs. http://127.0.0.1:9200) və PostgreSQL.</summary>
public sealed class ElasticFactAttribute : FactAttribute
{
    public const string EnvVar = "DENTACORE_TEST_ES";

    public ElasticFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresFixture.EnvVar)) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
        {
            Skip = $"Set {PostgresFixture.EnvVar} and {EnvVar} (http://host:9200) to run the Elasticsearch integration tests.";
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SearchDefinition : ICollectionFixture<SearchFixture>
{
    public const string Name = "search";
}

public sealed class SearchFixture : IAsyncLifetime, IDisposable
{
    public const string Demo = "t_demo";
    public const string Other = "t_other";

    private readonly TestKeys _keys = new();
    private ServiceProvider? _indexing;

    public PostgresFixture Db { get; } = new();

    public string EsUri { get; private set; } = string.Empty;

    /// <summary>Hər işə salma öz prefiksini alır: paralel işə salmalar və köhnə qalıqlar bir-birinə toxunmur.</summary>
    public string Prefix { get; } = "t" + Guid.NewGuid().ToString("N")[..8];

    public ApiFactory<Program>? Factory { get; private set; }

    public HttpClient Es { get; private set; } = new();

    public async Task InitializeAsync()
    {
        EsUri = Environment.GetEnvironmentVariable(ElasticFactAttribute.EnvVar) ?? string.Empty;
        await Db.InitializeAsync();
        if (string.IsNullOrEmpty(Db.ConnectionString) || string.IsNullOrEmpty(EsUri))
        {
            return;
        }

        Es = new HttpClient { BaseAddress = new Uri(EsUri.TrimEnd('/') + "/") };
        Factory = new ApiFactory<Program>(Db, ("Jwt__PublicKeyPem", _keys.PublicPem), ("Elasticsearch__Uri", EsUri), ("Elasticsearch__IndexPrefix", Prefix));
        _ = Factory.Server;
        _indexing = BuildIndexing(EsUri);
    }

    public async Task DisposeAsync()
    {
        Factory?.Dispose();
        if (_indexing is not null)
        {
            await _indexing.DisposeAsync();
        }

        if (!string.IsNullOrEmpty(EsUri))
        {
            // action.destructive_requires_name: indekslər adla silinir
            using var response = await Es.GetAsync($"_cat/indices/{Prefix}-*?format=json");
            if (response.IsSuccessStatusCode)
            {
                foreach (var index in JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateArray())
                {
                    await Es.DeleteAsync(index.GetProperty("index").GetString());
                }
            }

            Es.Dispose();
        }

        await Db.DisposeAsync();
    }

    public void Dispose() => _keys.Dispose();

    /// <summary>Hosted servislər işə salınmadan indexer və consumer birbaşa çağırılır: testlər dövrün nə vaxt işləməsinə nəzarət edir.</summary>
    public ServiceProvider BuildIndexing(string esUri)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = Db.ConnectionString,
            ["Security:PiiEncryptionKey"] = Db.EncryptionKey,
            ["Security:PiiHashKey"] = Db.HashKey,
            ["Elasticsearch:Uri"] = esUri,
            ["Elasticsearch:IndexPrefix"] = Prefix,
            ["Elasticsearch:RequestTimeoutSeconds"] = "2",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMessaging(configuration);
        services.AddSearchModule(configuration);
        services.AddSearchIndexing();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Indexer change_log-un xid horizontundan oxuyur (T011) və horizont KLASTER üzrədir: paralel test layihələrinin açıq yazı tranzaksiyası onu gecikdirə bilər.
    /// Bu, düzgün davranışdır (yalnız gecikmə); testlər indeksləməyə başlamazdan əvvəl horizontun öz yazılarını keçməsini gözləyir.
    /// </summary>
    private async Task WaitForHorizonAsync()
    {
        foreach (var schema in new[] { Demo, Other })
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline
                && !await Db.ScalarAsync<bool>(schema, "SELECT pg_snapshot_xmin(pg_current_snapshot())::text::bigint > COALESCE((SELECT max(xid)::text::bigint FROM change_log), 0)"))
            {
                await Task.Delay(25);
            }
        }
    }

    public async Task IndexAsync(ServiceProvider? provider = null)
    {
        await WaitForHorizonAsync();
        var sp = provider ?? _indexing!;
        await sp.GetRequiredService<PatientIndexerService>().RunOnceAsync(CancellationToken.None);
        await sp.GetRequiredService<ElasticClient>().RefreshAsync($"{Prefix}-*", CancellationToken.None);
    }

    public static Task IndexAsyncUnrefreshed(ServiceProvider provider) => provider.GetRequiredService<PatientIndexerService>().RunOnceAsync(CancellationToken.None);

    public async Task RefreshAsync() => await _indexing!.GetRequiredService<ElasticClient>().RefreshAsync($"{Prefix}-*", CancellationToken.None);

    public async Task ConsumeAuditAsync(IntegrationEnvelope envelope)
    {
        using var scope = _indexing!.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AuditIndexConsumer>().HandleAsync(envelope, CancellationToken.None);
    }

    public HttpClient Client(string[] permissions, Guid? userId = null, Guid[]? branches = null, string tenant = "demo", ApiFactory<Program>? factory = null)
    {
        var client = (factory ?? Factory!).CreateClientFor(tenant);
        var tenantId = tenant == "demo" ? Db.DemoTenantId : Db.OtherTenantId;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _keys.Mint(userId ?? Guid.NewGuid(), tenantId, permissions, branches?.Select(b => b.ToString())));
        return client;
    }

    public string PublicPem => _keys.PublicPem;

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public Task<Guid> BranchAsync(string schema = Demo) => Db.SeedBranchAsync(schema, "B" + Guid.NewGuid().ToString("N")[..6]);

    public async Task<Guid> PatientAsync(Guid branch, string firstName, string lastName, string? fatherName = null, string schema = Demo)
    {
        var id = Guid.NewGuid();
        await Db.ExecAsync(schema, "INSERT INTO patients(id, branch_id, first_name, last_name, father_name) VALUES ($1, $2, $3, $4, $5)", id, branch, firstName, lastName, (object?)fatherName ?? DBNull.Value);
        return id;
    }

    public Task<long> ChartNoAsync(Guid patient, string schema = Demo) => Db.ScalarAsync<long>(schema, "SELECT chart_no FROM patients WHERE id = $1", patient)!;

    public static async Task<List<Guid>> SearchIdsAsync(HttpClient client, string q, Guid? branch = null, int limit = 10)
    {
        var response = await client.GetAsync($"/v1/search/patients?q={Uri.EscapeDataString(q)}&limit={limit}" + (branch is null ? string.Empty : $"&branchId={branch}"));
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.OK, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await JsonAsync(response)).EnumerateArray().Select(h => h.GetProperty("patientId").GetGuid()).ToList();
    }

    public async Task<JsonElement> EsDocAsync(string index, string id)
    {
        using var response = await Es.GetAsync($"{index}/_doc/{id}");
        return await JsonAsync(response);
    }

    public string PatientsIndex(Guid tenantId) => $"{Prefix}-patients-{tenantId:N}";
}
