using System.Net;
using System.Net.Http.Json;
using DentaCore.TestSupport;
using static DentaCore.Billing.IntegrationTests.BillingApiFixture;

namespace DentaCore.Billing.IntegrationTests;

[Collection(BillingApiDefinition.Name)]
public sealed class ShiftAndRefundTests(BillingApiFixture f)
{
    private const string Demo = BillingApiFixture.Demo;

    private async Task<(HttpClient Client, Guid Id, Guid Shift)> CashierWithShiftAsync(string role = "cashier", decimal opening = 100m, string scope = "branch")
    {
        var (client, id) = await f.CashierAsync(f.BranchA, role, scope);
        var response = await client.PostAsJsonAsync("/v1/cash-shifts/open", new { branchId = f.BranchA, openingCash = opening });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (client, id, (await JsonAsync(response)).GetProperty("id").GetGuid());
    }

    private async Task<(Guid Invoice, Guid Payment)> PaidAsync(HttpClient cashier, decimal price, string method, decimal amount)
    {
        var patient = await f.SeedPatientAsync(f.BranchA);
        var invoice = await IssuedInvoiceAsync(cashier, patient, f.BranchA, price);
        var payment = await PayAsync(cashier, invoice, method, amount);
        Assert.Equal(HttpStatusCode.Created, payment.StatusCode);
        return (invoice, (await JsonAsync(payment)).GetProperty("id").GetGuid());
    }

