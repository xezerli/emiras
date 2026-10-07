using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Clinical.Domain;
using DentaCore.Scheduling.Contracts;
using FluentValidation;

namespace DentaCore.Clinical.Application;

public sealed record VisitDto(Guid Id, Guid PatientId, Guid? AppointmentId, Guid ProviderId, Guid BranchId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? ChiefComplaint, string Status, int RowVersion);

internal static class VisitMapper
{
    public static VisitDto ToDto(Visit v) =>
        new(v.Id, v.PatientId, v.AppointmentId, v.ProviderId, v.BranchId, v.StartedAt, v.EndedAt, v.ChiefComplaint, v.Status.ToString().ToLowerInvariant(), v.RowVersion);
}

public sealed record StartVisitCommand(Guid PatientId, Guid? AppointmentId, string? ChiefComplaint)
    : ICommand<VisitDto>, IRequiresAccess, IAuditable<VisitDto>
{
    public string Permission => ClinicalPermissions.Write;

    public AuditDescriptor Describe(VisitDto response) => new("visit.start", "visit", response?.Id);
}

public sealed class StartVisitCommandValidator : AbstractValidator<StartVisitCommand>
{
    public StartVisitCommandValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.ChiefComplaint).MaximumLength(1000);
    }
}

internal sealed class StartVisitCommandHandler(
    IClinicalRepository repository, ClinicalAccess access, IAppointmentLifecycle lifecycle, ICurrentUser user, IClinicalUnitOfWork unitOfWork, IClock clock)
    : IRequestHandler<StartVisitCommand, VisitDto>
{
    public async Task<Result<VisitDto>> Handle(StartVisitCommand request, CancellationToken cancellationToken)
    {
        var patient = await access.RequireAsync(request.PatientId, ClinicalPermissions.Write, cancellationToken);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        var open = await repository.FindOpenVisitAsync(request.PatientId, user.UserId, cancellationToken);
        if (open is not null)
        {
            return Error.Conflict("visit.already_open", "You already have an open visit for this patient.") with
            {
                Details = new Dictionary<string, object?> { ["existingVisitId"] = open.Id },
            };
        }

        var branchId = patient.Value.BranchId;
        if (request.AppointmentId is { } appointmentId)
        {
            // Qəbul bu pasiyentə və bu həkimə aid olmalıdır; status in_progress olur
            var started = await lifecycle.StartAsync(appointmentId, user.UserId, request.PatientId, cancellationToken);
            if (!started.Succeeded)
            {
                return started.ErrorCode == "appointment.invalid_state"
                    ? Error.Conflict(started.ErrorCode, started.Message ?? "Invalid appointment state.")
                    : Error.Validation(started.ErrorCode ?? "visit.appointment_mismatch", started.Message ?? "Invalid appointment.");
            }

            branchId = started.BranchId ?? branchId;
        }

        var visit = Visit.Start(Guid.NewGuid(), request.PatientId, user.UserId, branchId, request.AppointmentId, request.ChiefComplaint, clock.UtcNow);
        if (visit.IsFailure)
        {
            return visit.Error!;
        }

        repository.Add(visit.Value);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConstraintViolationException ex) when (ex.Kind == ConstraintKind.Unique)
        {
            // Paralel iki "vizitə başla": DB-dəki ux_visits_one_open yalnız birinə icazə verir
            return Error.Conflict("visit.already_open", "You already have an open visit for this patient.");
        }

        return VisitMapper.ToDto(visit.Value);
    }
}

public sealed record CloseVisitCommand(Guid VisitId) : ICommand<VisitDto>, IRequiresAccess, IAuditable<VisitDto>
{
    public string Permission => ClinicalPermissions.Write;

    public AuditDescriptor Describe(VisitDto response) => new("visit.close", "visit", VisitId);
}

internal sealed class CloseVisitCommandHandler(
    IClinicalRepository repository, IAppointmentLifecycle lifecycle, ICurrentUser user, IClinicalUnitOfWork unitOfWork, IClock clock)
    : IRequestHandler<CloseVisitCommand, VisitDto>
{
    public async Task<Result<VisitDto>> Handle(CloseVisitCommand request, CancellationToken cancellationToken)
    {
        var visit = await repository.GetVisitAsync(request.VisitId, cancellationToken);
        // Viziti yalnız onu açan həkim (və ya tenant scope-lu rəhbər) bağlaya bilər
        if (visit is null || (visit.ProviderId != user.UserId && user.ScopeOf(ClinicalPermissions.Write) != PermissionScope.Tenant))
        {
            return Error.NotFound("visit.not_found", "Visit not found.");
        }

        var closed = visit.Close(clock.UtcNow);
        if (closed.IsFailure)
        {
            return closed.Error!;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);   // VisitClosed hadisəsi outbox-a düşür (Billing saga)

        if (visit.AppointmentId is { } appointmentId)
        {
            // Qəbul statusu ikinci dərəcəlidir: vizit artıq bağlanıb, qəbul başqa vəziyyətdədirsə (məs. əl ilə dəyişilib) vizit uğurlu sayılır
            await lifecycle.CompleteAsync(appointmentId, cancellationToken);
        }

        return VisitMapper.ToDto(visit);
    }
}

public sealed record ListVisitsQuery(Guid PatientId) : IQuery<IReadOnlyList<VisitDto>>, IRequiresAccess, IAuditable<IReadOnlyList<VisitDto>>
{
    public string Permission => ClinicalPermissions.Read;

    public AuditDescriptor Describe(IReadOnlyList<VisitDto> response) =>
        new("visit.list", "patient", PatientId, ClinicalText.ToJson(new { results = response?.Count ?? 0 }));
}

internal sealed class ListVisitsQueryHandler(IClinicalRepository repository, ClinicalAccess access) : IRequestHandler<ListVisitsQuery, IReadOnlyList<VisitDto>>
{
    public async Task<Result<IReadOnlyList<VisitDto>>> Handle(ListVisitsQuery request, CancellationToken cancellationToken)
    {
        var patient = await access.RequireAsync(request.PatientId, ClinicalPermissions.Read, cancellationToken);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        IReadOnlyList<VisitDto> visits = (await repository.ListVisitsAsync(request.PatientId, cancellationToken)).Select(VisitMapper.ToDto).ToList();
        return Result.Success(visits);
    }
}
