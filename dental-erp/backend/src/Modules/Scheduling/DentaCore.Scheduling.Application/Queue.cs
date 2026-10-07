using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Patient.Contracts;
using DentaCore.Scheduling.Domain;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace DentaCore.Scheduling.Application;

public sealed record QueueTicketDto(Guid Id, int TicketNo, Guid? AppointmentId, Guid? PatientId, string? PatientName, string Status, int WaitingMinutes);

internal static class QueueMapper
{
    public static string StatusText(QueueStatus s) => s.ToString().ToLowerInvariant();

    public static QueueTicketDto ToDto(QueueTicket t, string? name, DateTimeOffset now) =>
        new(t.Id, t.TicketNo, t.AppointmentId, t.PatientId, name, StatusText(t.Status), (int)Math.Max(0, (now - t.CreatedAt).TotalMinutes));
}

// ---------------- Check-in ----------------
public sealed record CheckInCommand(Guid AppointmentId) : ICommand<QueueTicketDto>, IRequiresAccess, IAuditable<QueueTicketDto>
{
    public string Permission => SchedulingPermissions.Write;

    public AuditDescriptor Describe(QueueTicketDto response) => new("appointment.check_in", "appointment", AppointmentId);
}

internal sealed class CheckInCommandHandler(
    ISchedulingRepository repository,
    IPatientDirectory patients,
    ICurrentUser user,
    ISchedulingUnitOfWork unitOfWork,
    IOptions<SchedulingOptions> options,
    IClock clock) : IRequestHandler<CheckInCommand, QueueTicketDto>
{
    public async Task<Result<QueueTicketDto>> Handle(CheckInCommand request, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var appointment = await repository.GetAsync(request.AppointmentId, cancellationToken);
        if (appointment is null || !user.CanAccess(SchedulingPermissions.Write, appointment.BranchId, appointment.ProviderId))
        {
            return Error.NotFound("appointment.not_found", "Appointment not found.");
        }

        var checkedIn = appointment.CheckIn(now);
        if (checkedIn.IsFailure)
        {
            return checkedIn.Error!;
        }

        var queueDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, options.Value.Zone).DateTime);
        QueueTicket ticket;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            // Kilid tranzaksiya bitənə qədər saxlanılır: paralel check-in-lər eyni nömrəni ala bilmir
            var number = await repository.NextTicketNumberAsync(appointment.BranchId, queueDate, cancellationToken);
            ticket = QueueTicket.Issue(Guid.NewGuid(), appointment.BranchId, appointment.Id, appointment.PatientId, number, queueDate, now);
            repository.AddTicket(ticket);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var names = await patients.GetNamesAsync([appointment.PatientId], cancellationToken);
        return QueueMapper.ToDto(ticket, names.GetValueOrDefault(appointment.PatientId), now);
    }
}

// ---------------- Queue list / call ----------------
public sealed record GetQueueQuery(Guid BranchId) : IQuery<IReadOnlyList<QueueTicketDto>>, IRequiresAccess, IAuditable<IReadOnlyList<QueueTicketDto>>
{
    public string Permission => SchedulingPermissions.Read;

    public AuditDescriptor Describe(IReadOnlyList<QueueTicketDto> response) =>
        new("queue.read", "branch", BranchId, AppointmentMapper.ToJson(new { results = response?.Count ?? 0 }));
}

