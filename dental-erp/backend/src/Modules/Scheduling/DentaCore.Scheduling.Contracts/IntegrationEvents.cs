namespace DentaCore.Scheduling.Contracts;

/// <summary>
/// Routing key <c>scheduling.appointment-missed</c>. Başqa modullar Scheduling.Domain-ə istinad etmədən bu müqaviləni oxuyur;
/// sahə əlavə etmək geriyə uyğundur, silmək/adını dəyişmək yeni versiya tələb edir.
/// </summary>
public sealed record AppointmentMissedV1(Guid EventId, DateTimeOffset OccurredAt, Guid AppointmentId, Guid PatientId, Guid ProviderId);
