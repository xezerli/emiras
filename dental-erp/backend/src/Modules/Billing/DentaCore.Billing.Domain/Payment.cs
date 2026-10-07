using DentaCore.BuildingBlocks.Domain;

namespace DentaCore.Billing.Domain;

public enum PaymentKind
{
    Payment,
    Refund,
}

public static class PaymentMethods
{
    /// <summary>Hələlik dəstəklənən üsullar. gift_card, insurance, installment sxemdə var, amma əməliyyatı Faza 2-dədir.</summary>
    public static readonly IReadOnlyList<string> Supported = ["cash", "card", "transfer", "pos"];

    public static bool IsSupported(string method) => Supported.Contains(method, StringComparer.Ordinal);
}

/// <summary>Append-only ödəniş sətri (DB trigger-i update/delete-i qadağan edir). Düzəliş = əks əməliyyat.</summary>
public sealed class Payment : Entity<Guid>
{
    private Payment()
        : base(Guid.Empty)
    {
    }

    public Guid InvoiceId { get; private set; }

    public Guid PatientId { get; private set; }

    public Guid? ShiftId { get; private set; }

    public PaymentKind Kind { get; private set; }

    public string Method { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "AZN";

    public string? Reference { get; private set; }

    public string? IdempotencyKey { get; private set; }

    public Guid ReceivedBy { get; private set; }

    public DateTimeOffset PaidAt { get; private set; }

    public Guid? RefundOf { get; private set; }

    public string? Reason { get; private set; }

    public static Payment Receive(Guid id, Invoice invoice, Guid? shiftId, string method, decimal amount, string? reference, string idempotencyKey, Guid receivedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return new Payment
        {
            Id = id,
            InvoiceId = invoice.Id,
            PatientId = invoice.PatientId,
            ShiftId = shiftId,
            Kind = PaymentKind.Payment,
            Method = method,
            Amount = amount,
            Currency = invoice.Currency,
            Reference = reference,
            IdempotencyKey = idempotencyKey,
            ReceivedBy = receivedBy,
            PaidAt = now,
        };
    }

    public static Payment Refund(Guid id, Invoice invoice, Payment original, Guid? shiftId, decimal amount, string reason, string idempotencyKey, Guid refundedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(original);
        return new Payment
        {
            Id = id,
            InvoiceId = invoice.Id,
            PatientId = invoice.PatientId,
            ShiftId = shiftId,
            Kind = PaymentKind.Refund,
            Method = original.Method,
            Amount = amount,
            Currency = invoice.Currency,
            IdempotencyKey = idempotencyKey,
            ReceivedBy = refundedBy,
            PaidAt = now,
            RefundOf = original.Id,
            Reason = reason,
        };
    }
}