    // ---------------- Shifts ----------------
    [IntegrationFact]
    public async Task Shift_close_computes_expected_cash_from_cash_payments_and_cash_refunds_only()
    {
        var (cashier, _, shift) = await CashierWithShiftAsync(role: "director", opening: 100m, scope: "tenant");
        var (_, cashPayment) = await PaidAsync(cashier, 200m, "cash", 150m);
        await PaidAsync(cashier, 100m, "card", 100m);   // kart kassaya girmir
        var refund = await RefundAsync(cashier, cashPayment, 30m);
        Assert.Equal(HttpStatusCode.Created, refund.StatusCode);

        var current = await JsonAsync(await cashier.GetAsync("/v1/cash-shifts/current"));
        Assert.Equal(shift, current.GetProperty("id").GetGuid());

        var closed = await cashier.PostAsJsonAsync($"/v1/cash-shifts/{shift}/close", new { closingCash = 215m });

        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var body = await JsonAsync(closed);
        Assert.Equal(220m, body.GetProperty("expectedCash").GetDecimal());   // 100 + 150 - 30
        Assert.Equal(-5m, body.GetProperty("difference").GetDecimal());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, body.GetProperty("closedAt").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.GetAsync("/v1/cash-shifts/current")).StatusCode);
    }

    [IntegrationFact]
    public async Task Only_one_open_shift_per_cashier_and_closing_twice_is_a_conflict()
    {
        var (cashier, _, shift) = await CashierWithShiftAsync();

        var second = await cashier.PostAsJsonAsync("/v1/cash-shifts/open", new { branchId = f.BranchA, openingCash = 0m });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(shift, (await JsonAsync(second)).GetProperty("shiftId").GetGuid());

        Assert.Equal(HttpStatusCode.OK, (await cashier.PostAsJsonAsync($"/v1/cash-shifts/{shift}/close", new { closingCash = 100m })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await cashier.PostAsJsonAsync($"/v1/cash-shifts/{shift}/close", new { closingCash = 100m })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await cashier.PostAsJsonAsync("/v1/cash-shifts/open", new { branchId = f.BranchA, openingCash = 0m })).StatusCode);
    }

    [IntegrationFact]
    public async Task Parallel_open_requests_create_one_shift()
    {
        var (cashier, _) = await f.CashierAsync(f.BranchA);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => cashier.PostAsJsonAsync("/v1/cash-shifts/open", new { branchId = f.BranchA, openingCash = 10m })));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [IntegrationFact]
    public async Task Shift_scope_and_validation()
    {
        var (cashier, _, shift) = await CashierWithShiftAsync();
        var (colleague, _) = await f.CashierAsync(f.BranchA);
        var (otherBranch, _) = await f.CashierAsync(f.BranchB);

        Assert.Equal(HttpStatusCode.NotFound, (await colleague.PostAsJsonAsync($"/v1/cash-shifts/{shift}/close", new { closingCash = 1m })).StatusCode);   // başqasının smeni
        Assert.Equal(HttpStatusCode.NotFound, (await otherBranch.PostAsJsonAsync($"/v1/cash-shifts/{shift}/close", new { closingCash = 1m })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await colleague.PostAsJsonAsync("/v1/cash-shifts/open", new { branchId = f.BranchB, openingCash = 1m })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await colleague.PostAsJsonAsync("/v1/cash-shifts/open", new { branchId = f.BranchA, openingCash = -1m })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await cashier.PostAsJsonAsync($"/v1/cash-shifts/{shift}/close", new { closingCash = 1.234m })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.PostAsJsonAsync($"/v1/cash-shifts/{Guid.NewGuid()}/close", new { closingCash = 1m })).StatusCode);

        // tenant scope-lu rəhbər başqasının smenini bağlaya bilər
        var managerId = await f.Db.SeedUserAsync(Demo, $"mgr-{Guid.NewGuid():N}@clinic.az", "Menecer", "manager");
        using var manager = f.Client(["payment:write@tenant"], managerId);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/v1/cash-shifts/{shift}/close", new { closingCash = 100m })).StatusCode);
    }

    [IntegrationFact]
    public async Task Cash_payment_after_the_shift_is_closed_is_refused()
    {
        var (cashier, _, shift) = await CashierWithShiftAsync();
        var patient = await f.SeedPatientAsync(f.BranchA);
        var invoice = await IssuedInvoiceAsync(cashier, patient, f.BranchA, 50m);
        await cashier.PostAsJsonAsync($"/v1/cash-shifts/{shift}/close", new { closingCash = 100m });

        var response = await PayAsync(cashier, invoice, "cash", 10m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("billing.shift_required", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [IntegrationFact]
    public async Task Closing_a_shift_while_cash_payments_arrive_never_loses_a_payment_from_the_expected_total()
    {
        for (var round = 0; round < 4; round++)
        {
            var (cashier, _, shift) = await CashierWithShiftAsync(opening: 0m);
            var patient = await f.SeedPatientAsync(f.BranchA);
            var invoice = await IssuedInvoiceAsync(cashier, patient, f.BranchA, 1000m);

            var payments = Enumerable.Range(0, 6).Select(_ => PayAsync(cashier, invoice, "cash", 10m)).ToList();
            var close = cashier.PostAsJsonAsync($"/v1/cash-shifts/{shift}/close", new { closingCash = 0m });
            await Task.WhenAll(payments.Append(close));

            var expected = await f.Db.ScalarAsync<decimal>(Demo, "SELECT expected_cash FROM cash_shifts WHERE id = $1", shift);
            var recorded = await f.Db.ScalarAsync<decimal>(Demo, "SELECT COALESCE(sum(amount), 0) FROM payments WHERE shift_id = $1 AND method = 'cash'", shift);
            // Smen bağlandıqdan sonra heç bir nağd ödəniş həmin smenə yazıla bilməz: gözlənilən cəm = smenə yazılmış nağd
            Assert.Equal(expected, recorded);
        }
    }

    // ---------------- Refunds ----------------
    [IntegrationFact]
    public async Task Cashier_with_a_zero_refund_limit_is_told_to_ask_a_manager()
    {
        var (cashier, _, _) = await CashierWithShiftAsync(role: "cashier");
        var (invoice, payment) = await PaidAsync(cashier, 100m, "cash", 100m);

        var response = await RefundAsync(cashier, payment, 1m);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("billing.refund_limit_exceeded", body.GetProperty("code").GetString());
        Assert.Equal(0m, body.GetProperty("limit").GetDecimal());
        Assert.Equal("paid", await f.Db.ScalarAsync<string>(Demo, "SELECT status FROM invoices WHERE id = $1", invoice));
    }

    [IntegrationFact]
    public async Task Limited_role_can_refund_up_to_its_limit_per_request()
    {
        var (cashier, _, _) = await CashierWithShiftAsync(role: "limited_refund");
        var (_, payment) = await PaidAsync(cashier, 200m, "cash", 200m);

        Assert.Equal(HttpStatusCode.Forbidden, (await RefundAsync(cashier, payment, 50.01m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await RefundAsync(cashier, payment, 50m)).StatusCode);
    }

    [IntegrationFact]
    public async Task Role_with_unlimited_refund_returns_money_and_the_debt_comes_back()
    {
        var (director, _, _) = await CashierWithShiftAsync(role: "director", scope: "tenant");
        var (invoice, payment) = await PaidAsync(director, 100m, "cash", 100m);

        var partial = await RefundAsync(director, payment, 40m);
        Assert.Equal(HttpStatusCode.Created, partial.StatusCode);
        var body = await JsonAsync(partial);
        Assert.Equal("refund", body.GetProperty("kind").GetString());
        Assert.Equal(payment, body.GetProperty("refundOf").GetGuid());
        Assert.Equal("partially_paid", body.GetProperty("invoiceStatus").GetString());
        Assert.Equal(60m, await f.Db.ScalarAsync<decimal>(Demo, "SELECT paid_total FROM invoices WHERE id = $1", invoice));

        Assert.Equal(HttpStatusCode.Created, (await RefundAsync(director, payment, 60m)).StatusCode);
        var detail = await JsonAsync(await director.GetAsync($"/v1/invoices/{invoice}"));
        Assert.Equal("issued", detail.GetProperty("status").GetString());
        Assert.Equal(100m, detail.GetProperty("balance").GetDecimal());

        // Tam geri qaytarıldı: artıq heç nə qalmayıb
        var again = await RefundAsync(director, payment, 0.01m);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("billing.refund_exceeds_payment", (await JsonAsync(again)).GetProperty("code").GetString());

        // ləğv indi mümkündür
        Assert.Equal(HttpStatusCode.OK, (await director.PostAsync($"/v1/invoices/{invoice}/void", null)).StatusCode);
    }

    [IntegrationFact]
    public async Task Refunds_cannot_exceed_the_remaining_refundable_amount_and_are_idempotent()
    {
        var (director, _, _) = await CashierWithShiftAsync(role: "director", scope: "tenant");
        var (invoice, payment) = await PaidAsync(director, 100m, "card", 100m);

        var first = await RefundAsync(director, payment, 70m, "refund-key-1");
        var replay = await RefundAsync(director, payment, 70m, "refund-key-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal((await JsonAsync(first)).GetProperty("id").GetGuid(), (await JsonAsync(replay)).GetProperty("id").GetGuid());
        Assert.Equal(30m, await f.Db.ScalarAsync<decimal>(Demo, "SELECT paid_total FROM invoices WHERE id = $1", invoice));

        var over = await RefundAsync(director, payment, 30.01m);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        Assert.Equal(30m, (await JsonAsync(over)).GetProperty("refundable").GetDecimal());

        Assert.Equal(HttpStatusCode.Conflict, (await RefundAsync(director, payment, 10m, "refund-key-1")).StatusCode);   // eyni açar, fərqli məbləğ
        Assert.Equal(HttpStatusCode.Created, (await RefundAsync(director, payment, 30m)).StatusCode);
    }

    [IntegrationFact]
    public async Task Parallel_refunds_cannot_return_more_than_was_paid()
    {
        var (director, _, _) = await CashierWithShiftAsync(role: "director", scope: "tenant");
        var (invoice, payment) = await PaidAsync(director, 100m, "card", 100m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RefundAsync(director, payment, 30m)));

        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Created));   // 3 × 30 ≤ 100
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        Assert.Equal(90m, await f.Db.ScalarAsync<decimal>(Demo, "SELECT COALESCE(sum(amount), 0) FROM payments WHERE refund_of = $1", payment));
        Assert.Equal(10m, await f.Db.ScalarAsync<decimal>(Demo, "SELECT paid_total FROM invoices WHERE id = $1", invoice));
    }

    [IntegrationFact]
    public async Task Refund_validation_and_access()
    {
        var (director, _, _) = await CashierWithShiftAsync(role: "director", scope: "tenant");
        var (_, payment) = await PaidAsync(director, 100m, "card", 100m);
        var (branchBDirector, _) = await f.CashierAsync(f.BranchB, "director");
        var refundId = (await JsonAsync(await RefundAsync(director, payment, 10m))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await RefundAsync(director, payment, 5m, reason: "")).StatusCode);   // səbəb məcburidir
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await RefundAsync(director, payment, 0m)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await RefundAsync(director, refundId, 1m)).StatusCode);    // geri qaytarmanın özü qaytarılmır
        Assert.Equal(HttpStatusCode.NotFound, (await RefundAsync(director, Guid.NewGuid(), 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RefundAsync(branchBDirector, payment, 1m)).StatusCode);          // başqa filial

        var noKey = await director.PostAsJsonAsync($"/v1/payments/{payment}/refund", new { amount = 1m, reason = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
    }

    [IntegrationFact]
    public async Task Cash_refund_needs_an_open_shift_but_card_refund_does_not()
    {
        var (director, _, shift) = await CashierWithShiftAsync(role: "director", scope: "tenant");
        var (_, cashPayment) = await PaidAsync(director, 100m, "cash", 100m);
        var (_, cardPayment) = await PaidAsync(director, 100m, "card", 100m);
        await director.PostAsJsonAsync($"/v1/cash-shifts/{shift}/close", new { closingCash = 200m });

        var cash = await RefundAsync(director, cashPayment, 10m);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cash.StatusCode);
        Assert.Equal("billing.shift_required", (await JsonAsync(cash)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, (await RefundAsync(director, cardPayment, 10m)).StatusCode);
    }
}
