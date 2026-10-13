namespace DentaCore.Clinical.Contracts;

/// <summary>
/// Routing key <c>clinical.procedure-performed</c>. Billing faktura sətri yaradır. Sahə əlavə etmək geriyə uyğundur,
/// silmək və ya adını dəyişmək yeni versiya tələb edir.
/// </summary>
public sealed record ProcedurePerformedV1(
    Guid EventId, DateTimeOffset OccurredAt, Guid PlanId, Guid PlanItemId, Guid VisitId, Guid PatientId, Guid ProviderId,
    string ProcedureCode, int? ToothFdi, int Quantity, decimal UnitPrice, decimal DiscountPercent);
