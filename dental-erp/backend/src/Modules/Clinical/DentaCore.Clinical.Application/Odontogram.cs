using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Clinical.Domain;
using FluentValidation;

namespace DentaCore.Clinical.Application;

public sealed record ToothRecordDto(Guid Id, int ToothFdi, string? Surface, string Condition, string? Material, string? Notes, Guid? VisitId, Guid? RecordedBy, DateTimeOffset RecordedAt, DateTimeOffset? SupersededAt);

public sealed record OdontogramDto(Guid PatientId, string Dentition, IReadOnlyDictionary<int, IReadOnlyList<ToothRecordDto>> Teeth);

internal static class OdontogramMapper
{
    public static ToothRecordDto ToDto(ToothRecord r) =>
        new(r.Id, r.ToothFdi, r.Surface?.ToString(), ClinicalText.Condition(r.Condition), r.Material, r.Notes, r.VisitId, r.RecordedBy, r.RecordedAt, r.SupersededAt);
}

public sealed record RecordToothCommand(Guid PatientId, int ToothFdi, string? Surface, string Condition, string? Material, string? Notes, Guid? VisitId)
    : ICommand<ToothRecordDto>, IRequiresAccess, IAuditable<ToothRecordDto>
{
    public string Permission => ClinicalPermissions.Write;

    public AuditDescriptor Describe(ToothRecordDto response) => new("odontogram.record", "patient", PatientId, ClinicalText.ToJson(new { tooth = ToothFdi }));
}

public sealed class RecordToothCommandValidator : AbstractValidator<RecordToothCommand>
{
    public RecordToothCommandValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.Surface).Must(s => s is null || s.Length == 1).WithMessage("Surface must be a single letter.");
        RuleFor(x => x.Condition).Must(c => ClinicalText.TryParseCondition(c, out _)).WithMessage("Unknown tooth condition.");
    }
}

internal sealed class RecordToothCommandHandler(
    IClinicalRepository repository, ClinicalAccess access, ICurrentUser user, IClinicalUnitOfWork unitOfWork, IClock clock)
    : IRequestHandler<RecordToothCommand, ToothRecordDto>
{
    public async Task<Result<ToothRecordDto>> Handle(RecordToothCommand request, CancellationToken cancellationToken)
    {
        var patient = await access.RequireAsync(request.PatientId, ClinicalPermissions.Write, cancellationToken);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        if (request.VisitId is { } visitId)
        {
            var visit = await VisitGuard.RequireOpenAsync(repository, visitId, request.PatientId, cancellationToken);
            if (visit.IsFailure)
            {
                return visit.Error!;
            }
        }

        _ = ClinicalText.TryParseCondition(request.Condition, out var condition);
        var now = clock.UtcNow;
        var record = ToothRecord.Create(Guid.NewGuid(), request.PatientId, request.VisitId, request.ToothFdi, request.Surface?[0], condition, request.Material, request.Notes, user.UserId, now);
        if (record.IsFailure)
        {
            return record.Error!;
        }

        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            await repository.SupersedeAsync(request.PatientId, request.ToothFdi, record.Value.Surface, now, cancellationToken);
            repository.Add(record.Value);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (ConstraintViolationException ex) when (ex.Kind == ConstraintKind.Unique)
        {
            // Kilid səbəbindən nadir hal; DB-dəki unikal indeks son müdafiədir
            return Error.Conflict("odontogram.concurrent_update", "The tooth was updated by someone else at the same time. Reload and retry.");
        }

        return OdontogramMapper.ToDto(record.Value);
    }
}

public sealed record GetOdontogramQuery(Guid PatientId, DateTimeOffset? At) : IQuery<OdontogramDto>, IRequiresAccess, IAuditable<OdontogramDto>
{
    public string Permission => ClinicalPermissions.Read;

    public AuditDescriptor Describe(OdontogramDto response) => new("odontogram.read", "patient", PatientId);
}

internal sealed class GetOdontogramQueryHandler(IClinicalRepository repository, ClinicalAccess access) : IRequestHandler<GetOdontogramQuery, OdontogramDto>
{
    public async Task<Result<OdontogramDto>> Handle(GetOdontogramQuery request, CancellationToken cancellationToken)
    {
        var patient = await access.RequireAsync(request.PatientId, ClinicalPermissions.Read, cancellationToken);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        var records = await repository.GetOdontogramAsync(request.PatientId, request.At?.ToUniversalTime(), cancellationToken);
        var teeth = records
            .GroupBy(r => r.ToothFdi)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ToothRecordDto>)g.Select(OdontogramMapper.ToDto).ToList());
        var anyPrimary = teeth.Keys.Any(Fdi.IsPrimary);
        var anyPermanent = teeth.Keys.Any(t => !Fdi.IsPrimary(t));
        var dentition = anyPrimary && anyPermanent ? "mixed" : anyPrimary ? "primary" : "permanent";
        return new OdontogramDto(request.PatientId, dentition, teeth);
    }
}

public sealed record GetToothHistoryQuery(Guid PatientId, int ToothFdi) : IQuery<IReadOnlyList<ToothRecordDto>>, IRequiresAccess, IAuditable<IReadOnlyList<ToothRecordDto>>
{
    public string Permission => ClinicalPermissions.Read;

    public AuditDescriptor Describe(IReadOnlyList<ToothRecordDto> response) => new("odontogram.history", "patient", PatientId, ClinicalText.ToJson(new { tooth = ToothFdi }));
}

internal sealed class GetToothHistoryQueryHandler(IClinicalRepository repository, ClinicalAccess access) : IRequestHandler<GetToothHistoryQuery, IReadOnlyList<ToothRecordDto>>
{
    public async Task<Result<IReadOnlyList<ToothRecordDto>>> Handle(GetToothHistoryQuery request, CancellationToken cancellationToken)
    {
        if (!Fdi.IsValid(request.ToothFdi))
        {
            return Error.Validation("odontogram.invalid_tooth", "Tooth number must be a valid FDI number.");
        }

        var patient = await access.RequireAsync(request.PatientId, ClinicalPermissions.Read, cancellationToken);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        IReadOnlyList<ToothRecordDto> history = (await repository.GetToothHistoryAsync(request.PatientId, request.ToothFdi, cancellationToken)).Select(OdontogramMapper.ToDto).ToList();
        return Result.Success(history);
    }
}
