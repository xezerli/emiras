using DentaCore.Billing.Domain;

namespace DentaCore.Billing.UnitTests;

public sealed class InvoiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 3, 10);

    private static NewInvoiceItem Item(decimal price = 100m, int qty = 1, decimal discount = 0m, decimal vat = 0m, string description = "Plomb") =>
        new(null, null, description, 16, null, qty, price, discount, vat);

    private static Invoice Draft(params NewInvoiceItem[] items) =>
        Invoice.Create(Guid.NewGuid(), InvoiceKind.Invoice, Guid.NewGuid(), Guid.NewGuid(), null, null, null, "AZN", null, null, items.Length == 0 ? [Item()] : items, null, Now).Value;

    private static Invoice Issued(params NewInvoiceItem[] items)
    {
        var invoice = Draft(items);
        Assert.True(invoice.Issue(Now, Today).IsSuccess);
        invoice.ClearDomainEvents();
        return invoice;
    }

    // ---------------- Totals ----------------
    [Fact]
    public void Totals_are_subtotal_minus_discount_plus_tax()
    {
        var invoice = Draft(Item(price: 50m, qty: 3, discount: 20m, vat: 18m), Item(price: 10m));

        Assert.Equal(160m, invoice.Subtotal);
        Assert.Equal(20m, invoice.DiscountTotal);
        Assert.Equal(23.4m, invoice.TaxTotal);   // (150 - 20) * 18% = 23.40, ikinci sətir vergisiz
        Assert.Equal(163.4m, invoice.Total);
        Assert.Equal(163.4m, invoice.Balance);
    }

    [Fact]
    public void Tax_is_rounded_half_away_from_zero_per_line()
    {
        var invoice = Draft(Item(price: 0.05m, vat: 10m), Item(price: 0.05m, vat: 10m));

        Assert.Equal(0.02m, invoice.TaxTotal);   // hər sətir 0.005 → 0.01
    }

    [Fact]
    public void Provider_defaults_to_the_first_item_provider()
    {
        var doctor = Guid.NewGuid();
        var invoice = Draft(new NewInvoiceItem(null, null, "Plomb", 16, doctor, 1, 10m, 0m, 0m));

        Assert.Equal(doctor, invoice.ProviderId);
    }

    // ---------------- Validation ----------------
    [Theory]
    [InlineData(10.001)]
    [InlineData(-1)]
    public void Item_rejects_bad_price(double price)
    {
        var result = Invoice.Create(Guid.NewGuid(), InvoiceKind.Invoice, Guid.NewGuid(), Guid.NewGuid(), null, null, null, "AZN", null, null, [Item(price: (decimal)price)], null, Now);

        Assert.Equal("invoice.invalid_amount", result.Error!.Code);
    }

    [Fact]
    public void Discount_cannot_exceed_the_line()
    {
        var result = Invoice.Create(Guid.NewGuid(), InvoiceKind.Invoice, Guid.NewGuid(), Guid.NewGuid(), null, null, null, "AZN", null, null, [Item(price: 10m, qty: 2, discount: 20.01m)], null, Now);

        Assert.Equal("invoice.discount_exceeds_line", result.Error!.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void Quantity_must_be_in_range(int qty)
    {
        var result = Invoice.Create(Guid.NewGuid(), InvoiceKind.Invoice, Guid.NewGuid(), Guid.NewGuid(), null, null, null, "AZN", null, null, [Item(qty: qty)], null, Now);

        Assert.Equal("invoice.invalid_item", result.Error!.Code);
    }

    [Fact]
    public void Invoice_needs_between_one_and_max_items()
    {
        var none = Invoice.Create(Guid.NewGuid(), InvoiceKind.Invoice, Guid.NewGuid(), Guid.NewGuid(), null, null, null, "AZN", null, null, [], null, Now);
        var tooMany = Invoice.Create(Guid.NewGuid(), InvoiceKind.Invoice, Guid.NewGuid(), Guid.NewGuid(), null, null, null, "AZN", null, null, Enumerable.Repeat(Item(), Invoice.MaxItems + 1).ToList(), null, Now);

        Assert.Equal("invoice.invalid_items", none.Error!.Code);
        Assert.Equal("invoice.invalid_items", tooMany.Error!.Code);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(86)]
    public void Tooth_must_be_a_valid_fdi_number(int tooth)
    {
        var result = Invoice.Create(Guid.NewGuid(), InvoiceKind.Invoice, Guid.NewGuid(), Guid.NewGuid(), null, null, null, "AZN", null, null,
            [new NewInvoiceItem(null, null, "x", tooth, null, 1, 1m, 0m, 0m)], null, Now);

        Assert.Equal("invoice.invalid_tooth", result.Error!.Code);
    }

    // ---------------- Issue / void ----------------
    [Fact]
    public void Issue_sets_status_due_date_and_raises_event()
    {
        var invoice = Draft(Item(price: 80m));

        var result = invoice.Issue(Now, Today);

        Assert.True(result.IsSuccess);
        Assert.Equal(InvoiceStatus.Issued, invoice.Status);
        Assert.Equal(Today, invoice.DueDate);
        Assert.Equal(Now, invoice.IssuedAt);
        var issued = Assert.IsType<InvoiceIssued>(Assert.Single(invoice.DomainEvents));
        Assert.Equal(80m, issued.Total);
    }

    [Fact]
    public void Issue_keeps_an_explicit_due_date()
    {
        var due = new DateOnly(2026, 4, 1);
        var invoice = Invoice.Create(Guid.NewGuid(), InvoiceKind.Invoice, Guid.NewGuid(), Guid.NewGuid(), null, null, null, "AZN", due, null, [Item()], null, Now).Value;

        invoice.Issue(Now, Today);

        Assert.Equal(due, invoice.DueDate);
    }

    [Fact]
    public void Estimate_cannot_be_issued()
    {
        var estimate = Invoice.Create(Guid.NewGuid(), InvoiceKind.Estimate, Guid.NewGuid(), Guid.NewGuid(), null, null, null, "AZN", null, null, [Item()], null, Now).Value;

        Assert.Equal("invoice.estimate_not_issuable", estimate.Issue(Now, Today).Error!.Code);
    }

    [Fact]
    public void Zero_total_cannot_be_issued()
    {
        var invoice = Draft(Item(price: 0m));

        Assert.Equal("invoice.empty_total", invoice.Issue(Now, Today).Error!.Code);
    }

    [Fact]
    public void Issuing_twice_is_a_conflict()
    {
        var invoice = Issued();

        Assert.Equal("invoice.invalid_state", invoice.Issue(Now, Today).Error!.Code);
    }

    [Fact]
    public void Items_are_frozen_after_issue()
    {
        var invoice = Issued();

        Assert.Equal("invoice.not_draft", invoice.AddItem(Item()).Error!.Code);
    }

    [Fact]
    public void Void_works_for_draft_and_unpaid_issued_only()
    {
        var draft = Draft();
        var issued = Issued();
        var partlyPaid = Issued(Item(price: 100m));
        partlyPaid.ApplyPayment(Guid.NewGuid(), 10m, "cash", Now);

        Assert.True(draft.Void(Now).IsSuccess);
        Assert.True(issued.Void(Now).IsSuccess);
        Assert.Equal(InvoiceStatus.Void, issued.Status);
        Assert.Equal("invoice.invalid_state", partlyPaid.Void(Now).Error!.Code);   // PartiallyPaid statusu
        Assert.Equal("invoice.invalid_state", issued.Void(Now).Error!.Code);       // ikinci dəfə
    }

    [Fact]
    public void Void_is_allowed_again_once_everything_was_refunded()
    {
        var invoice = Issued(Item(price: 100m));
        invoice.ApplyPayment(Guid.NewGuid(), 100m, "cash", Now);
        invoice.ApplyRefund(Guid.NewGuid(), Guid.NewGuid(), 100m, Now);   // tam geri qaytarıldı → yenə Issued, PaidTotal=0

        Assert.Equal(InvoiceStatus.Issued, invoice.Status);
        Assert.True(invoice.Void(Now).IsSuccess);   // pul qalmayıb: ləğv mümkündür
    }

    // ---------------- Payments ----------------
    [Fact]
    public void Partial_then_full_payment_moves_status_through_partially_paid_to_paid()
    {
        var invoice = Issued(Item(price: 100m));

        Assert.True(invoice.ApplyPayment(Guid.NewGuid(), 30m, "cash", Now).IsSuccess);
        Assert.Equal(InvoiceStatus.PartiallyPaid, invoice.Status);
        Assert.Equal(70m, invoice.Balance);

        Assert.True(invoice.ApplyPayment(Guid.NewGuid(), 70m, "card", Now).IsSuccess);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(0m, invoice.Balance);
        var events = invoice.DomainEvents.OfType<PaymentReceived>().ToList();
        Assert.Equal([false, true], events.Select(e => e.InvoiceFullyPaid));
    }

    [Fact]
    public void Overpayment_is_refused_with_the_balance_in_details()
    {
        var invoice = Issued(Item(price: 100m));

        var result = invoice.ApplyPayment(Guid.NewGuid(), 100.01m, "cash", Now);

        Assert.Equal("billing.overpayment", result.Error!.Code);
        Assert.Equal(100m, result.Error.Details!["balance"]);
        Assert.Equal(0m, invoice.PaidTotal);
        Assert.Empty(invoice.DomainEvents);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1.005)]
    public void Payment_amount_must_be_positive_with_two_decimals(double amount)
    {
        var invoice = Issued(Item(price: 100m));

        Assert.Equal("payment.invalid_amount", invoice.ApplyPayment(Guid.NewGuid(), (decimal)amount, "cash", Now).Error!.Code);
    }

    [Fact]
    public void Payment_on_draft_or_void_or_paid_invoice_is_a_conflict()
    {
        var draft = Draft();
        var voided = Issued();
        voided.Void(Now);
        var paid = Issued(Item(price: 10m));
        paid.ApplyPayment(Guid.NewGuid(), 10m, "cash", Now);

        Assert.Equal("invoice.invalid_state", draft.ApplyPayment(Guid.NewGuid(), 1m, "cash", Now).Error!.Code);
        Assert.Equal("invoice.invalid_state", voided.ApplyPayment(Guid.NewGuid(), 1m, "cash", Now).Error!.Code);
        Assert.Equal("invoice.invalid_state", paid.ApplyPayment(Guid.NewGuid(), 1m, "cash", Now).Error!.Code);
    }

    // ---------------- Refunds ----------------
    [Fact]
    public void Partial_refund_reinstates_debt_and_full_refund_returns_to_issued()
    {
        var invoice = Issued(Item(price: 100m));
        invoice.ApplyPayment(Guid.NewGuid(), 100m, "cash", Now);

        Assert.True(invoice.ApplyRefund(Guid.NewGuid(), Guid.NewGuid(), 40m, Now).IsSuccess);
        Assert.Equal(InvoiceStatus.PartiallyPaid, invoice.Status);
        Assert.Equal(40m, invoice.Balance);

        Assert.True(invoice.ApplyRefund(Guid.NewGuid(), Guid.NewGuid(), 60m, Now).IsSuccess);
        Assert.Equal(InvoiceStatus.Issued, invoice.Status);
        Assert.Equal(100m, invoice.Balance);
        Assert.Equal(2, invoice.DomainEvents.OfType<PaymentRefunded>().Count());
    }

    [Fact]
    public void Refund_cannot_exceed_paid_total_or_apply_to_an_unpaid_invoice()
    {
        var unpaid = Issued();
        var invoice = Issued(Item(price: 100m));
        invoice.ApplyPayment(Guid.NewGuid(), 50m, "cash", Now);

        Assert.Equal("invoice.invalid_state", unpaid.ApplyRefund(Guid.NewGuid(), Guid.NewGuid(), 1m, Now).Error!.Code);
        Assert.Equal("payment.invalid_amount", invoice.ApplyRefund(Guid.NewGuid(), Guid.NewGuid(), 50.01m, Now).Error!.Code);
        Assert.Equal("payment.invalid_amount", invoice.ApplyRefund(Guid.NewGuid(), Guid.NewGuid(), 0m, Now).Error!.Code);
        Assert.Equal(50m, invoice.PaidTotal);
    }
}
