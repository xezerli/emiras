using DentaCore.BuildingBlocks.Domain;

namespace DentaCore.Scheduling.Domain;

public enum AppointmentStatus
{
    Booked,
    Confirmed,
    CheckedIn,
    InProgress,
    Completed,
    Cancelled,
    NoShow,
    Rescheduled,
}

/// <summary>Yarı açıq zaman aralığı [Start, End): bitmə anı ilə başlama anı üst-üstə düşmür (ardıcıl qəbullar mümkündür).</summary>
public readonly record struct TimeSlot(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Duration => End - Start;

    public bool Overlaps(TimeSlot other) => Start < other.End && other.Start < End;
}

public sealed record AppointmentBooked(Guid EventId, DateTimeOffset OccurredAt, Guid AppointmentId, Guid PatientId, Guid ProviderId, Guid BranchId, DateTimeOffset Start, DateTimeOffset End)
    : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "scheduling.appointment-booked";
}

public sealed record AppointmentRescheduled(Guid EventId, DateTimeOffset OccurredAt, Guid AppointmentId, Guid PatientId, Guid ProviderId, DateTimeOffset OldStart, DateTimeOffset NewStart, DateTimeOffset NewEnd)
    : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "scheduling.appointment-rescheduled";
}

public sealed record AppointmentCancelled(Guid EventId, DateTimeOffset OccurredAt, Guid AppointmentId, Guid PatientId, Guid ProviderId, DateTimeOffset Start)
    : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "scheduling.appointment-cancelled";
}

public sealed record PatientCheckedIn(Guid EventId, DateTimeOffset OccurredAt, Guid AppointmentId, Guid PatientId, Guid ProviderId, Guid BranchId)
    : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "scheduling.patient-checked-in";
}

/// <summary>Pasiyent gəlmədi. Patient modulu no_show_count və risk skorunu bu hadisə ilə yeniləyəcək.</summary>
public sealed record AppointmentMissed(Guid EventId, DateTimeOffset OccurredAt, Guid AppointmentId, Guid PatientId, Guid ProviderId)
    : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "scheduling.appointment-missed";
}

public sealed class Appointment : AggregateRoot<Guid>
{
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(8);
    public static readonly TimeSpan CheckInOpensBefore = TimeSpan.FromHours(2);
    private static readonly TimeSpan PastGrace = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxHorizon = TimeSpan.FromDays(730);

    private static readonly string[] Sources = ["reception", "online", "phone", "ai", "app"];

    private Appointment()
        : base(Guid.Empty)
    {
    }

    public Guid BranchId { get; private set; }

    public Guid PatientId { get; private set; }

    public Guid ProviderId { get; private set; }

    public Guid? RoomId { get; private set; }

    public TimeSlot Slot { get; private set; }

    public AppointmentStatus Status { get; private set; }

    public string? Reason { get; private set; }

    public string Source { get; private set; } = "reception";

