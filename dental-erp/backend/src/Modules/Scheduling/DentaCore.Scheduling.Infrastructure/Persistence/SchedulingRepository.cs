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

/// <summary>Clinical modulunun çağırdığı kontrakt: qəbul statusunu vizitlə sinxronlaşdırır.</summary>
internal sealed class AppointmentLifecycle(SchedulingDbContext db, DentaCore.BuildingBlocks.Application.IClock clock) : Contracts.IAppointmentLifecycle
{
    public async Task<Contracts.LifecycleOutcome> StartAsync(Guid appointmentId, Guid providerId, Guid patientId, CancellationToken cancellationToken)
    {
        var appointment = await db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);
        if (appointment is null || appointment.PatientId != patientId || appointment.ProviderId != providerId)
        {
            // Başqa pasiyentin/həkimin qəbulunu vizitə bağlamaq olmaz. Mövcudluq sızmasın deyə eyni cavab
            return Contracts.LifecycleOutcome.Fail("visit.appointment_mismatch", "The appointment does not belong to this patient and provider.");
        }

        var result = appointment.Start(clock.UtcNow);
        if (result.IsFailure)
        {
            return Contracts.LifecycleOutcome.Fail(result.Error!.Code, result.Error.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new Contracts.LifecycleOutcome(true, BranchId: appointment.BranchId);
    }

    public async Task<Contracts.LifecycleOutcome> CompleteAsync(Guid appointmentId, CancellationToken cancellationToken)
    {
        var appointment = await db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);
        if (appointment is null)
        {
            return Contracts.LifecycleOutcome.Fail("appointment.not_found", "Appointment not found.");
        }

        var result = appointment.Complete();
        if (result.IsFailure)
        {
            return Contracts.LifecycleOutcome.Fail(result.Error!.Code, result.Error.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Contracts.LifecycleOutcome.Ok;
    }
}
