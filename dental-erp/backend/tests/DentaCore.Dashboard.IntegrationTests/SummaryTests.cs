using System.Net;
using System.Text.Json;
using DentaCore.TestSupport;
using static DentaCore.Dashboard.IntegrationTests.DashboardFixture;

namespace DentaCore.Dashboard.IntegrationTests;

[Collection(DashboardDefinition.Name)]
public sealed class SummaryTests(DashboardFixture f)
{
    private static readonly string[] Tenant = ["report:read@tenant"];

    private static async Task<JsonElement> GetAsync(HttpClient client, DateOnly day, Guid? branch = null)
    {
        var url = $"/v1/dashboard/summary?date={day:yyyy-MM-dd}" + (branch is null ? string.Empty : $"&branchId={branch}");
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    // ---------------- Numbers ----------------
    [IntegrationFact]
    public async Task Summary_computes_every_figure_from_the_clinics_data()
    {
        var day = new DateOnly(2024, 3, 10);
        var branch = await f.BranchAsync();
        var patient = await f.PatientAsync(branch, Baku(day.AddDays(-30), 12));
        var room1 = await f.RoomAsync(branch, "Otaq 1");
        await f.RoomAsync(branch, "Otaq 2");
        await f.RoomAsync(branch, "Köhnə otaq", active: false);

        // Randevular: bu gün 7 (biri ləğv), dünən və sabah 1-i
        foreach (var (hour, status) in new[] { (9, "booked"), (10, "completed"), (11, "completed"), (12, "no_show"), (13, "checked_in"), (14, "cancelled") })
        {
            await f.AppointmentAsync(branch, patient, f.Provider1, Baku(day, hour), status);
        }

        await f.AppointmentAsync(branch, patient, f.Provider1, Baku(day, 15), "in_progress", room1);
        await f.AppointmentAsync(branch, patient, f.Provider1, Baku(day.AddDays(-1), 10), "completed");
        await f.AppointmentAsync(branch, patient, f.Provider1, Baku(day.AddDays(1), 10), "booked");

        // Yeni pasiyentlər: bu gün 2, dünən 1
        await f.PatientAsync(branch, Baku(day, 9));
        await f.PatientAsync(branch, Baku(day, 16));
        await f.PatientAsync(branch, Baku(day.AddDays(-1), 16));

        // Faktura 1 (bu gün): Plomb 2x50 (Dr1) + Təmizləmə 40 (Dr2) = 140; nağd 100, kart 20, nağd geri qaytarma 30 → ödənilmiş 90
        var inv1 = await f.InvoiceAsync(branch, patient, Baku(day, 10), 90m, ("Plomb", 2, 50m, f.Provider1), ("Təmizləmə", 1, 40m, f.Provider2));
        var cash = await f.PaymentAsync(inv1, patient, "cash", 100m, Baku(day, 10, 30));
        await f.PaymentAsync(inv1, patient, "card", 20m, Baku(day, 11));
        await f.RefundAsync(inv1, patient, cash, 30m, Baku(day, 12));
        // Faktura 2 (bu ay, 5 gün əvvəl): Plomb 60, tam ödənilib
        var inv2 = await f.InvoiceAsync(branch, patient, Baku(day.AddDays(-5), 10), 60m, ("Plomb", 1, 60m, f.Provider1));
        await f.PaymentAsync(inv2, patient, "cash", 60m, Baku(day.AddDays(-5), 10, 30));
        // Faktura 3 (keçən ay): İmplant 1000, 200 ödənilib → borc 800; keçən ayın ödənişi aylıq gəlirə düşmür
        var inv3 = await f.InvoiceAsync(branch, patient, Baku(new DateOnly(2024, 2, 20), 10), 200m, ("İmplant", 1, 1000m, f.Provider2));
        await f.PaymentAsync(inv3, patient, "card", 200m, Baku(new DateOnly(2024, 2, 20), 11));

        using var client = f.Client(Tenant);
        var s = await GetAsync(client, day, branch);

        Assert.Equal("2024-03-10", s.GetProperty("date").GetString());
        var appointments = s.GetProperty("appointmentsToday");
        Assert.Equal(6, appointments.GetProperty("total").GetInt32());       // ləğv və başqa günlər sayılmır
        Assert.Equal(2, appointments.GetProperty("completed").GetInt32());
        Assert.Equal(1, appointments.GetProperty("noShow").GetInt32());
        Assert.Equal(1, appointments.GetProperty("waiting").GetInt32());
        Assert.Equal(90m, s.GetProperty("revenueToday").GetDecimal());       // 100 + 20 − 30
        Assert.Equal(150m, s.GetProperty("revenueMonth").GetDecimal());      // 90 + 60, fevral ödənişi yox
        Assert.Equal(850m, s.GetProperty("outstandingDebt").GetDecimal());   // 50 + 800
        Assert.Equal(2, s.GetProperty("newPatients").GetInt32());

        var services = s.GetProperty("topServices").EnumerateArray().ToList();
        Assert.Equal("Plomb,Təmizləmə", string.Join(",", services.Select(x => x.GetProperty("name").GetString())));
        Assert.Equal(3, services[0].GetProperty("count").GetInt32());
        Assert.Equal(160m, services[0].GetProperty("revenue").GetDecimal());
        Assert.Equal(40m, services[1].GetProperty("revenue").GetDecimal());   // fevral implantı ayın xidmətlərinə düşmür

        var providers = s.GetProperty("topProviders").EnumerateArray().ToList();
        Assert.Equal([f.Provider1, f.Provider2], providers.Select(p => p.GetProperty("providerId").GetGuid()).ToArray());
        Assert.Equal(160m, providers[0].GetProperty("revenue").GetDecimal());
        Assert.Equal("Dr Birinci", providers[0].GetProperty("name").GetString());

        var rooms = s.GetProperty("rooms").EnumerateArray().ToList();
        Assert.Equal("Otaq 1,Otaq 2", string.Join(",", rooms.Select(r => r.GetProperty("name").GetString())));   // passiv otaq yoxdur
        Assert.Equal("occupied,free", string.Join(",", rooms.Select(r => r.GetProperty("state").GetString())));
    }

    [IntegrationFact]
    public async Task Revenue_series_covers_thirty_local_days_with_zeros_and_negative_refund_days()
    {
        var day = new DateOnly(2024, 3, 10);   // fevral 29 gündür (kəbisə ili): seriya 10 fevraldan başlayır
        var branch = await f.BranchAsync();
        var patient = await f.PatientAsync(branch);
        var invoice = await f.InvoiceAsync(branch, patient, Baku(day.AddDays(-20), 9), 0m, ("X", 1, 500m, null));
        var early = await f.PaymentAsync(invoice, patient, "card", 200m, Baku(day.AddDays(-29), 9));   // pəncərənin ilk günü
        await f.PaymentAsync(invoice, patient, "card", 70m, Baku(day, 9));
        await f.RefundAsync(invoice, patient, early, 50m, Baku(day.AddDays(-3), 9));
        await f.PaymentAsync(invoice, patient, "card", 999m, Baku(day.AddDays(-30), 9));                // pəncərədən kənar

        using var client = f.Client(Tenant);
        var s = await GetAsync(client, day, branch);

        var series = s.GetProperty("revenueSeries").EnumerateArray().Select(p => (Day: p.GetProperty("day").GetString(), Amount: p.GetProperty("amount").GetDecimal())).ToList();
        Assert.Equal(30, series.Count);
        Assert.Equal("2024-02-10", series[0].Day);
        Assert.Equal("2024-03-10", series[^1].Day);
        Assert.Equal(200m, series[0].Amount);
        Assert.Equal(-50m, series.Single(p => p.Day == "2024-03-07").Amount);
        Assert.Equal(70m, series[^1].Amount);
        Assert.Equal(220m, series.Sum(p => p.Amount));   // 200 + 70 − 50; kənar 999 yoxdur
        Assert.Equal(27, series.Count(p => p.Amount == 0));
    }

    [IntegrationFact]
    public async Task Day_boundaries_follow_the_clinics_local_midnight_not_utc()
    {
        var day = new DateOnly(2023, 3, 10);
        var branch = await f.BranchAsync();
        var patient = await f.PatientAsync(branch);
        var invoice = await f.InvoiceAsync(branch, patient, Baku(day.AddDays(-1), 9), 0m, ("X", 1, 1000m, null));

        // Bakı: 10 mart 00:00 və 23:59 daxildir, 9 mart 23:59 və 11 mart 00:00 xaricdir
        await f.AppointmentAsync(branch, patient, f.Provider1, Baku(day, 0), "booked");
        await f.AppointmentAsync(branch, patient, f.Provider1, Baku(day, 23, 29), "booked");
        await f.AppointmentAsync(branch, patient, f.Provider1, Baku(day.AddDays(-1), 23, 29), "booked");
        await f.AppointmentAsync(branch, patient, f.Provider1, Baku(day.AddDays(1), 0), "booked");
        await f.PaymentAsync(invoice, patient, "cash", 1m, Baku(day.AddDays(-1), 23, 59));
        await f.PaymentAsync(invoice, patient, "cash", 10m, Baku(day, 0));
        await f.PaymentAsync(invoice, patient, "cash", 100m, Baku(day, 23, 59));
        await f.PaymentAsync(invoice, patient, "cash", 1000m, Baku(day.AddDays(1), 0));

        using var client = f.Client(Tenant);
        var s = await GetAsync(client, day, branch);

        Assert.Equal(2, s.GetProperty("appointmentsToday").GetProperty("total").GetInt32());
        Assert.Equal(110m, s.GetProperty("revenueToday").GetDecimal());
    }

    [IntegrationFact]
    public async Task Empty_branch_gives_zeros_not_errors()
    {
        var branch = await f.BranchAsync();
        using var client = f.Client(Tenant);

        var s = await GetAsync(client, new DateOnly(2022, 1, 5), branch);

        Assert.Equal(0, s.GetProperty("appointmentsToday").GetProperty("total").GetInt32());
        Assert.Equal(0m, s.GetProperty("revenueToday").GetDecimal());
        Assert.Equal(0m, s.GetProperty("outstandingDebt").GetDecimal());
        Assert.Empty(s.GetProperty("topServices").EnumerateArray());
        Assert.Empty(s.GetProperty("rooms").EnumerateArray());
        Assert.Equal(30, s.GetProperty("revenueSeries").GetArrayLength());
    }

    // ---------------- Access ----------------
    [IntegrationFact]
    public async Task Branch_scope_sees_only_its_branches_and_cannot_peek_at_others()
    {
        var day = new DateOnly(2022, 3, 10);
        var a = await f.BranchAsync();
        var b = await f.BranchAsync();
        var patientA = await f.PatientAsync(a);
        var patientB = await f.PatientAsync(b);
        var invA = await f.InvoiceAsync(a, patientA, Baku(day, 9), 0m, ("X", 1, 100m, null));
        var invB = await f.InvoiceAsync(b, patientB, Baku(day, 9), 0m, ("X", 1, 70m, null));
        await f.PaymentAsync(invA, patientA, "card", 100m, Baku(day, 10));
        await f.PaymentAsync(invB, patientB, "card", 70m, Baku(day, 10));

        using var branchUser = f.Client(["report:read@branch"], branches: [a]);
        using var tenantUser = f.Client(Tenant);

        Assert.Equal(100m, (await GetAsync(branchUser, day)).GetProperty("revenueToday").GetDecimal());          // filialsız sorğu: yalnız öz filialı
        Assert.Equal(100m, (await GetAsync(branchUser, day, a)).GetProperty("revenueToday").GetDecimal());
        Assert.Equal(HttpStatusCode.NotFound, (await branchUser.GetAsync($"/v1/dashboard/summary?date={day:yyyy-MM-dd}&branchId={b}")).StatusCode);
        Assert.Equal(70m, (await GetAsync(tenantUser, day, b)).GetProperty("revenueToday").GetDecimal());
        Assert.Equal(170m, (await GetAsync(tenantUser, day)).GetProperty("revenueToday").GetDecimal());           // bütün tenant

        using var twoBranches = f.Client(["report:read@branch"], branches: [a, b]);
        Assert.Equal(170m, (await GetAsync(twoBranches, day)).GetProperty("revenueToday").GetDecimal());
    }

    [IntegrationFact]
    public async Task Own_scope_missing_permission_anonymous_and_bad_input_are_refused()
    {
        var branch = await f.BranchAsync();
        var url = $"/v1/dashboard/summary?date=2022-03-10&branchId={branch}";
        using var own = f.Client(["report:read@own"]);
        using var other = f.Client(["patient:read@tenant", "invoice:read@tenant"]);
        using var anonymous = f.Factory!.CreateClientFor("demo");
        using var tenant = f.Client(Tenant);

        Assert.Equal(HttpStatusCode.Forbidden, (await own.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await tenant.GetAsync($"/v1/dashboard/summary?date={DateTime.UtcNow.AddDays(3):yyyy-MM-dd}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await tenant.GetAsync("/v1/dashboard/summary?date=not-a-date")).StatusCode);
    }

    [IntegrationFact]
    public async Task Another_tenant_never_sees_this_tenants_numbers_and_responses_are_not_cacheable()
    {
        var day = new DateOnly(2021, 3, 10);
        var branch = await f.BranchAsync();
        var patient = await f.PatientAsync(branch);
        var invoice = await f.InvoiceAsync(branch, patient, Baku(day, 9), 0m, ("X", 1, 500m, null));
        await f.PaymentAsync(invoice, patient, "card", 500m, Baku(day, 10));

        using var other = f.Client(Tenant, tenant: "other");
        var s = await GetAsync(other, day, branch);   // demo filialının id-si, amma "other" sxemində belə filial yoxdur

        Assert.Equal(0m, s.GetProperty("revenueToday").GetDecimal());
        using var demo = f.Client(Tenant);
        var response = await demo.GetAsync($"/v1/dashboard/summary?date={day:yyyy-MM-dd}&branchId={branch}");
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(500m, (await JsonAsync(response)).GetProperty("revenueToday").GetDecimal());
    }
}