internal sealed class GetQueueQueryHandler(
    ISchedulingReadModel readModel, IPatientDirectory patients, ICurrentUser user, IOptions<SchedulingOptions> options, IClock clock)
    : IRequestHandler<GetQueueQuery, IReadOnlyList<QueueTicketDto>>
{
    public async Task<Result<IReadOnlyList<QueueTicketDto>>> Handle(GetQueueQuery request, CancellationToken cancellationToken)
    {
        // Növbə filialın lobbisidir: own scope kifayət deyil, filial/tenant scope lazımdır
        if (user.ScopeOf(SchedulingPermissions.Read) is null or PermissionScope.Own || !user.CanAccess(SchedulingPermissions.Read, request.BranchId))
        {
            return Error.NotFound("branch.not_found", "Branch not found.");
        }

        var now = clock.UtcNow;
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, options.Value.Zone).DateTime);
        var rows = await readModel.ListQueueAsync(request.BranchId, date, cancellationToken);
        var names = await patients.GetNamesAsync(rows.Where(r => r.PatientId is not null).Select(r => r.PatientId!.Value).Distinct().ToArray(), cancellationToken);
        IReadOnlyList<QueueTicketDto> items = rows
            .Select(r => new QueueTicketDto(r.Id, r.TicketNo, r.AppointmentId, r.PatientId, r.PatientId is { } p ? names.GetValueOrDefault(p) : null, r.Status, (int)Math.Max(0, (now - r.CreatedAt).TotalMinutes)))
            .ToList();
        return Result.Success(items);
    }
}

public sealed record CallTicketCommand(Guid TicketId) : ICommand<QueueTicketDto>, IRequiresAccess, IAuditable<QueueTicketDto>
{
    public string Permission => SchedulingPermissions.Write;

    public AuditDescriptor Describe(QueueTicketDto response) => new("queue.call", "queue_ticket", TicketId);
}

internal sealed class CallTicketCommandHandler(ISchedulingRepository repository, IPatientDirectory patients, ICurrentUser user, IClock clock)
    : IRequestHandler<CallTicketCommand, QueueTicketDto>
{
    public async Task<Result<QueueTicketDto>> Handle(CallTicketCommand request, CancellationToken cancellationToken)
    {
        var ticket = await repository.GetTicketAsync(request.TicketId, cancellationToken);
        if (ticket is null || !user.CanAccess(SchedulingPermissions.Write, ticket.BranchId))
        {
            return Error.NotFound("queue.not_found", "Ticket not found.");
        }

        var now = clock.UtcNow;
        var called = ticket.Call(now);
        if (called.IsFailure)
        {
            return called.Error!;
        }

        var names = ticket.PatientId is { } p ? await patients.GetNamesAsync([p], cancellationToken) : new Dictionary<Guid, string>();
        return QueueMapper.ToDto(ticket, ticket.PatientId is { } id ? names.GetValueOrDefault(id) : null, now);
    }
}

// ---------------- Waitlist ----------------
public sealed record WaitlistDto(Guid Id);

public sealed record AddToWaitlistCommand(Guid PatientId, Guid BranchId, Guid? ProviderId, DateTimeOffset? Earliest, DateTimeOffset? Latest, int Priority = 5)
    : ICommand<WaitlistDto>, IRequiresAccess, IAuditable<WaitlistDto>
{
    public string Permission => SchedulingPermissions.Write;

    public AuditDescriptor Describe(WaitlistDto response) => new("waitlist.add", "patient", PatientId);
}

public sealed class AddToWaitlistCommandValidator : AbstractValidator<AddToWaitlistCommand>
{
    public AddToWaitlistCommandValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.BranchId).NotEmpty();
    }
}

internal sealed class AddToWaitlistCommandHandler(ISchedulingRepository repository, IPatientDirectory patients, ICurrentUser user)
    : IRequestHandler<AddToWaitlistCommand, WaitlistDto>
{
    public async Task<Result<WaitlistDto>> Handle(AddToWaitlistCommand request, CancellationToken cancellationToken)
    {
        if (!user.CanAccess(SchedulingPermissions.Write, request.BranchId, request.ProviderId))
        {
            return Error.Forbidden("permission.denied", "You cannot manage the waitlist of this branch.");
        }

        if (await patients.FindAsync(request.PatientId, cancellationToken) is null)
        {
            return Error.NotFound("patient.not_found", "Patient not found.");
        }

        var entry = WaitlistEntry.Create(request.PatientId, request.BranchId, request.ProviderId, request.Earliest?.ToUniversalTime(), request.Latest?.ToUniversalTime(), request.Priority);
        if (entry.IsFailure)
        {
            return entry.Error!;
        }

        repository.AddWaitlist(entry.Value);
        return new WaitlistDto(entry.Value.Id);
    }
}
