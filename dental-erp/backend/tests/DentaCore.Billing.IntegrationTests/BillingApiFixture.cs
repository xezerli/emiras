using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DentaCore.Audit.Application;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace DentaCore.Billing.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class BillingApiDefinition : ICollectionFixture<BillingApiFixture>
{
    public const string Name = "billing-api";
}

/// <summary>Bir baza, bir host, iki tenant. Hər test öz pasiyenti, fakturası və kassirini yaradır, ona görə testlər bir-birinə mane olmur.</summary>
public sealed class BillingApiFixture : IAsyncLifetime, IDisposable
{
    public const string Demo = "t_demo";

    private readonly TestKeys _keys = new();

    public PostgresFixture Db { get; } = new();

    public ApiFactory<Program>? Factory { get; private set; }

    public Guid BranchA { get; private set; }

    public Guid BranchB { get; private set; }

    public async Task InitializeAsync()
    {
        await Db.InitializeAsync();
        if (string.IsNullOrEmpty(Db.ConnectionString))
        {
            return;
        }

        BranchA = await Db.SeedBranchAsync(Demo, "A");
        BranchB = await Db.SeedBranchAsync(Demo, "B");
        await Db.ExecAsync(Demo, "INSERT INTO roles(code, name) VALUES ('limited_refund', 'Limitli')");
        await Db.ExecAsync(
            Demo, "INSERT INTO role_permissions(role_id, permission_code, scope, max_amount) SELECT id, 'invoice:refund', 'branch', 50 FROM roles WHERE code = 'limited_refund'");
        Factory = new ApiFactory<Program>(Db, ("Jwt__PublicKeyPem", _keys.PublicPem));
    }

    public async Task DisposeAsync()
    {
        Factory?.Dispose();
        await Db.DisposeAsync();
    }

    public void Dispose() => _keys.Dispose();

    public HttpClient Client(string[] permissions, Guid userId, Guid[]? branches = null, string tenant = "demo")
    {
        var client = Factory!.CreateClientFor(tenant);
        var tenantId = tenant == "demo" ? Db.DemoTenantId : Db.OtherTenantId;
        var token = _keys.Mint(userId, tenantId, permissions, branches?.Select(b => b.ToString()));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Kassir: DB-də 'cashier' rolu (refund limiti 0), tokendə filial scope-lu icazələr.</summary>
    public async Task<(HttpClient Client, Guid Id)> CashierAsync(Guid branch, string role = "cashier", string scope = "branch")
    {
        var id = await Db.SeedUserAsync(Demo, $"cashier-{Guid.NewGuid():N}@clinic.az", "Kassir", role);
        string[] perms = [$"invoice:read@{scope}", $"invoice:write@{scope}", $"payment:write@{scope}", $"invoice:refund@{scope}"];
        return (Client(perms, id, [branch]), id);
    }

    public async Task<Guid> SeedPatientAsync(Guid branch)
    {
        var id = Guid.NewGuid();
        await Db.ExecAsync(Demo, "INSERT INTO patients(id, branch_id, first_name, last_name) VALUES ($1, $2, 'Test', $3)", id, branch, "Pas" + id.ToString("N")[..8]);
        return id;
    }

    public async Task<Guid> SeedServiceAsync(decimal price, decimal vat = 0m, string? procedureCode = null, bool active = true)
    {
        var id = Guid.NewGuid();
        await Db.ExecAsync(
            Demo, "INSERT INTO services(id, code, name, price, vat_rate, procedure_code, is_active) VALUES ($1, $2, $3, $4, $5, $6, $7)",
            id, "S" + id.ToString("N")[..10], "Xidmət " + id.ToString("N")[..4], price, vat, (object?)procedureCode ?? DBNull.Value, active);
        return id;
    }

    public async Task<AuditChainResult> VerifyAuditChainAsync()
    {
        using var scope = Factory!.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(new TenantInfo(Db.DemoTenantId, "demo", Demo));
        return await scope.ServiceProvider.GetRequiredService<IAuditChainVerifier>().VerifyAsync(CancellationToken.None);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public static Task<HttpResponseMessage> PayAsync(HttpClient client, Guid invoice, string method, decimal amount, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/invoices/{invoice}/payments") { Content = JsonContent.Create(new { method, amount }) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> RefundAsync(HttpClient client, Guid payment, decimal amount, string? key = null, string reason = "Səhv ödəniş")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/payments/{payment}/refund") { Content = JsonContent.Create(new { amount, reason }) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return client.SendAsync(request);
    }

    /// <summary>Verilmiş məbləğdə rəsmiləşdirilmiş faktura (1 xidmətsiz sətir).</summary>
    public static async Task<Guid> IssuedInvoiceAsync(HttpClient client, Guid patient, Guid branch, decimal price, int quantity = 1)
    {
        var created = await client.PostAsJsonAsync("/v1/invoices", new
        {
            kind = "invoice", patientId = patient, branchId = branch,
            items = new[] { new { description = "Müalicə", quantity, unitPrice = price } },
        });
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var issued = await client.PostAsync($"/v1/invoices/{id}/issue", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, issued.StatusCode);
        return id;
    }

    public static async Task OpenShiftAsync(HttpClient client, Guid branch, decimal opening = 0m)
    {
        var response = await client.PostAsJsonAsync("/v1/cash-shifts/open", new { branchId = branch, openingCash = opening });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
    }
}