    public string? CancelReason { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public DateTimeOffset? CheckedInAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Slotu tutan (aktiv) statuslar. DB-dəki EXCLUDE constraint-in WHERE şərti ilə eyni olmalıdır.</summary>
    public bool HoldsSlot => Status is AppointmentStatus.Booked or AppointmentStatus.Confirmed or AppointmentStatus.CheckedIn or AppointmentStatus.InProgress;

    public static Result<Appointment> Book(
        Guid id, Guid branchId, Guid patientId, Guid providerId, Guid? roomId, TimeSlot slot, string? reason, string source, Guid createdBy, DateTimeOffset now)
    {
        var validation = ValidateSlot(slot, now);
        if (validation.IsFailure)
        {
            return validation.Error!;
        }

        if (!Sources.Contains(source))
        {
            return Error.Validation("appointment.invalid_source", "Unsupported appointment source.");
        }

        if (reason is { Length: > 500 })
        {
            return Error.Validation("appointment.reason_too_long", "Reason is too long (max 500).");
        }

        var appointment = new Appointment
        {
            Id = id,
            BranchId = branchId,
            PatientId = patientId,
            ProviderId = providerId,
            RoomId = roomId,
            Slot = slot,
            Status = AppointmentStatus.Booked,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            Source = source,
            CreatedBy = createdBy,
            CreatedAt = now,
        };
        appointment.Raise(new AppointmentBooked(Guid.NewGuid(), now, id, patientId, providerId, branchId, slot.Start, slot.End));
        return appointment;
    }

    /// <summary>Vaxt/həkim/otaq dəyişikliyi (drag &amp; drop). Yalnız hələ başlamamış (booked/confirmed) qəbul köçürülə bilər.</summary>
    public Result Reschedule(Guid providerId, Guid? roomId, TimeSlot slot, string? reason, DateTimeOffset now)
    {
        if (Status is not (AppointmentStatus.Booked or AppointmentStatus.Confirmed))
        {
            return Error.Conflict("appointment.not_reschedulable", $"An appointment in status '{Status}' cannot be changed.");
        }

        var validation = ValidateSlot(slot, now);
        if (validation.IsFailure)
        {
            return validation;
        }

        if (reason is { Length: > 500 })
        {
            return Error.Validation("appointment.reason_too_long", "Reason is too long (max 500).");
        }

        var oldStart = Slot.Start;
        var timeChanged = slot != Slot;
        ProviderId = providerId;
        RoomId = roomId;
        Slot = slot;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (timeChanged)
        {
            Raise(new AppointmentRescheduled(Guid.NewGuid(), now, Id, PatientId, providerId, oldStart, slot.Start, slot.End));
        }

        return Result.Success();
    }

    public Result CheckIn(DateTimeOffset now)
    {
        if (Status is not (AppointmentStatus.Booked or AppointmentStatus.Confirmed))
        {
            return Error.Conflict("appointment.invalid_state", $"Cannot check in an appointment in status '{Status}'.");
        }

        if (now < Slot.Start - CheckInOpensBefore || now >= Slot.End)
        {
            return Error.Conflict("appointment.checkin_window", "Check-in is possible from 2 hours before the start until the end of the appointment.");
        }

        Status = AppointmentStatus.CheckedIn;
        CheckedInAt = now;
        Raise(new PatientCheckedIn(Guid.NewGuid(), now, Id, PatientId, ProviderId, BranchId));
        return Result.Success();
    }

    /// <summary>Vizit başlayır. Qəbul gəlməmiş (booked/confirmed) də başlana bilər: gəlişi qeyd olunmamış, amma həkimin yanındadır.</summary>
    public Result Start(DateTimeOffset now)
    {
        if (Status is not (AppointmentStatus.Booked or AppointmentStatus.Confirmed or AppointmentStatus.CheckedIn))
        {
            return Error.Conflict("appointment.invalid_state", $"Cannot start a visit for an appointment in status '{Status}'.");
        }

        if (Status != AppointmentStatus.CheckedIn)
        {
            CheckedInAt = now;
        }

        Status = AppointmentStatus.InProgress;
        return Result.Success();
    }

    public Result Complete()
    {
        if (Status != AppointmentStatus.InProgress)
        {
            return Error.Conflict("appointment.invalid_state", $"Cannot complete an appointment in status '{Status}'.");
        }

        Status = AppointmentStatus.Completed;
        return Result.Success();
    }

    public Result Cancel(string? reason, DateTimeOffset now)
    {
        if (Status is not (AppointmentStatus.Booked or AppointmentStatus.Confirmed or AppointmentStatus.CheckedIn))
        {
            return Error.Conflict("appointment.invalid_state", $"Cannot cancel an appointment in status '{Status}'.");
        }

        Status = AppointmentStatus.Cancelled;
        CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 500)];
        CancelledAt = now;
        Raise(new AppointmentCancelled(Guid.NewGuid(), now, Id, PatientId, ProviderId, Slot.Start));
        return Result.Success();
    }

    public Result MarkNoShow(DateTimeOffset now)
    {
        if (Status is not (AppointmentStatus.Booked or AppointmentStatus.Confirmed))
        {
            return Error.Conflict("appointment.invalid_state", $"Cannot mark an appointment in status '{Status}' as no-show.");
        }

        if (now < Slot.Start)
        {
            return Error.Conflict("appointment.too_early", "An appointment cannot be a no-show before it starts.");
        }

        Status = AppointmentStatus.NoShow;
        Raise(new AppointmentMissed(Guid.NewGuid(), now, Id, PatientId, ProviderId));
        return Result.Success();
    }

    private static Result ValidateSlot(TimeSlot slot, DateTimeOffset now)
    {
        if (slot.End <= slot.Start)
        {
            return Error.Validation("appointment.invalid_period", "End must be after start.");
        }

        if (slot.Duration < MinDuration || slot.Duration > MaxDuration)
        {
            return Error.Validation("appointment.invalid_duration", "Duration must be between 5 minutes and 8 hours.");
        }

        if (slot.Start < now - PastGrace)
        {
            return Error.Validation("appointment.in_the_past", "An appointment cannot start in the past.");
        }

        if (slot.Start > now + MaxHorizon)
        {
            return Error.Validation("appointment.too_far", "An appointment cannot be booked more than 2 years ahead.");
        }

        return Result.Success();
    }
}
