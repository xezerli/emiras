using DentaCore.BuildingBlocks.Domain;

namespace DentaCore.Billing.Domain;

public enum InvoiceKind
{
    Estimate,
    Invoice,
}

public enum InvoiceStatus
{
    Draft,
    Issued,
    PartiallyPaid,
    Paid,
    Void,
    Refunded,
}

public sealed record InvoiceIssued(Guid EventId, DateTimeOffset OccurredAt, Guid InvoiceId, Guid PatientId, Guid BranchId, decimal Total, string Currency)
    : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "billing.invoice-issued";
}

public sealed record InvoiceVoided(Guid EventId, DateTimeOffset OccurredAt, Guid InvoiceId, Guid PatientId, Guid BranchId)
    : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "billing.invoice-voided";
}

/// <summary>Accounting/Dashboard/CRM bunu dinləyəcək. PaymentId ödəniş sətrinin id-sidir.</summary>
public sealed record PaymentReceived(
    Guid EventId, DateTimeOffset OccurredAt, Guid InvoiceId, Guid PaymentId, Guid PatientId, Guid BranchId, decimal Amount, string Method, string Currency, bool InvoiceFullyPaid)
    : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "billing.payment-received";
}

public sealed record PaymentRefunded(
    Guid EventId, DateTimeOffset OccurredAt, Guid InvoiceId, Guid PaymentId, Guid RefundOfPaymentId, Guid PatientId, Guid BranchId, decimal Amount, string Currency)
    : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "billing.payment-refunded";
}

public sealed record NewInvoiceItem(
    Guid? ServiceId, Guid? PlanItemId, string Description, int? ToothFdi, Guid? ProviderId, int Quantity, decimal UnitPrice, decimal Discount, decimal VatRate);

public sealed class InvoiceItem : Entity<Guid>
{
    private InvoiceItem()
        : base(Guid.Empty)
    {
    }

    public Guid InvoiceId { get; private set; }

    public Guid? ServiceId { get; private set; }

    public Guid? PlanItemId { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public int? ToothFdi { get; private set; }

    public Guid? ProviderId { get; private set; }

    public int Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal Discount { get; private set; }

    public decimal VatRate { get; private set; }

    /// <summary>Endirimdən sonra, vergisiz. DB-də generated column ilə eynidir (quantity * unit_price - discount).</summary>
    public decimal LineTotal => (Quantity * UnitPrice) - Discount;

    public decimal LineTax => Money.Round(LineTotal * VatRate / 100m);

    internal static Result<InvoiceItem> Create(Guid invoiceId, NewInvoiceItem n)
    {
        if (string.IsNullOrWhiteSpace(n.Description) || n.Description.Trim().Length > 500)
        {
            return Error.Validation("invoice.invalid_item", "Item description is required (max 500 characters).");
        }

        if (n.Quantity is < 1 or > 1000)
        {
            return Error.Validation("invoice.invalid_item", "Quantity must be between 1 and 1000.");
        }

        if (!Money.IsValidAmount(n.UnitPrice) || n.UnitPrice < 0 || !Money.IsValidAmount(n.Discount) || n.Discount < 0)
        {
            return Error.Validation("invoice.invalid_amount", "Amounts must be non-negative with at most 2 decimal places.");
        }

        if (n.Discount > n.Quantity * n.UnitPrice)
        {
            return Error.Validation("invoice.discount_exceeds_line", "The discount cannot exceed the line amount.");
        }

        if (n.VatRate is < 0 or > 100)
        {
            return Error.Validation("invoice.invalid_vat", "VAT rate must be between 0 and 100.");
        }

        if (n.ToothFdi is { } tooth && (tooth < 11 || tooth > 85))
        {
            return Error.Validation("invoice.invalid_tooth", "Tooth number must be a valid FDI number.");
        }

        return new InvoiceItem
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            ServiceId = n.ServiceId,
            PlanItemId = n.PlanItemId,
            Description = n.Description.Trim(),
            ToothFdi = n.ToothFdi,
            ProviderId = n.ProviderId,
            Quantity = n.Quantity,
            UnitPrice = n.UnitPrice,
            Discount = n.Discount,
            VatRate = n.VatRate,
        };
    }
}

/// <summary>
/// Faktura aggregate-i. Qaydalar: məbləğlər 2 onluq; qaralamada sətir əlavə olunur, rəsmiləşdirildikdən sonra sətirlər dəyişməzdir;
/// ödəniş qalıq borcu aşa bilməz; geri qaytarma ödənilmiş məbləği azaldır və borcu bərpa edir.
/// Qiymətlər vergisizdir: vergi = sətir cəmi × stavka, yekun = cəm + vergi.
/// </summary>
public sealed class Invoice : AggregateRoot<Guid>
{
    public const int MaxItems = 200;

    private readonly List<InvoiceItem> _items = [];

    private Invoice()
        : base(Guid.Empty)
    {
    }

    public string Number { get; private set; } = null!;   // DB default (ardıcıl nömrə) təyin edir

