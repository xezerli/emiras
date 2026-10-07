using DentaCore.Scheduling.Application;
using DentaCore.Scheduling.Domain;
using Microsoft.EntityFrameworkCore;

namespace DentaCore.Scheduling.Infrastructure.Persistence;

internal sealed class SchedulingRepository(SchedulingDbContext db) : ISchedulingRepository
{
    public void Add(Appointment appointment) => db.Appointments.Add(appointment);

    public void AddTicket(QueueTicket ticket) => db.QueueTickets.Add(ticket);

    public void AddReminders(IEnumerable<AppointmentReminder> reminders) => db.Reminders.AddRange(reminders);

    public void AddWaitlist(WaitlistEntry entry) => db.Waitlist.Add(entry);

    public Task<Appointment?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Appointments.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<QueueTicket?> GetTicketAsync(Guid id, CancellationToken cancellationToken) =>
        db.QueueTickets.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<int> NextTicketNumberAsync(Guid branchId, DateOnly date, CancellationToken cancellationToken)
    {
        var lockKey = $"queue:{branchId}:{date:yyyy-MM-dd}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended(current_schema() || {lockKey}, 0))", cancellationToken);
        var next = await db.Database
            .SqlQuery<int>($"SELECT COALESCE(MAX(ticket_no), 0) + 1 AS \"Value\" FROM queue_tickets WHERE branch_id = {branchId} AND queue_date = {date}")
            .ToListAsync(cancellationToken);
        return next[0];
    }

    public async Task CancelPendingRemindersAsync(Guid appointmentId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync(
            $"UPDATE appointment_reminders SET status = 'cancelled' WHERE appointment_id = {appointmentId} AND status = 'pending'",
            cancellationToken);
}
