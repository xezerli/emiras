using System.Net;
using DentaCore.TestSupport;
using StackExchange.Redis;
using static DentaCore.Dashboard.IntegrationTests.DashboardFixture;

namespace DentaCore.Dashboard.IntegrationTests;

public sealed class RedisFactAttribute : FactAttribute
{
    public const string EnvVar = "DENTACORE_TEST_REDIS";

    public RedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresFixture.EnvVar)) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
        {
            Skip = $"Set {PostgresFixture.EnvVar} and {EnvVar} (host:port) to run the Redis cache tests.";
        }
    }
}

[Collection(DashboardDefinition.Name)]
public sealed class CacheTests(DashboardFixture f)
{
    private static readonly string[] Tenant = ["report:read@tenant"];

    private ApiFactory<Program> Cached(params (string Key, string Value)[] extra) =>
        new(f.Db, [("Jwt__PublicKeyPem", f.PublicPem), ("Dashboard__CacheSeconds", "30"), .. extra]);

    private static async Task<decimal> RevenueAsync(HttpClient client, DateOnly day, Guid branch)
    {
        var response = await client.GetAsync($"/v1/dashboard/summary?date={day:yyyy-MM-dd}&branchId={branch}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("revenueToday").GetDecimal();
    }

    private async Task<(Guid Branch, Guid Invoice, Guid Patient)> SeedAsync(DateOnly day, decimal paid)
    {
        var branch = await f.BranchAsync();
        var patient = await f.PatientAsync(branch);
        var invoice = await f.InvoiceAsync(branch, patient, Baku(day, 9), 0m, ("X", 1, 1000m, null));
        await f.PaymentAsync(invoice, patient, "card", paid, Baku(day, 10));
        return (branch, invoice, patient);
    }

    [IntegrationFact]
    public async Task Repeated_requests_inside_the_ttl_are_served_from_the_cache_but_a_different_branch_or_tenant_is_not()
    {
        var day = new DateOnly(2020, 3, 10);
        var (branch, invoice, patient) = await SeedAsync(day, 100m);
        var (otherBranch, _, _) = await SeedAsync(day, 7m);
        using var factory = Cached();
        using var client = f.Client(Tenant, factory: factory);

        Assert.Equal(100m, await RevenueAsync(client, day, branch));
        await f.PaymentAsync(invoice, patient, "card", 50m, Baku(day, 11));

        Assert.Equal(100m, await RevenueAsync(client, day, branch));        // keşdən: yeni ödəniş hələ görünmür
        Assert.Equal(7m, await RevenueAsync(client, day, otherBranch));     // başqa filial başqa açardır
        using var otherTenant = f.Client(Tenant, tenant: "other", factory: factory);
        Assert.Equal(0m, await RevenueAsync(otherTenant, day, branch));     // eyni sorğu, başqa tenant: demo-nun keşi verilmir

        using var fresh = f.Client(Tenant);   // keşsiz əsas host DB-dən oxuyur və yeni ödənişi görür
        Assert.Equal(150m, await RevenueAsync(fresh, day, branch));
    }

    [IntegrationFact]
    public async Task Cache_entries_expire()
    {
        var day = new DateOnly(2020, 4, 10);
        var (branch, invoice, patient) = await SeedAsync(day, 100m);
        using var factory = Cached(("Dashboard__CacheSeconds", "1"));
        using var client = f.Client(Tenant, factory: factory);

        Assert.Equal(100m, await RevenueAsync(client, day, branch));
        await f.PaymentAsync(invoice, patient, "card", 25m, Baku(day, 11));
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        Assert.Equal(125m, await RevenueAsync(client, day, branch));
    }

    [RedisFact]
    public async Task Redis_holds_the_entry_with_a_ttl_under_a_tenant_and_branch_scoped_key()
    {
        var redisAddress = Environment.GetEnvironmentVariable(RedisFactAttribute.EnvVar)!;
        var day = new DateOnly(2019, 3, 10);
        var (branch, invoice, patient) = await SeedAsync(day, 100m);
        using var factory = Cached(("ConnectionStrings__Redis", redisAddress));
        using var client = f.Client(Tenant, factory: factory);

        Assert.Equal(100m, await RevenueAsync(client, day, branch));
        await f.PaymentAsync(invoice, patient, "card", 50m, Baku(day, 11));
        Assert.Equal(100m, await RevenueAsync(client, day, branch));   // Redis-dən

        await using var redis = await ConnectionMultiplexer.ConnectAsync(redisAddress);
        var key = $"dentacore:dash:{DashboardFixture.Demo}:{day:yyyy-MM-dd}:{branch:N}";
        var db = redis.GetDatabase();
        Assert.True(await db.KeyExistsAsync(key));
        var ttl = await db.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value.TotalSeconds, 1, 30);
    }

    [IntegrationFact]
    public async Task Unreachable_redis_degrades_to_direct_reads_instead_of_failing()
    {
        var day = new DateOnly(2019, 4, 10);
        var (branch, _, _) = await SeedAsync(day, 100m);
        using var factory = Cached(("ConnectionStrings__Redis", "127.0.0.1:1,abortConnect=false,connectTimeout=300,syncTimeout=300,asyncTimeout=300"));
        using var client = f.Client(Tenant, factory: factory);

        Assert.Equal(100m, await RevenueAsync(client, day, branch));
        Assert.Equal(100m, await RevenueAsync(client, day, branch));
    }
}
