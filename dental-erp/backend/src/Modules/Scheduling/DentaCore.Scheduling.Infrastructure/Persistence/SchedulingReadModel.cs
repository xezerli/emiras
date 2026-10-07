using System.Data.Common;
using System.Text;
using DentaCore.Scheduling.Application;
using DentaCore.Scheduling.Contracts;
using DentaCore.Scheduling.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace DentaCore.Scheduling.Infrastructure.Persistence;

/// <summary>Oxu modeli və "baxan həkim" əlaqəsi. Raw SQL sütun adları EF-nin snake_case convention-ına uyğundur (PeriodStart → period_start).</summary>
internal sealed class SchedulingReadModel(SchedulingDbContext db) : ISchedulingReadModel, ICareRelationships
{
    private static readonly string[] ActiveStatuses = ["booked", "confirmed", "checked_in", "in_progress"];

    public async Task<ProviderDay> GetProviderDayAsync(Guid providerId, DateOnly localDate, TimeZoneInfo zone, Guid? excludeAppointmentId, CancellationToken cancellationToken)
    {
        var isoWeekday = localDate.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)localDate.DayOfWeek;
        var dayStart = SchedulePlanner.ToUtc(localDate, TimeOnly.MinValue, zone);
        var dayEnd = SchedulePlanner.ToUtc(localDate.AddDays(1), TimeOnly.MinValue, zone);

        var any = await db.Database
            .SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM provider_schedules WHERE provider_id = {providerId} AND valid_from <= {localDate} AND (valid_to IS NULL OR valid_to >= {localDate})) AS \"Value\"")
            .ToListAsync(cancellationToken);
        var windows = await db.Database
            .SqlQuery<WindowRow>($"SELECT start_time, end_time FROM provider_schedules WHERE provider_id = {providerId} AND weekday = {isoWeekday} AND valid_from <= {localDate} AND (valid_to IS NULL OR valid_to >= {localDate}) ORDER BY start_time")
            .ToListAsync(cancellationToken);
        var timeOff = await db.Database
            .SqlQuery<SlotRow>($"SELECT lower(period) AS period_start, upper(period) AS period_end FROM time_off WHERE provider_id = {providerId} AND period && tstzrange({dayStart}, {dayEnd})")
            .ToListAsync(cancellationToken);
        var busy = await db.Database
            .SqlQuery<SlotRow>($"SELECT lower(period) AS period_start, upper(period) AS period_end FROM appointments WHERE provider_id = {providerId} AND status = ANY({ActiveStatuses}) AND period && tstzrange({dayStart}, {dayEnd}) AND ({excludeAppointmentId}::uuid IS NULL OR id <> {excludeAppointmentId})")
            .ToListAsync(cancellationToken);

        return new ProviderDay(
            any[0],
            windows.Select(w => new WorkWindow(w.StartTime, w.EndTime)).ToList(),
            timeOff.Select(s => new TimeSlot(s.PeriodStart, s.PeriodEnd)).ToList(),
            busy.Select(s => new TimeSlot(s.PeriodStart, s.PeriodEnd)).ToList());
    }

    public async Task<IReadOnlyList<AppointmentRow>> ListAsync(AppointmentQuery q, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(q);
        var sql = new StringBuilder("""
            SELECT a.id, a.branch_id, a.patient_id, a.provider_id, a.room_id, lower(a.period) AS period_start, upper(a.period) AS period_end,
                   a.status, a.reason, a.source, a.row_version
            FROM appointments a
            WHERE a.period && tstzrange(@from, @to)
            """);
        var args = new List<DbParameter>
        {
            new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = q.From.UtcDateTime },
            new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = q.To.UtcDateTime },
        };

        void Add(string condition, string name, NpgsqlDbType type, object value)
        {
            sql.Append(" AND ").Append(condition);
            args.Add(new NpgsqlParameter(name, type) { Value = value });
        }

        if (q.ProviderId is { } provider)
        {
            Add("a.provider_id = @provider", "provider", NpgsqlDbType.Uuid, provider);
        }

        if (q.RoomId is { } room)
        {
            Add("a.room_id = @room", "room", NpgsqlDbType.Uuid, room);
        }

        if (q.BranchId is { } branch)
        {
            Add("a.branch_id = @branch", "branch", NpgsqlDbType.Uuid, branch);
        }

        if (q.AllowedBranchIds is { Count: > 0 } allowed)
        {
            Add("a.branch_id = ANY(@allowed)", "allowed", NpgsqlDbType.Array | NpgsqlDbType.Uuid, allowed.ToArray());
        }

        if (q.OwnProviderId is { } own)
        {
            Add("a.provider_id = @own", "own", NpgsqlDbType.Uuid, own);
        }

        if (q.Statuses is { Length: > 0 } statuses)
        {
            Add("a.status = ANY(@statuses)", "statuses", NpgsqlDbType.Array | NpgsqlDbType.Text, statuses);
        }

        sql.Append(" ORDER BY lower(a.period), a.id LIMIT 2000");
        return await db.Database.SqlQueryRaw<AppointmentRow>(sql.ToString(), args.ToArray()).ToListAsync(cancellationToken);
    }

    public async Task<Guid?> GetRoomBranchAsync(Guid roomId, CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQuery<Guid>($"SELECT branch_id AS \"Value\" FROM rooms WHERE id = {roomId} AND is_active")
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<IReadOnlyList<QueueRow>> ListQueueAsync(Guid branchId, DateOnly date, CancellationToken cancellationToken) =>
        await db.Database
            .SqlQuery<QueueRow>($"SELECT id, ticket_no, patient_id, status, created_at, appointment_id FROM queue_tickets WHERE branch_id = {branchId} AND queue_date = {date} ORDER BY ticket_no")
            .ToListAsync(cancellationToken);

    // ---- ICareRelationships (Patient/Clinical own scope üçün) ----
    public async Task<bool> HasAsync(Guid providerId, Guid patientId, CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM appointments WHERE provider_id = {providerId} AND patient_id = {patientId} AND status <> 'cancelled') AS \"Value\"")
            .ToListAsync(cancellationToken);
        return rows[0];
    }

    public async Task<IReadOnlyCollection<Guid>> PatientIdsAsync(Guid providerId, CancellationToken cancellationToken) =>
        await db.Database
            .SqlQuery<Guid>($"SELECT DISTINCT patient_id AS \"Value\" FROM appointments WHERE provider_id = {providerId} AND status <> 'cancelled'")
            .ToListAsync(cancellationToken);

    private sealed record WindowRow(TimeOnly StartTime, TimeOnly EndTime);

    private sealed record SlotRow(DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd);
}
