using DentaCore.BuildingBlocks.Application;
using DentaCore.Scheduling.Domain;

namespace DentaCore.Scheduling.Application;

public static class SchedulingPermissions
{
    public const string Read = "appointment:read";
    public const string Write = "appointment:write";
}

public sealed class SchedulingOptions
{
    public const string Section = "Scheduling";

    private TimeZoneInfo? _zone;

    /// <summary>İş qrafiki yerli vaxtla saxlanılır (provider_schedules.start_time). Klinikanın IANA saat qurşağı.</summary>
    public string TimeZone { get; set; } = "Asia/Baku";

    public int SlotStepMinutes { get; set; } = 15;

    /// <summary>Default xatırlatmalar: başlanğıcdan neçə dəqiqə əvvəl (24 saat və 2 saat).</summary>
    public int[] DefaultReminderMinutes { get; set; } = [1440, 120];

    public TimeZoneInfo Zone => _zone ??= TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}

public interface ISchedulingUnitOfWork : IModuleUnitOfWork
{
}

public interface ISchedulingRepository
{
    void Add(Appointment appointment);

    void AddTicket(QueueTicket ticket);

    void AddReminders(IEnumerable<AppointmentReminder> reminders);

    void AddWaitlist(WaitlistEntry entry);

    Task<Appointment?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<QueueTicket?> GetTicketAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Filial və gün üzrə növbəti bilet nömrəsi. Cari tranzaksiyada advisory kilid götürür:
    /// paralel check-in-lər nömrəni ardıcıl alır və təkrarlanmır. Çağıran tranzaksiya açmalıdır.
    /// </summary>
    Task<int> NextTicketNumberAsync(Guid branchId, DateOnly date, CancellationToken cancellationToken);

    Task CancelPendingRemindersAsync(Guid appointmentId, CancellationToken cancellationToken);
}

public sealed record AppointmentRow(
    Guid Id, Guid BranchId, Guid PatientId, Guid ProviderId, Guid? RoomId,
    DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, string Status, string? Reason, string Source, int RowVersion);

public sealed record AppointmentQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    Guid? ProviderId,
    Guid? RoomId,
    Guid? BranchId,
    IReadOnlyCollection<Guid>? AllowedBranchIds,
    Guid? OwnProviderId,
    string[]? Statuses);

public sealed record QueueRow(Guid Id, int TicketNo, Guid? PatientId, string Status, DateTimeOffset CreatedAt, Guid? AppointmentId);

public interface ISchedulingReadModel
{
    /// <summary>Provayderin həmin yerli günü: iş pəncərələri, məzuniyyət və (ləğv olunmamış) qəbullar.</summary>
    Task<ProviderDay> GetProviderDayAsync(Guid providerId, DateOnly localDate, TimeZoneInfo zone, Guid? excludeAppointmentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AppointmentRow>> ListAsync(AppointmentQuery query, CancellationToken cancellationToken);

    /// <summary>Otağın filialı (otaq başqa filiala aid ola bilməz). Otaq yoxdursa null.</summary>
    Task<Guid?> GetRoomBranchAsync(Guid roomId, CancellationToken cancellationToken);

    Task<IReadOnlyList<QueueRow>> ListQueueAsync(Guid branchId, DateOnly date, CancellationToken cancellationToken);
}
