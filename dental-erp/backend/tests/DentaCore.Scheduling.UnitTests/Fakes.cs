using DentaCore.BuildingBlocks.Application;
using DentaCore.Patient.Contracts;
using DentaCore.Scheduling.Application;
using DentaCore.Scheduling.Domain;
using Microsoft.Extensions.Options;

namespace DentaCore.Scheduling.UnitTests;

internal sealed class FakeClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = start;
}

internal sealed class FakeUow : ISchedulingUnitOfWork
{
    public System.Reflection.Assembly ApplicationAssembly => typeof(ISchedulingUnitOfWork).Assembly;

    public int Saves { get; private set; }

    public int Commits { get; private set; }

    /// <summary>DB constraint pozuntusunu təqlid edir (double-booking, FK).</summary>
    public ConstraintViolationException? ThrowOnSave { get; set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Saves++;
        return ThrowOnSave is { } ex ? throw ex : Task.FromResult(0);
    }

    public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IUnitOfWorkTransaction>(new Tx(this));

    private sealed class Tx(FakeUow owner) : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            owner.Commits++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FakeRepo : ISchedulingRepository
{
    public List<Appointment> Appointments { get; } = [];

    public List<AppointmentReminder> Reminders { get; } = [];

    public List<QueueTicket> Tickets { get; } = [];

    public List<WaitlistEntry> Waitlist { get; } = [];

    public List<Guid> RemindersCancelledFor { get; } = [];

    public int NextTicket { get; set; } = 1;

    public void Add(Appointment appointment) => Appointments.Add(appointment);

    public void AddTicket(QueueTicket ticket) => Tickets.Add(ticket);

    public void AddReminders(IEnumerable<AppointmentReminder> reminders) => Reminders.AddRange(reminders);

    public void AddWaitlist(WaitlistEntry entry) => Waitlist.Add(entry);

    public Task<Appointment?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Appointments.FirstOrDefault(a => a.Id == id));

    public Task<QueueTicket?> GetTicketAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Tickets.FirstOrDefault(t => t.Id == id));

    public Task<int> NextTicketNumberAsync(Guid branchId, DateOnly date, CancellationToken cancellationToken) => Task.FromResult(NextTicket++);

    public Task CancelPendingRemindersAsync(Guid appointmentId, CancellationToken cancellationToken)
    {
        RemindersCancelledFor.Add(appointmentId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeRead : ISchedulingReadModel
{
    public ProviderDay Day { get; set; } = new(false, [], [], []);

    public Dictionary<Guid, Guid> RoomBranches { get; } = [];

    public AppointmentQuery? LastQuery { get; private set; }

    public List<AppointmentRow> Rows { get; } = [];

    public List<QueueRow> Queue { get; } = [];

    public Task<ProviderDay> GetProviderDayAsync(Guid providerId, DateOnly localDate, TimeZoneInfo zone, Guid? excludeAppointmentId, CancellationToken cancellationToken) => Task.FromResult(Day);

    public Task<IReadOnlyList<AppointmentRow>> ListAsync(AppointmentQuery query, CancellationToken cancellationToken)
    {
        LastQuery = query;
        return Task.FromResult<IReadOnlyList<AppointmentRow>>(Rows);
    }

    public Task<Guid?> GetRoomBranchAsync(Guid roomId, CancellationToken cancellationToken) =>
        Task.FromResult<Guid?>(RoomBranches.TryGetValue(roomId, out var b) ? b : null);

    public Task<IReadOnlyList<QueueRow>> ListQueueAsync(Guid branchId, DateOnly date, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<QueueRow>>(Queue);
}

internal sealed class FakePatients : IPatientDirectory
{
    public Dictionary<Guid, PatientRef> Items { get; } = [];

    public Task<PatientRef?> FindAsync(Guid patientId, CancellationToken cancellationToken) => Task.FromResult(Items.GetValueOrDefault(patientId));

    public Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IReadOnlyCollection<Guid> patientIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(Items.Where(i => patientIds.Contains(i.Key)).ToDictionary(i => i.Key, i => i.Value.FullName));
}

internal static class Env
{
    public static readonly DateTimeOffset Now = new(2026, 10, 7, 6, 0, 0, TimeSpan.Zero);   // Bakı 10:00
    public static readonly Guid BranchA = Guid.NewGuid();
    public static readonly Guid BranchB = Guid.NewGuid();
    public static readonly Guid Provider = Guid.NewGuid();
    public static readonly TimeZoneInfo Baku = TimeZoneInfo.FindSystemTimeZoneById("Asia/Baku");

    public static IOptions<SchedulingOptions> Opts() => Options.Create(new SchedulingOptions());

    public static CurrentUser User(Guid? id = null, string[]? perms = null, string[]? branches = null) =>
        new(id ?? Guid.NewGuid(), perms ?? ["appointment:write@tenant", "appointment:read@tenant"], branches ?? ["*"], null);

    public static PatientRef Patient(string channel = "sms") => new(Guid.NewGuid(), BranchA, null, "Qasımova Aysel", channel);

    public static DateTimeOffset LocalToday(int hour, int min = 0) => SchedulePlanner.ToUtc(new DateOnly(2026, 10, 8), new TimeOnly(hour, min), Baku);
}
