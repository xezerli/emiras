using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Clinical.Domain;
using FluentValidation;

namespace DentaCore.Clinical.Application;

public sealed record PlanItemDto(
    Guid Id, string ProcedureCode, int? ToothFdi, string? Surface, int Phase, int Quantity, decimal UnitPrice, decimal DiscountPercent,
    decimal LineTotal, string Status, Guid? PerformedVisitId, DateTimeOffset? PerformedAt);

public sealed record PlanDto(
    Guid Id, Guid PatientId, Guid ProviderId, string Title, string Status, int Version, decimal Total, bool AiGenerated,
    DateTimeOffset? AcceptedAt, IReadOnlyList<PlanItemDto> Items, int RowVersion);

internal static class PlanMapper
{
    public static PlanDto ToDto(TreatmentPlan p) =>
        new(p.Id, p.PatientId, p.ProviderId, p.Title, ClinicalText.PlanStatus(p.Status), p.Version, p.Total, p.AiGenerated, p.AcceptedAt,
            p.Items.OrderBy(i => i.SortOrder).Select(i => new PlanItemDto(
                i.Id, i.ProcedureCode, i.ToothFdi, i.Surface?.ToString(), i.Phase, i.Quantity, i.UnitPrice, i.DiscountPercent, i.LineTotal,
                i.Status.ToString().ToLowerInvariant(), i.PerformedVisitId, i.PerformedAt)).ToList(),
            p.RowVersion);
}

public sealed record PlanItemInput(string ProcedureCode, int? ToothFdi, string? Surface, int? Phase, int? Quantity, decimal UnitPrice, decimal? DiscountPercent);

public sealed record CreatePlanCommand(Guid PatientId, string Title, IReadOnlyList<PlanItemInput> Items)
    : ICommand<PlanDto>, IRequiresAccess, IAuditable<PlanDto>
{
    public string Permission => ClinicalPermissions.Write;

    public AuditDescriptor Describe(PlanDto response) => new("plan.create", "treatment_plan", response?.Id, ClinicalText.ToJson(new { items = Items.Count }));
}

public sealed class CreatePlanCommandValidator : AbstractValidator<CreatePlanCommand>
{
    public CreatePlanCommandValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Items).NotNull().Must(i => i is { Count: >= 1 and <= TreatmentPlan.MaxItems }).WithMessage("A plan needs 1-50 items.");
        RuleForEach(x => x.Items).ChildRules(i =>
        {
            i.RuleFor(x => x.ProcedureCode).NotEmpty().MaximumLength(20);
            i.RuleFor(x => x.Surface).Must(s => s is null || s.Length == 1).WithMessage("Surface must be a single letter.");
        });
    }
}

internal sealed class CreatePlanCommandHandler(IClinicalRepository repository, ClinicalAccess access, ICurrentUser user, IClock clock)
    : IRequestHandler<CreatePlanCommand, PlanDto>
{
    public async Task<Result<PlanDto>> Handle(CreatePlanCommand request, CancellationToken cancellationToken)
    {
        var patient = await access.RequireAsync(request.PatientId, ClinicalPermissions.Write, cancellationToken);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        var catalog = await repository.GetProceduresAsync(request.Items.Select(i => i.ProcedureCode).Distinct().ToArray(), cancellationToken);
        var items = new List<NewPlanItem>();
        foreach (var input in request.Items)
        {
            if (!catalog.TryGetValue(input.ProcedureCode, out var procedure))
            {
                return Error.Validation("plan.unknown_procedure", $"Procedure code '{input.ProcedureCode}' does not exist in the catalog.");
            }

            if (procedure.RequiresTooth && input.ToothFdi is null)
            {
                return Error.Validation("plan.tooth_required", $"Procedure '{procedure.Code}' requires a tooth number.");
            }

            items.Add(new NewPlanItem(input.ProcedureCode, input.ToothFdi, input.Surface?[0], input.Phase ?? 1, input.Quantity ?? 1, input.UnitPrice, input.DiscountPercent ?? 0));
        }

        var plan = TreatmentPlan.Create(Guid.NewGuid(), request.PatientId, user.UserId, request.Title, items, clock.UtcNow);
        if (plan.IsFailure)
        {
            return plan.Error!;
        }

        repository.Add(plan.Value);
        return PlanMapper.ToDto(plan.Value);
    }
}

public sealed record GetPlanQuery(Guid PlanId) : IQuery<PlanDto>, IRequiresAccess, IAuditable<PlanDto>
{
    public string Permission => ClinicalPermissions.Read;

    public AuditDescriptor Describe(PlanDto response) => new("plan.read", "treatment_plan", PlanId);
}

internal sealed class GetPlanQueryHandler(IClinicalRepository repository, ClinicalAccess access) : IRequestHandler<GetPlanQuery, PlanDto>
{
    public async Task<Result<PlanDto>> Handle(GetPlanQuery request, CancellationToken cancellationToken)
    {
        var plan = await repository.GetPlanAsync(request.PlanId, cancellationToken);
        if (plan is null || (await access.RequireAsync(plan.PatientId, ClinicalPermissions.Read, cancellationToken)).IsFailure)
        {
            return Error.NotFound("plan.not_found", "Treatment plan not found.");
        }

        return PlanMapper.ToDto(plan);
    }
}

