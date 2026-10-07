using System.Net.Http.Headers;
using DentaCore.Audit.Application;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace DentaCore.Patient.IntegrationTests;

/// <summary>Bir PostgreSQL bazası, bir host, iki tenant, hər tenant-da iki filial. Testlər unikal data ilə bir-birinə mane olmur.</summary>
public sealed class PatientApiFixture : IAsyncLifetime, IDisposable
{
    public const string Demo = "t_demo";
    public const string Other = "t_other";

    private readonly TestKeys _keys = new();

    public PostgresFixture Db { get; } = new();

    public ApiFactory<Program>? Factory { get; private set; }

    public Guid BranchA { get; private set; }

    public Guid BranchB { get; private set; }

    public Guid OtherBranch { get; private set; }

    public Guid Staff { get; private set; }

    public Guid OtherStaff { get; private set; }

    public Guid OtherTenantStaff { get; private set; }

    public async Task InitializeAsync()
    {
        await Db.InitializeAsync();
        if (string.IsNullOrEmpty(Db.ConnectionString))
        {
            return;
        }

        BranchA = await Db.SeedBranchAsync(Demo, "A");
        BranchB = await Db.SeedBranchAsync(Demo, "B");
        OtherBranch = await Db.SeedBranchAsync(Other, "X");
        Staff = await Db.SeedUserAsync(Demo, $"staff-{Guid.NewGuid():N}@clinic.az");
        OtherStaff = await Db.SeedUserAsync(Demo, $"staff2-{Guid.NewGuid():N}@clinic.az");
        OtherTenantStaff = await Db.SeedUserAsync(Other, $"staff3-{Guid.NewGuid():N}@clinic.az");
        Factory = new ApiFactory<Program>(Db, ("Jwt__PublicKeyPem", _keys.PublicPem));
    }

    public async Task DisposeAsync()
    {
        Factory?.Dispose();
        await Db.DisposeAsync();
    }

    public void Dispose() => _keys.Dispose();

    /// <summary>İmzalı token ilə HttpClient. Tenant "demo" (və ya "other").</summary>
    public HttpClient Client(string[] permissions, Guid[]? branches = null, Guid? userId = null, string tenant = "demo")
    {
        var client = Factory!.CreateClientFor(tenant);
        var tenantId = tenant == "demo" ? Db.DemoTenantId : Db.OtherTenantId;
        var user = userId ?? (tenant == "demo" ? Staff : OtherTenantStaff);
        var token = _keys.Mint(user, tenantId, permissions, branches?.Select(b => b.ToString()));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient Anonymous() => Factory!.CreateClientFor("demo");

    public async Task<AuditChainResult> VerifyAuditChainAsync(string schema, string slug)
    {
        using var scope = Factory!.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .Set(new TenantInfo(schema == Demo ? Db.DemoTenantId : Db.OtherTenantId, slug, schema));
        return await scope.ServiceProvider.GetRequiredService<IAuditChainVerifier>().VerifyAsync(CancellationToken.None);
    }
}

public static class Perms
{
    public static readonly string[] All =
    [
        "patient:read@tenant", "patient:write@tenant", "patient:read_sensitive@tenant", "clinical:read@tenant", "clinical:write@tenant",
    ];
}
