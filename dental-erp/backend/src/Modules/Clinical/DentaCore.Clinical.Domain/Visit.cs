using DentaCore.BuildingBlocks.Domain;

namespace DentaCore.Clinical.Domain;

public enum VisitStatus
{
    Open,
    Closed,
}

public sealed record VisitStarted(Guid EventId, DateTimeOffset OccurredAt, Guid VisitId, Guid PatientId, Guid ProviderId, Guid BranchId, Guid? AppointmentId)
    : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "clinical.visit-started";
}

/// <summary>Billing bu hadisə ilə invoice qaralamasını tamamlayır (Mərhələ 3 §3.3 saga).</summary>
public sealed record VisitClosed(Guid EventId, DateTimeOffset OccurredAt, Guid VisitId, Guid PatientId, Guid ProviderId, Guid BranchId, Guid? AppointmentId, DateTimeOffset StartedAt, DateTimeOffset EndedAt)
    : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "clinical.visit-closed";
}

public sealed class Visit : AggregateRoot<Guid>
{
    private Visit()
        : base(Guid.Empty)
    {
    }

    public Guid PatientId { get; private set; }

    public Guid? AppointmentId { get; private set; }

    public Guid ProviderId { get; private set; }

    public Guid BranchId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public string? ChiefComplaint { get; private set; }

    public VisitStatus Status { get; private set; }

    public static Result<Visit> Start(Guid id, Guid patientId, Guid providerId, Guid branchId, Guid? appointmentId, string? complaint, DateTimeOffset now)
    {
        if (complaint is { Length: > 1000 })
        {
            return Error.Validation("visit.complaint_too_long", "Chief complaint is too long (max 1000).");
        }

        var visit = new Visit
        {
            Id = id,
            PatientId = patientId,
            ProviderId = providerId,
            BranchId = branchId,
            AppointmentId = appointmentId,
            ChiefComplaint = string.IsNullOrWhiteSpace(complaint) ? null : complaint.Trim(),
            StartedAt = now,
            Status = VisitStatus.Open,
        };
        visit.Raise(new VisitStarted(Guid.NewGuid(), now, id, patientId, providerId, branchId, appointmentId));
        return visit;
    }

    public Result Close(DateTimeOffset now)
    {
        if (Status == VisitStatus.Closed)
        {
            return Error.Conflict("visit.already_closed", "The visit is already closed.");
        }

        Status = VisitStatus.Closed;
        EndedAt = now;
        Raise(new VisitClosed(Guid.NewGuid(), now, Id, PatientId, ProviderId, BranchId, AppointmentId, StartedAt, now));
        return Result.Success();
    }
}