public sealed record ListPlansQuery(Guid PatientId) : IQuery<IReadOnlyList<PlanDto>>, IRequiresAccess, IAuditable<IReadOnlyList<PlanDto>>
{
    public string Permission => ClinicalPermissions.Read;

    public AuditDescriptor Describe(IReadOnlyList<PlanDto> response) => new("plan.list", "patient", PatientId, ClinicalText.ToJson(new { results = response?.Count ?? 0 }));
}

internal sealed class ListPlansQueryHandler(IClinicalRepository repository, ClinicalAccess access) : IRequestHandler<ListPlansQuery, IReadOnlyList<PlanDto>>
{
    public async Task<Result<IReadOnlyList<PlanDto>>> Handle(ListPlansQuery request, CancellationToken cancellationToken)
    {
        var patient = await access.RequireAsync(request.PatientId, ClinicalPermissions.Read, cancellationToken);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        IReadOnlyList<PlanDto> plans = (await repository.ListPlansAsync(request.PatientId, cancellationToken)).Select(PlanMapper.ToDto).ToList();
        return Result.Success(plans);
    }
}

public enum PlanAction
{
    Propose,
    Accept,
    Reject,
    Cancel,
}

public sealed record TransitionPlanCommand(Guid PlanId, PlanAction Action) : ICommand<PlanDto>, IRequiresAccess, IAuditable<PlanDto>
{
    public string Permission => ClinicalPermissions.Write;

    public AuditDescriptor Describe(PlanDto response) =>
        new($"plan.{Action.ToString().ToLowerInvariant()}", "treatment_plan", PlanId, ClinicalText.ToJson(new { total = response?.Total }));
}

internal sealed class TransitionPlanCommandHandler(IClinicalRepository repository, ClinicalAccess access, IClock clock)
    : IRequestHandler<TransitionPlanCommand, PlanDto>
{
    public async Task<Result<PlanDto>> Handle(TransitionPlanCommand request, CancellationToken cancellationToken)
    {
        var plan = await repository.GetPlanAsync(request.PlanId, cancellationToken);
        if (plan is null || (await access.RequireAsync(plan.PatientId, ClinicalPermissions.Write, cancellationToken)).IsFailure)
        {
            return Error.NotFound("plan.not_found", "Treatment plan not found.");
        }

        var result = request.Action switch
        {
            PlanAction.Propose => plan.Propose(),
            PlanAction.Accept => plan.Accept(clock.UtcNow),
            PlanAction.Reject => plan.Reject(),
            _ => plan.Cancel(),
        };
        return result.IsFailure ? result.Error! : PlanMapper.ToDto(plan);
    }
}

public sealed record PerformPlanItemCommand(Guid PlanId, Guid ItemId, Guid VisitId) : ICommand<PlanDto>, IRequiresAccess, IAuditable<PlanDto>
{
    public string Permission => ClinicalPermissions.Write;

    public AuditDescriptor Describe(PlanDto response) => new("plan.perform_item", "treatment_plan", PlanId);
}

internal sealed class PerformPlanItemCommandHandler(IClinicalRepository repository, ClinicalAccess access, ICurrentUser user, IClock clock)
    : IRequestHandler<PerformPlanItemCommand, PlanDto>
{
    public async Task<Result<PlanDto>> Handle(PerformPlanItemCommand request, CancellationToken cancellationToken)
    {
        var plan = await repository.GetPlanAsync(request.PlanId, cancellationToken);
        if (plan is null || (await access.RequireAsync(plan.PatientId, ClinicalPermissions.Write, cancellationToken)).IsFailure)
        {
            return Error.NotFound("plan.not_found", "Treatment plan not found.");
        }

        var visit = await VisitGuard.RequireOpenAsync(repository, request.VisitId, plan.PatientId, cancellationToken);
        if (visit.IsFailure)
        {
            return visit.Error!;
        }

        // Prosedur həkimin öz açıq vizitində icra olunur (başqa həkimin vizitinə prosedur yazmaq olmaz)
        if (visit.Value.ProviderId != user.UserId)
        {
            return Error.Forbidden("plan.not_your_visit", "A procedure can only be performed within your own visit.");
        }

        var performed = plan.PerformItem(request.ItemId, request.VisitId, user.UserId, clock.UtcNow);
        return performed.IsFailure ? performed.Error! : PlanMapper.ToDto(plan);
    }
}

public sealed record ListProcedureCodesQuery(string? Search) : IQuery<IReadOnlyList<ProcedureCodeDto>>, IRequiresAccess
{
    public string Permission => ClinicalPermissions.Read;
}

internal sealed class ListProcedureCodesQueryHandler(IClinicalRepository repository) : IRequestHandler<ListProcedureCodesQuery, IReadOnlyList<ProcedureCodeDto>>
{
    public async Task<Result<IReadOnlyList<ProcedureCodeDto>>> Handle(ListProcedureCodesQuery request, CancellationToken cancellationToken) =>
        Result.Success(await repository.ListProceduresAsync(request.Search, cancellationToken));
}