    public InvoiceKind Kind { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid PatientId { get; private set; }

    public Guid? ProviderId { get; private set; }

    public Guid? VisitId { get; private set; }

    public Guid? PlanId { get; private set; }

    public InvoiceStatus Status { get; private set; }

    public string Currency { get; private set; } = "AZN";

    public decimal Subtotal { get; private set; }

    public decimal DiscountTotal { get; private set; }

    public decimal TaxTotal { get; private set; }

    public decimal Total { get; private set; }

    public decimal PaidTotal { get; private set; }

    public decimal InsuranceAmount { get; private set; }

    public DateTimeOffset? IssuedAt { get; private set; }

    public DateOnly? DueDate { get; private set; }

    public string? Notes { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<InvoiceItem> Items => _items;

    public decimal Balance => Total - InsuranceAmount - PaidTotal;

    public static Result<Invoice> Create(
        Guid id, InvoiceKind kind, Guid branchId, Guid patientId, Guid? providerId, Guid? visitId, Guid? planId, string currency,
        DateOnly? dueDate, string? notes, IReadOnlyCollection<NewInvoiceItem> items, Guid? createdBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count is 0 or > MaxItems)
        {
            return Error.Validation("invoice.invalid_items", $"An invoice needs between 1 and {MaxItems} items.");
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            return Error.Validation("invoice.invalid_currency", "Currency must be a 3-letter code.");
        }

        var invoice = new Invoice
        {
            Id = id,
            Kind = kind,
            BranchId = branchId,
            PatientId = patientId,
            ProviderId = providerId,
            VisitId = visitId,
            PlanId = planId,
            Currency = currency.ToUpperInvariant(),
            Status = InvoiceStatus.Draft,
            DueDate = dueDate,
            Notes = notes,
            CreatedBy = createdBy,
            CreatedAt = now,
        };
        foreach (var item in items)
        {
            var added = invoice.AddItem(item);
            if (added.IsFailure)
            {
                return added.Error!;
            }
        }

        return invoice;
    }

    public Result AddItem(NewInvoiceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Status != InvoiceStatus.Draft)
        {
            return Error.Conflict("invoice.not_draft", "Items can only be added to a draft invoice.");
        }

        if (_items.Count >= MaxItems)
        {
            return Error.Validation("invoice.too_many_items", $"An invoice can have at most {MaxItems} items.");
        }

        var created = InvoiceItem.Create(Id, item);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        _items.Add(created.Value);
        Recalculate();
        return Result.Success();
    }

    public Result Issue(DateTimeOffset now, DateOnly today)
    {
        if (Kind != InvoiceKind.Invoice)
        {
            return Error.Validation("invoice.estimate_not_issuable", "An estimate cannot be issued; create an invoice instead.");
        }

        if (Status != InvoiceStatus.Draft)
        {
            return Error.Conflict("invoice.invalid_state", $"Only a draft invoice can be issued (current: {Status}).");
        }

        if (Total <= 0)
        {
            return Error.Validation("invoice.empty_total", "An invoice with a zero total cannot be issued.");
        }

        Status = InvoiceStatus.Issued;
        IssuedAt = now;
        DueDate ??= today;
        Raise(new InvoiceIssued(Guid.NewGuid(), now, Id, PatientId, BranchId, Total, Currency));
        return Result.Success();
    }

    public Result Void(DateTimeOffset now)
    {
        if (Status is not (InvoiceStatus.Draft or InvoiceStatus.Issued))
        {
            return Error.Conflict("invoice.invalid_state", $"Only a draft or unpaid issued invoice can be voided (current: {Status}).");
        }

        if (PaidTotal > 0)
        {
            return Error.Conflict("invoice.has_payments", "Refund the payments before voiding the invoice.");
        }

        Status = InvoiceStatus.Void;
        Raise(new InvoiceVoided(Guid.NewGuid(), now, Id, PatientId, BranchId));
        return Result.Success();
    }

    public Result ApplyPayment(Guid paymentId, decimal amount, string method, DateTimeOffset now)
    {
        if (Status is not (InvoiceStatus.Issued or InvoiceStatus.PartiallyPaid))
        {
            return Error.Conflict("invoice.invalid_state", $"Payments can only be taken on an issued invoice (current: {Status}).");
        }

        if (amount <= 0 || !Money.IsValidAmount(amount))
        {
            return Error.Validation("payment.invalid_amount", "The amount must be positive with at most 2 decimal places.");
        }

        if (amount > Balance)
        {
            return Error.Validation("billing.overpayment", "The amount exceeds the outstanding balance.") with
            {
                Details = new Dictionary<string, object?> { ["balance"] = Balance },
            };
        }

        PaidTotal += amount;
        Status = Balance == 0 ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
        Raise(new PaymentReceived(Guid.NewGuid(), now, Id, paymentId, PatientId, BranchId, amount, method, Currency, Status == InvoiceStatus.Paid));
        return Result.Success();
    }

    /// <summary>Geri qaytarma: ödənilmiş məbləğ azalır, borc bərpa olunur. Xidmət ləğv olunursa əvvəl geri qaytarılır, sonra faktura ləğv edilir.</summary>
    public Result ApplyRefund(Guid refundPaymentId, Guid refundOfPaymentId, decimal amount, DateTimeOffset now)
    {
        if (Status is not (InvoiceStatus.PartiallyPaid or InvoiceStatus.Paid))
        {
            return Error.Conflict("invoice.invalid_state", $"Only a paid invoice can be refunded (current: {Status}).");
        }

        if (amount <= 0 || !Money.IsValidAmount(amount) || amount > PaidTotal)
        {
            return Error.Validation("payment.invalid_amount", "The refund must be positive, with at most 2 decimal places, and not exceed the paid total.");
        }

        PaidTotal -= amount;
        Status = PaidTotal == 0 ? InvoiceStatus.Issued : InvoiceStatus.PartiallyPaid;
        Raise(new PaymentRefunded(Guid.NewGuid(), now, Id, refundPaymentId, refundOfPaymentId, PatientId, BranchId, amount, Currency));
        return Result.Success();
    }

    private void Recalculate()
    {
        Subtotal = _items.Sum(i => i.Quantity * i.UnitPrice);
        DiscountTotal = _items.Sum(i => i.Discount);
        TaxTotal = _items.Sum(i => i.LineTax);
        Total = Subtotal - DiscountTotal + TaxTotal;
        ProviderId ??= _items.Select(i => i.ProviderId).FirstOrDefault(p => p is not null);
    }
}
