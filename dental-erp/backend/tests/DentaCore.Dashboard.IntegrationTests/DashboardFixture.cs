using System.Net.Http.Headers;
using System.Text.Json;
using DentaCore.TestSupport;

namespace DentaCore.Dashboard.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class DashboardDefinition : ICollectionFixture<DashboardFixture>
{
    public const string Name = "dashboard";
}

/// <summary>Real PostgreSQL üzərində ClinicCore host-u (dashboard orada işləyir). Keş söndürülüb (CacheSeconds=0): testlər faktiki hesablamanı yoxlayır.</summary>
public sealed class DashboardFixture : IAsyncLifetime, IDisposable
{
    public const string Demo = "t_demo";

    private readonly TestKeys _keys = new();

    public PostgresFixture Db { get; } = new();

    public ApiFactory<Program>? Factory { get; private set; }

    public Guid Provider1 { get; private set; }

    public Guid Provider2 { get; private set; }

    public async Task InitializeAsync()
    {
        await Db.InitializeAsync();
        if (string.IsNullOrEmpty(Db.ConnectionString))
        {
            return;
        }

        Provider1 = await Db.SeedUserAsync(Demo, $"p1-{Guid.NewGuid():N}@clinic.az", "Dr Birinci", "doctor");
        Provider2 = await Db.SeedUserAsync(Demo, $"p2-{Guid.NewGuid():N}@clinic.az", "Dr İkinci", "doctor");
        Factory = new ApiFactory<Program>(Db, ("Jwt__PublicKeyPem", _keys.PublicPem), ("Dashboard__CacheSeconds", "0"));
        _ = Factory.Server;   // host indi qurulsun: sonradan yaradılan başqa factory-nin mühit dəyişənləri bunu dəyişməsin
    }

    public async Task DisposeAsync()
    {
        Factory?.Dispose();
        await Db.DisposeAsync();
    }

    public void Dispose() => _keys.Dispose();

    public string PublicPem => _keys.PublicPem;

    public HttpClient Client(string[] permissions, Guid? userId = null, Guid[]? branches = null, string tenant = "demo", ApiFactory<Program>? factory = null)
    {
        var client = (factory ?? Factory!).CreateClientFor(tenant);
        var tenantId = tenant == "demo" ? Db.DemoTenantId : Db.OtherTenantId;
        var token = _keys.Mint(userId ?? Guid.NewGuid(), tenantId, permissions, branches?.Select(b => b.ToString()));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>Bakı vaxtı (UTC+4) → UTC. Test günləri üçün yerli saatı dəqiq vermək rahatdır.</summary>
    public static DateTime Baku(DateOnly day, int hour, int minute = 0) => DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(hour, minute)).AddHours(-4), DateTimeKind.Utc);

    public Task<Guid> BranchAsync() => Db.SeedBranchAsync(Demo, "B" + Guid.NewGuid().ToString("N")[..6]);

    public async Task<Guid> PatientAsync(Guid branch, DateTime? createdAt = null)
    {
        var id = Guid.NewGuid();
        await Db.ExecAsync(Demo, "INSERT INTO patients(id, branch_id, first_name, last_name, created_at) VALUES ($1, $2, 'Test', 'Pas', $3)", id, branch, createdAt ?? DateTime.UtcNow);
        return id;
    }

    public async Task<Guid> RoomAsync(Guid branch, string name, bool active = true)
    {
        var id = Guid.NewGuid();
        await Db.ExecAsync(Demo, "INSERT INTO rooms(id, branch_id, name, is_active) VALUES ($1, $2, $3, $4)", id, branch, name, active);
        return id;
    }

    public Task AppointmentAsync(Guid branch, Guid patient, Guid provider, DateTime startUtc, string status, Guid? room = null, int minutes = 30) =>
        Db.ExecAsync(
            Demo, "INSERT INTO appointments(branch_id, patient_id, provider_id, room_id, period, status) VALUES ($1, $2, $3, $4, tstzrange($5::timestamptz, $6::timestamptz), $7)",
            branch, patient, provider, (object?)room ?? DBNull.Value, startUtc, startUtc.AddMinutes(minutes), status);

    /// <summary>Qaralama yaradır, sətirləri əlavə edir, sonra rəsmiləşdirir (issue-dan sonra sətirlər dəyişməzdir).</summary>
    public async Task<Guid> InvoiceAsync(Guid branch, Guid patient, DateTime issuedAtUtc, decimal paidTotal, params (string Description, int Quantity, decimal Price, Guid? Provider)[] items)
    {
        var id = Guid.NewGuid();
        await Db.ExecAsync(Demo, "INSERT INTO invoices(id, kind, branch_id, patient_id, status, created_at) VALUES ($1, 'invoice', $2, $3, 'draft', $4)", id, branch, patient, issuedAtUtc);
        foreach (var (description, quantity, price, provider) in items)
        {
            await Db.ExecAsync(
                Demo, "INSERT INTO invoice_items(invoice_id, description, quantity, unit_price, provider_id) VALUES ($1, $2, $3, $4, $5)",
                id, description, quantity, price, (object?)provider ?? DBNull.Value);
        }

        var total = items.Sum(i => i.Quantity * i.Price);
        await Db.ExecAsync(
            Demo, "UPDATE invoices SET status = CASE WHEN $2 = 0 THEN 'issued' WHEN $2 >= $3 THEN 'paid' ELSE 'partially_paid' END, subtotal = $3, total = $3, paid_total = $2, issued_at = $4, due_date = $4::date WHERE id = $1",
            id, paidTotal, total, issuedAtUtc);
        return id;
    }

    public async Task<Guid> PaymentAsync(Guid invoice, Guid patient, string method, decimal amount, DateTime paidAtUtc)
    {
        var id = Guid.NewGuid();
        await Db.ExecAsync(Demo, "INSERT INTO payments(id, invoice_id, patient_id, kind, method, amount, paid_at) VALUES ($1, $2, $3, 'payment', $4, $5, $6)", id, invoice, patient, method, amount, paidAtUtc);
        return id;
    }

    public Task RefundAsync(Guid invoice, Guid patient, Guid original, decimal amount, DateTime paidAtUtc) =>
        Db.ExecAsync(
            Demo, "INSERT INTO payments(invoice_id, patient_id, kind, method, amount, paid_at, refund_of, reason) VALUES ($1, $2, 'refund', 'cash', $3, $4, $5, 'test')",
            invoice, patient, amount, paidAtUtc, original);
}
