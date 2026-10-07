using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DentaCore.TestSupport;
using static DentaCore.Billing.IntegrationTests.BillingApiFixture;

namespace DentaCore.Billing.IntegrationTests;

[Collection(BillingApiDefinition.Name)]
public sealed class InvoiceTests(BillingApiFixture f)
{
    private const string Demo = BillingApiFixture.Demo;

    private static async Task<(HttpResponseMessage Response, JsonElement Body)> SendAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        return (response, JsonDocument.Parse(await response.Content.ReadAsStringAsync() is { Length: > 0 } s ? s : "{}").RootElement);
    }

    // ---------------- Price list ----------------
    [IntegrationFact]
    public async Task Price_list_can_be_created_updated_with_if_match_and_filtered()
    {
        var (cashier, _) = await f.CashierAsync(f.BranchA);
        using var admin = f.Client(["settings:manage@tenant", "invoice:read@tenant"], Guid.NewGuid());
        var code = "P" + Guid.NewGuid().ToString("N")[..8];

        var created = await admin.PostAsJsonAsync("/v1/services", new { code, name = "Implant Alpha", category = "Cərrahiyyə", price = 1200.50m, vatRate = 18 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var service = await JsonAsync(created);
        var id = service.GetProperty("id").GetGuid();
        Assert.Equal("AZN", service.GetProperty("currency").GetString());

        var duplicate = await admin.PostAsJsonAsync("/v1/services", new { code, name = "Başqa", price = 1m });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var noIfMatch = new HttpRequestMessage(HttpMethod.Put, $"/v1/services/{id}") { Content = JsonContent.Create(new { code, name = "Implant Alpha", price = 1300m }) };
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await admin.SendAsync(noIfMatch)).StatusCode);

        using var update = new HttpRequestMessage(HttpMethod.Put, $"/v1/services/{id}") { Content = JsonContent.Create(new { code, name = "Implant Alpha", price = 1300m, vatRate = 18 }) };
        update.Headers.TryAddWithoutValidation("If-Match", "\"" + service.GetProperty("rowVersion").GetInt32() + "\"");
        var updated = await admin.SendAsync(update);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedBody = await JsonAsync(updated);
        Assert.Equal(1300m, updatedBody.GetProperty("price").GetDecimal());
        Assert.True(updatedBody.GetProperty("rowVersion").GetInt32() > service.GetProperty("rowVersion").GetInt32());   // yeni versiya cavabdadır

        using var stale = new HttpRequestMessage(HttpMethod.Put, $"/v1/services/{id}") { Content = JsonContent.Create(new { code, name = "x", price = 1m }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"" + service.GetProperty("rowVersion").GetInt32() + "\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await admin.SendAsync(stale)).StatusCode);

        var found = await JsonAsync(await admin.GetAsync("/v1/services?q=alpha"));
        Assert.Contains(found.EnumerateArray(), s => s.GetProperty("id").GetGuid() == id);
        var wildcard = await JsonAsync(await admin.GetAsync("/v1/services?q=%25"));   // % hərfi hərfdir, hamısını qaytarmır
        Assert.DoesNotContain(wildcard.EnumerateArray(), s => s.GetProperty("id").GetGuid() == id);

        // kassirin settings:manage icazəsi yoxdur
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/v1/services", new { code = code + "x", name = "n", price = 1m })).StatusCode);
    }

    // ---------------- Create ----------------
    [IntegrationFact]
    public async Task Invoice_takes_prices_and_vat_from_the_price_list_and_computes_totals()
    {
        var (cashier, _) = await f.CashierAsync(f.BranchA);
        var patient = await f.SeedPatientAsync(f.BranchA);
        var cleaning = await f.SeedServiceAsync(40m, vat: 18m);
        var filling = await f.SeedServiceAsync(100m);

        var response = await cashier.PostAsJsonAsync("/v1/invoices", new
        {
            kind = "invoice", patientId = patient, branchId = f.BranchA,
            items = new object[]
            {
                new { serviceId = cleaning, quantity = 2, toothFdi = (int?)null },
                new { serviceId = filling, quantity = 1, discount = 10m, toothFdi = 16 },
                new { description = "Əlavə", quantity = 1, unitPrice = 5m },
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invoice = await JsonAsync(response);
        Assert.Equal("draft", invoice.GetProperty("status").GetString());
        Assert.Equal(185m, invoice.GetProperty("subtotal").GetDecimal());      // 80 + 100 + 5
        Assert.Equal(10m, invoice.GetProperty("discountTotal").GetDecimal());
        Assert.Equal(14.4m, invoice.GetProperty("taxTotal").GetDecimal());     // 80 * 18%
        Assert.Equal(189.4m, invoice.GetProperty("total").GetDecimal());
        Assert.Equal(189.4m, invoice.GetProperty("balance").GetDecimal());
        Assert.Matches(@"^INV-\d{4}-\d{6}$", invoice.GetProperty("number").GetString());
        Assert.Equal(3, invoice.GetProperty("items").GetArrayLength());
        // DB-dəki hesablanmış sətir cəmi domendəkilə uyğundur
        var id = invoice.GetProperty("id").GetGuid();
        Assert.Equal(189.4m - 14.4m, await f.Db.ScalarAsync<decimal>(Demo, "SELECT sum(line_total) FROM invoice_items WHERE invoice_id = $1", id));
    }

    [IntegrationFact]
    public async Task Price_change_does_not_touch_existing_invoice_lines()
    {
        var (cashier, _) = await f.CashierAsync(f.BranchA);
        var patient = await f.SeedPatientAsync(f.BranchA);
        var service = await f.SeedServiceAsync(100m);
        var created = await cashier.PostAsJsonAsync("/v1/invoices", new { kind = "invoice", patientId = patient, branchId = f.BranchA, items = new[] { new { serviceId = service, quantity = 1 } } });
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();

        await f.Db.ExecAsync(Demo, "UPDATE services SET price = 999 WHERE id = $1", service);

        var invoice = await JsonAsync(await cashier.GetAsync($"/v1/invoices/{id}"));
        Assert.Equal(100m, invoice.GetProperty("total").GetDecimal());
    }

    [IntegrationFact]
    public async Task Create_validates_input()
    {
        var (cashier, _) = await f.CashierAsync(f.BranchA);
        var patient = await f.SeedPatientAsync(f.BranchA);
        var inactive = await f.SeedServiceAsync(10m, active: false);
        var good = new { description = "x", quantity = 1, unitPrice = 10m };

        async Task<HttpStatusCode> Post(object body) => (await cashier.PostAsJsonAsync("/v1/invoices", body)).StatusCode;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Post(new { kind = "invoice", patientId = patient, branchId = f.BranchA, items = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Post(new { kind = "nonsense", patientId = patient, branchId = f.BranchA, items = new[] { good } }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Post(new { kind = "invoice", patientId = patient, branchId = f.BranchA, items = new[] { new { description = "x", quantity = 1 } } }));   // nə qiymət, nə xidmət
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Post(new { kind = "invoice", patientId = patient, branchId = f.BranchA, items = new[] { new { serviceId = inactive, quantity = 1 } } }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Post(new { kind = "invoice", patientId = patient, branchId = f.BranchA, items = new[] { new { description = "x", quantity = 1, unitPrice = 10.123m } } }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Post(new { kind = "invoice", patientId = patient, branchId = f.BranchA, items = new[] { new { description = "x", quantity = 1, unitPrice = 10m, discount = 11m } } }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Post(new { kind = "invoice", patientId = patient, branchId = f.BranchA, promoCode = "YAZ", items = new[] { good } }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Post(new { kind = "invoice", patientId = patient, branchId = f.BranchA, visitId = Guid.NewGuid(), items = new[] { good } }));   // FK
        Assert.Equal(HttpStatusCode.NotFound, await Post(new { kind = "invoice", patientId = Guid.NewGuid(), branchId = f.BranchA, items = new[] { good } }));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(new { kind = "invoice", patientId = patient, branchId = f.BranchB, items = new[] { good } }));   // kassir yalnız A filialındadır
        Assert.Equal(HttpStatusCode.Created, await Post(new { kind = "estimate", patientId = patient, branchId = f.BranchA, items = new[] { good } }));
    }

    // ---------------- Issue / void ----------------
    [IntegrationFact]
    public async Task Issue_freezes_the_invoice_both_in_the_domain_and_in_the_database()
    {
        var (cashier, _) = await f.CashierAsync(f.BranchA);
        var patient = await f.SeedPatientAsync(f.BranchA);
        var id = await IssuedInvoiceAsync(cashier, patient, f.BranchA, 100m);

        var invoice = await JsonAsync(await cashier.GetAsync($"/v1/invoices/{id}"));
        Assert.Equal("issued", invoice.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, invoice.GetProperty("dueDate").ValueKind);

        var again = await cashier.PostAsync($"/v1/invoices/{id}/issue", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // Tətbiq qatını yan keçən birbaşa SQL də rədd olunur (DB trigger-i)
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            f.Db.ExecAsync(Demo, "UPDATE invoice_items SET unit_price = 1 WHERE invoice_id = $1", id));
        Assert.Contains("immutable", ex.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Db.ExecAsync(
            Demo, "INSERT INTO invoice_items(invoice_id, description, quantity, unit_price) VALUES ($1, 'gizli', 1, 1)", id));
    }

    [IntegrationFact]
    public async Task Estimate_cannot_be_issued_and_zero_total_cannot_be_issued()
    {
        var (cashier, _) = await f.CashierAsync(f.BranchA);
        var patient = await f.SeedPatientAsync(f.BranchA);
        var estimate = await JsonAsync(await cashier.PostAsJsonAsync("/v1/invoices", new { kind = "estimate", patientId = patient, branchId = f.BranchA, items = new[] { new { description = "x", quantity = 1, unitPrice = 10m } } }));
        var free = await JsonAsync(await cashier.PostAsJsonAsync("/v1/invoices", new { kind = "invoice", patientId = patient, branchId = f.BranchA, items = new[] { new { description = "x", quantity = 1, unitPrice = 0m } } }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await cashier.PostAsync($"/v1/invoices/{estimate.GetProperty("id").GetGuid()}/issue", null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await cashier.PostAsync($"/v1/invoices/{free.GetProperty("id").GetGuid()}/issue", null)).StatusCode);
    }

    [IntegrationFact]
    public async Task Void_rules()
    {
        var (cashier, _) = await f.CashierAsync(f.BranchA);
        var patient = await f.SeedPatientAsync(f.BranchA);
        var unpaid = await IssuedInvoiceAsync(cashier, patient, f.BranchA, 50m);
        var paid = await IssuedInvoiceAsync(cashier, patient, f.BranchA, 50m);
        await OpenShiftAsync(cashier, f.BranchA);
        Assert.Equal(HttpStatusCode.Created, (await PayAsync(cashier, paid, "cash", 10m)).StatusCode);

        var ok = await cashier.PostAsync($"/v1/invoices/{unpaid}/void", null);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("void", (await JsonAsync(ok)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await cashier.PostAsync($"/v1/invoices/{unpaid}/void", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await cashier.PostAsync($"/v1/invoices/{paid}/void", null)).StatusCode);
        // ləğv edilmiş fakturaya ödəniş olmaz
        Assert.Equal(HttpStatusCode.Conflict, (await PayAsync(cashier, unpaid, "cash", 1m)).StatusCode);
    }

    // ---------------- Reads, scope, paging ----------------
    [IntegrationFact]
    public async Task Branch_scope_hides_other_branches_and_other_tenants()
    {
        var (cashierA, _) = await f.CashierAsync(f.BranchA);
        var (cashierB, _) = await f.CashierAsync(f.BranchB);
        var patient = await f.SeedPatientAsync(f.BranchA);
        var id = await IssuedInvoiceAsync(cashierA, patient, f.BranchA, 20m);

        Assert.Equal(HttpStatusCode.OK, (await cashierA.GetAsync($"/v1/invoices/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cashierB.GetAsync($"/v1/invoices/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cashierB.PostAsync($"/v1/invoices/{id}/void", null)).StatusCode);
        var listB = await JsonAsync(await cashierB.GetAsync($"/v1/invoices?patientId={patient}"));
        Assert.Empty(listB.GetProperty("items").EnumerateArray());

        using var otherTenant = f.Client(["invoice:read@tenant"], Guid.NewGuid(), tenant: "other");
        Assert.Equal(HttpStatusCode.NotFound, (await otherTenant.GetAsync($"/v1/invoices/{id}")).StatusCode);

        using var noPermission = f.Client(["patient:read@tenant"], Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await noPermission.GetAsync($"/v1/invoices/{id}")).StatusCode);
        using var anonymous = f.Factory!.CreateClientFor("demo");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/v1/invoices/{id}")).StatusCode);
    }

    [IntegrationFact]
    public async Task Doctor_with_own_scope_sees_only_invoices_of_his_work()
    {
        var (cashier, _) = await f.CashierAsync(f.BranchA);
        var doctorId = await f.Db.SeedUserAsync(Demo, $"dr-{Guid.NewGuid():N}@clinic.az", "Dr", "doctor");
        using var doctor = f.Client(["invoice:read@own"], doctorId, [f.BranchA]);
        var patient = await f.SeedPatientAsync(f.BranchA);
        var mine = await JsonAsync(await cashier.PostAsJsonAsync("/v1/invoices", new
        {
            kind = "invoice", patientId = patient, branchId = f.BranchA,
            items = new[] { new { description = "x", quantity = 1, unitPrice = 10m, providerId = doctorId } },
        }));
        var notMine = await JsonAsync(await cashier.PostAsJsonAsync("/v1/invoices", new { kind = "invoice", patientId = patient, branchId = f.BranchA, items = new[] { new { description = "y", quantity = 1, unitPrice = 10m } } }));

        Assert.Equal(HttpStatusCode.OK, (await doctor.GetAsync($"/v1/invoices/{mine.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await doctor.GetAsync($"/v1/invoices/{notMine.GetProperty("id").GetGuid()}")).StatusCode);
        var list = await JsonAsync(await doctor.GetAsync($"/v1/invoices?patientId={patient}"));
        Assert.Equal([mine.GetProperty("id").GetGuid()], list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());
    }

    [IntegrationFact]
    public async Task List_pages_with_a_cursor_without_gaps_or_duplicates_and_filters_by_status_and_overdue()
    {
        var (cashier, _) = await f.CashierAsync(f.BranchA);
        var patient = await f.SeedPatientAsync(f.BranchA);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await IssuedInvoiceAsync(cashier, patient, f.BranchA, 10m + i));
        }

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"/v1/invoices?patientId={patient}&limit=2" + (cursor is null ? string.Empty : $"&cursor={cursor}");
            var page = await JsonAsync(await cashier.GetAsync(url));
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(ids.Order(), seen.Order());
        Assert.Equal(5, seen.Distinct().Count());

        await f.Db.ExecAsync(Demo, "UPDATE invoices SET due_date = current_date - 10 WHERE id = $1", ids[0]);
        var overdue = await JsonAsync(await cashier.GetAsync($"/v1/invoices?patientId={patient}&overdue=true"));
        Assert.Equal([ids[0]], overdue.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());
        var drafts = await JsonAsync(await cashier.GetAsync($"/v1/invoices?patientId={patient}&status=draft"));
        Assert.Empty(drafts.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await cashier.GetAsync("/v1/invoices?status=whatever")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await cashier.GetAsync("/v1/invoices?limit=1000")).StatusCode);
    }

    [IntegrationFact]
    public async Task Invoice_reads_are_never_cached_and_money_changes_are_audited_without_pii()
    {
        var (cashier, _) = await f.CashierAsync(f.BranchA);
        var patient = await f.SeedPatientAsync(f.BranchA);
        var id = await IssuedInvoiceAsync(cashier, patient, f.BranchA, 25m);
        await OpenShiftAsync(cashier, f.BranchA);
        await PayAsync(cashier, id, "cash", 25m);

        var response = await cashier.GetAsync($"/v1/invoices/{id}");
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.NotNull(response.Headers.ETag);

        var actions = await f.Db.ScalarAsync<long>(Demo, "SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action IN ('invoice.create','invoice.issue','payment.receive','invoice.read')", id);
        Assert.True(actions >= 3);
        var chain = await f.VerifyAuditChainAsync();
        Assert.True(chain.IsIntact, chain.ToString());
    }

    // ---------------- Database guarantees ----------------
    [IntegrationFact]
    public async Task Database_refuses_overpayment_and_payment_rewrites_even_when_the_application_is_bypassed()
    {
        var (cashier, _) = await f.CashierAsync(f.BranchA);
        var patient = await f.SeedPatientAsync(f.BranchA);
        var id = await IssuedInvoiceAsync(cashier, patient, f.BranchA, 100m);
        await OpenShiftAsync(cashier, f.BranchA);
        var payment = await JsonAsync(await PayAsync(cashier, id, "cash", 40m));

        await Assert.ThrowsAnyAsync<Exception>(() => f.Db.ExecAsync(Demo, "UPDATE invoices SET paid_total = 100.01 WHERE id = $1", id));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Db.ExecAsync(Demo, "UPDATE payments SET amount = 1 WHERE id = $1", payment.GetProperty("id").GetGuid()));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Db.ExecAsync(Demo, "DELETE FROM payments WHERE id = $1", payment.GetProperty("id").GetGuid()));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Db.ExecAsync(
            Demo, "INSERT INTO payments(invoice_id, patient_id, kind, method, amount) VALUES ($1, $2, 'refund', 'cash', 1)", id, patient));   // refund_of olmadan
    }
}
