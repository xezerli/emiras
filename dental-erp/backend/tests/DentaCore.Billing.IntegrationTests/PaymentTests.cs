using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DentaCore.TestSupport;
using static DentaCore.Billing.IntegrationTests.BillingApiFixture;

namespace DentaCore.Billing.IntegrationTests;

[Collection(BillingApiDefinition.Name)]
public sealed class PaymentTests(BillingApiFixture f)
{
    private const string Demo = BillingApiFixture.Demo;

    private async Task<(HttpClient Cashier, Guid Invoice, Guid Patient)> ReadyAsync(decimal price, bool openShift = true)
    {
        var (cashier, _) = await f.CashierAsync(f.BranchA);
        var patient = await f.SeedPatientAsync(f.BranchA);
        var invoice = await IssuedInvoiceAsync(cashier, patient, f.BranchA, price);
        if (openShift)
        {
            await OpenShiftAsync(cashier, f.BranchA);
        }

        return (cashier, invoice, patient);
    }

    private Task<decimal> PaidTotal(Guid invoice) => f.Db.ScalarAsync<decimal>(Demo, "SELECT paid_total FROM invoices WHERE id = $1", invoice);

    private Task<long> PaymentRows(Guid invoice) => f.Db.ScalarAsync<long>(Demo, "SELECT count(*) FROM payments WHERE invoice_id = $1 AND kind = 'payment'", invoice)!;

    [IntegrationFact]
    public async Task Partial_payments_lead_to_paid_and_show_up_on_the_invoice()
    {
        var (cashier, invoice, _) = await ReadyAsync(100m);

        var first = await PayAsync(cashier, invoice, "cash", 30m);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await JsonAsync(first);
        Assert.Equal("partially_paid", firstBody.GetProperty("invoiceStatus").GetString());
        Assert.Equal("payment", firstBody.GetProperty("kind").GetString());

        var second = await PayAsync(cashier, invoice, "card", 70m);
        Assert.Equal("paid", (await JsonAsync(second)).GetProperty("invoiceStatus").GetString());

        var detail = await JsonAsync(await cashier.GetAsync($"/v1/invoices/{invoice}"));
        Assert.Equal("paid", detail.GetProperty("status").GetString());
        Assert.Equal(100m, detail.GetProperty("paidTotal").GetDecimal());
        Assert.Equal(0m, detail.GetProperty("balance").GetDecimal());
        Assert.Equal("cash,card", string.Join(",", detail.GetProperty("payments").EnumerateArray().Select(p => p.GetProperty("method").GetString())));

        // outbox: iki ödəniş hadisəsi (Accounting/Dashboard üçün), sonuncusu "tam ödənib" işarəli
        var events = await f.Db.ScalarAsync<long>(Demo, "SELECT count(*) FROM outbox_messages WHERE routing_key = 'billing.payment-received' AND payload->>'invoiceId' = $1", invoice.ToString());
        Assert.Equal(2, events);
    }

