using DentaCore.BuildingBlocks.Domain;

namespace DentaCore.Scheduling.Domain;

public enum QueueStatus
{
    Waiting,
    Called,
    Serving,
    Done,
    Left,
}

/// <summary>Lobbi növbə bileti. Nömrə filial və gün üzrə ardıcıldır, atomik təyin olunur (infrastruktur).</summary>
public sealed class QueueTicket : Entity<Guid>
{
    private QueueTicket()
        : base(Guid.Empty)
    {
    }

    public Guid BranchId { get; private set; }

    public Guid? AppointmentId { get; private set; }

    public Guid? PatientId { get; private set; }

    public int TicketNo { get; private set; }

    public DateOnly QueueDate { get; private set; }

    public QueueStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CalledAt { get; private set; }

    public static QueueTicket Issue(Guid id, Guid branchId, Guid appointmentId, Guid patientId, int ticketNo, DateOnly date, DateTimeOffset now) =>
        new() { Id = id, BranchId = branchId, AppointmentId = appointmentId, PatientId = patientId, TicketNo = ticketNo, QueueDate = date, Status = QueueStatus.Waiting, CreatedAt = now };

    public Result Call(DateTimeOffset now)
    {
        if (Status != QueueStatus.Waiting)
        {
            return Error.Conflict("queue.invalid_state", $"A ticket in status '{Status}' cannot be called.");
        }

        Status = QueueStatus.Called;
        CalledAt = now;
        return Result.Success();
    }
}

public sealed class AppointmentReminder
{
    private AppointmentReminder()
    {
    }

    public Guid Id { get; private set; }

    public Guid AppointmentId { get; private set; }

    public string Channel { get; private set; } = string.Empty;

    public DateTimeOffset SendAt { get; private set; }

    public string Status { get; private set; } = "pending";

    public static AppointmentReminder Create(Guid appointmentId, string channel, DateTimeOffset sendAt) =>
        new() { Id = Guid.NewGuid(), AppointmentId = appointmentId, Channel = channel, SendAt = sendAt };
}

public sealed class WaitlistEntry
{
    private WaitlistEntry()
    {
    }

    public Guid Id { get; private set; }

    public Guid PatientId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid? ProviderId { get; private set; }

    public DateTimeOffset? Earliest { get; private set; }

    public DateTimeOffset? Latest { get; private set; }

    public int Priority { get; private set; }

    public static Result<WaitlistEntry> Create(Guid patientId, Guid branchId, Guid? providerId, DateTimeOffset? earliest, DateTimeOffset? latest, int priority)
    {
        if (priority is < 1 or > 9)
        {
            return Error.Validation("waitlist.invalid_priority", "Priority must be between 1 and 9.");
        }

        if (earliest is { } e && latest is { } l && l <= e)
        {
            return Error.Validation("waitlist.invalid_window", "Latest must be after earliest.");
        }

        return new WaitlistEntry { Id = Guid.NewGuid(), PatientId = patientId, BranchId = branchId, ProviderId = providerId, Earliest = earliest, Latest = latest, Priority = priority };
    }
}