    [IntegrationFact]
    public async Task Overpayment_and_bad_amounts_are_refused_and_change_nothing()
    {
        var (cashier, invoice, _) = await ReadyAsync(100m);
        await PayAsync(cashier, invoice, "cash", 60m);

        var over = await PayAsync(cashier, invoice, "cash", 40.01m);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        var body = await JsonAsync(over);
        Assert.Equal("billing.overpayment", body.GetProperty("code").GetString());
        Assert.Equal(40m, body.GetProperty("balance").GetDecimal());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PayAsync(cashier, invoice, "cash", 0m)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PayAsync(cashier, invoice, "cash", -5m)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PayAsync(cashier, invoice, "cash", 1.005m)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PayAsync(cashier, invoice, "gift_card", 1m)).StatusCode);
        Assert.Equal(60m, await PaidTotal(invoice));
        Assert.Equal(1, await PaymentRows(invoice));
    }

    [IntegrationFact]
    public async Task Cash_needs_an_open_shift_in_the_same_branch_but_card_does_not()
    {
        var (cashier, invoice, _) = await ReadyAsync(100m, openShift: false);

        var noShift = await PayAsync(cashier, invoice, "cash", 10m);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noShift.StatusCode);
        Assert.Equal("billing.shift_required", (await JsonAsync(noShift)).GetProperty("code").GetString());

        var card = await PayAsync(cashier, invoice, "card", 10m);
        Assert.Equal(HttpStatusCode.Created, card.StatusCode);
        Assert.Null(await f.Db.ScalarAsync<Guid?>(Demo, "SELECT shift_id FROM payments WHERE id = $1", (await JsonAsync(card)).GetProperty("id").GetGuid()));

        // B filialında smen açan kassir A filialının fakturasına nağd qəbul edə bilməz
        var id = await f.Db.SeedUserAsync(Demo, $"multi-{Guid.NewGuid():N}@clinic.az", "Kassir", "cashier");
        using var multi = f.Client(["invoice:read@tenant", "payment:write@tenant"], id);
        await OpenShiftAsync(multi, f.BranchB);
        var mismatch = await PayAsync(multi, invoice, "cash", 5m);
        Assert.Equal("billing.shift_branch_mismatch", (await JsonAsync(mismatch)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, (await PayAsync(multi, invoice, "card", 5m)).StatusCode);   // kart başqa filialın kassasına yazılmır, amma qəbul olunur
    }

    [IntegrationFact]
    public async Task Draft_invoice_cannot_be_paid()
    {
        var (cashier, _) = await f.CashierAsync(f.BranchA);
        var patient = await f.SeedPatientAsync(f.BranchA);
        await OpenShiftAsync(cashier, f.BranchA);
        var draft = await JsonAsync(await cashier.PostAsJsonAsync("/v1/invoices", new { kind = "invoice", patientId = patient, branchId = f.BranchA, items = new[] { new { description = "x", quantity = 1, unitPrice = 10m } } }));

        Assert.Equal(HttpStatusCode.Conflict, (await PayAsync(cashier, draft.GetProperty("id").GetGuid(), "cash", 5m)).StatusCode);
    }

    [IntegrationFact]
    public async Task Payment_requires_the_permission_a_scope_and_an_idempotency_key()
    {
        var (cashier, invoice, _) = await ReadyAsync(100m);
        var (otherBranchCashier, _) = await f.CashierAsync(f.BranchB);
        using var readOnly = f.Client(["invoice:read@tenant"], Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, (await PayAsync(readOnly, invoice, "card", 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PayAsync(otherBranchCashier, invoice, "card", 1m)).StatusCode);

        var noKey = await cashier.PostAsJsonAsync($"/v1/invoices/{invoice}/payments", new { method = "card", amount = 1m });
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal("idempotency.key_required", (await JsonAsync(noKey)).GetProperty("code").GetString());
        Assert.Equal(0, await PaymentRows(invoice));
    }

    // ---------------- Idempotency ----------------
    [IntegrationFact]
    public async Task Same_idempotency_key_returns_the_same_payment_without_charging_twice()
    {
        var (cashier, invoice, _) = await ReadyAsync(100m);
        const string key = "retry-after-timeout-1";

        var first = await PayAsync(cashier, invoice, "card", 40m, key);
        var retry = await PayAsync(cashier, invoice, "card", 40m, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal((await JsonAsync(first)).GetProperty("id").GetGuid(), (await JsonAsync(retry)).GetProperty("id").GetGuid());
        Assert.Equal(40m, await PaidTotal(invoice));
        Assert.Equal(1, await PaymentRows(invoice));

        var different = await PayAsync(cashier, invoice, "card", 41m, key);   // eyni açar, fərqli məbləğ
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        Assert.Equal("payment.idempotency_key_reuse", (await JsonAsync(different)).GetProperty("code").GetString());
        Assert.Equal(40m, await PaidTotal(invoice));
    }

    [IntegrationFact]
    public async Task Idempotency_key_of_another_invoice_is_not_replayable()
    {
        var (cashier, invoice, patient) = await ReadyAsync(100m);
        var other = await IssuedInvoiceAsync(cashier, patient, f.BranchA, 100m);
        await PayAsync(cashier, invoice, "card", 10m, "shared-key");

        var response = await PayAsync(cashier, other, "card", 10m, "shared-key");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0m, await PaidTotal(other));
    }

    // ---------------- Concurrency ----------------
    [IntegrationFact]
    public async Task Parallel_payments_that_together_exceed_the_balance_let_exactly_one_through()
    {
        var (cashier, invoice, _) = await ReadyAsync(100m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => PayAsync(cashier, invoice, "card", 60m)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        Assert.Equal(60m, await PaidTotal(invoice));
        Assert.Equal(1, await PaymentRows(invoice));
    }

    [IntegrationFact]
    public async Task Parallel_payments_that_fit_are_all_applied_without_lost_updates()
    {
        var (cashier, invoice, _) = await ReadyAsync(100m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PayAsync(cashier, invoice, "card", 20m)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(100m, await PaidTotal(invoice));
        Assert.Equal("paid", await f.Db.ScalarAsync<string>(Demo, "SELECT status FROM invoices WHERE id = $1", invoice));
        Assert.Equal(5, await PaymentRows(invoice));
    }

    [IntegrationFact]
    public async Task Parallel_requests_with_the_same_key_create_one_payment()
    {
        var (cashier, invoice, _) = await ReadyAsync(100m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PayAsync(cashier, invoice, "card", 25m, "double-click")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Single((await Task.WhenAll(responses.Select(async r => (await JsonAsync(r)).GetProperty("id").GetGuid()))).Distinct());
        Assert.Equal(25m, await PaidTotal(invoice));
        Assert.Equal(1, await PaymentRows(invoice));
    }

    [IntegrationFact]
    public async Task Void_racing_with_a_payment_never_leaves_a_voided_invoice_with_money()
    {
        for (var round = 0; round < 5; round++)
        {
            var (cashier, invoice, _) = await ReadyAsync(100m);

            var results = await Task.WhenAll(PayAsync(cashier, invoice, "card", 50m), cashier.PostAsync($"/v1/invoices/{invoice}/void", null));

            var status = await f.Db.ScalarAsync<string>(Demo, "SELECT status FROM invoices WHERE id = $1", invoice);
            var paid = await PaidTotal(invoice);
            Assert.True((status == "void" && paid == 0m) || (status == "partially_paid" && paid == 50m), $"status={status} paid={paid}");
            Assert.Equal(1, results.Count(r => r.IsSuccessStatusCode));   // tam bir qalib
        }
    }
}
