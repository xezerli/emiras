using DentaCore.BuildingBlocks.Domain;

namespace DentaCore.Clinical.Domain;

public enum PlanStatus
{
    Draft,
    Proposed,
    Accepted,
    InProgress,
    Completed,
    Rejected,
    Cancelled,
}

public enum ItemStatus
{
    Planned,
    Scheduled,
    Done,
    Cancelled,
}

public sealed record TreatmentPlanAccepted(Guid EventId, DateTimeOffset OccurredAt, Guid PlanId, Guid PatientId, Guid ProviderId, decimal Total)
    : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "clinical.treatment-plan-accepted";
}

/// <summary>
/// Prosedur icra olundu. Billing invoice sətri yaradır, Inventory material norması üzrə stoku silir (Faza 2).
/// Qiymət və endirim plan bəndindən ŞAMİLDİR: sonradan plan dəyişsə də faktura icra anındakı qiyməti saxlayır.
/// </summary>
public sealed record ProcedurePerformed(
    Guid EventId, DateTimeOffset OccurredAt, Guid PlanId, Guid PlanItemId, Guid VisitId, Guid PatientId, Guid ProviderId,
    string ProcedureCode, int? ToothFdi, int Quantity, decimal UnitPrice, decimal DiscountPercent)
    : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "clinical.procedure-performed";
}

public sealed record TreatmentPlanCompleted(Guid EventId, DateTimeOffset OccurredAt, Guid PlanId, Guid PatientId) : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "clinical.treatment-plan-completed";
}

public sealed record NewPlanItem(string ProcedureCode, int? ToothFdi, char? Surface, int Phase, int Quantity, decimal UnitPrice, decimal DiscountPercent);

public sealed class PlanItem : Entity<Guid>
{
    private PlanItem()
        : base(Guid.Empty)
    {
    }

    public Guid PlanId { get; private set; }

    public string ProcedureCode { get; private set; } = string.Empty;

    public int? ToothFdi { get; private set; }

    public char? Surface { get; private set; }

    public int Phase { get; private set; }

    public int Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal DiscountPercent { get; private set; }

    public ItemStatus Status { get; private set; }

    public Guid? PerformedVisitId { get; private set; }

    public DateTimeOffset? PerformedAt { get; private set; }

    public Guid? PerformedBy { get; private set; }

    public int SortOrder { get; private set; }

    /// <summary>OCC token (T008): eyni bəndi iki nəfər eyni anda icra edə bilməz.</summary>
    public int RowVersion { get; private set; }

    /// <summary>Sətir cəmi: say × qiymət × (1 − endirim%). Bank yuvarlaqlaşdırması.</summary>
    public decimal LineTotal => Math.Round(Quantity * UnitPrice * (1 - (DiscountPercent / 100m)), 2, MidpointRounding.ToEven);

    internal static PlanItem Create(Guid planId, NewPlanItem n, int order) => new()
    {
        Id = Guid.NewGuid(),
        PlanId = planId,
        ProcedureCode = n.ProcedureCode,
        ToothFdi = n.ToothFdi,
        Surface = n.Surface,
        Phase = n.Phase,
        Quantity = n.Quantity,
        UnitPrice = n.UnitPrice,
        DiscountPercent = n.DiscountPercent,
        Status = ItemStatus.Planned,
        SortOrder = order,
    };

    internal void MarkDone(Guid visitId, Guid performedBy, DateTimeOffset now)
    {
        Status = ItemStatus.Done;
        PerformedVisitId = visitId;
        PerformedBy = performedBy;
        PerformedAt = now;
    }

    internal void MarkCancelled() => Status = ItemStatus.Cancelled;
}

public sealed class TreatmentPlan : AggregateRoot<Guid>
{
    public const int MaxItems = 50;

    private readonly List<PlanItem> _items = [];

    private TreatmentPlan()
        : base(Guid.Empty)
    {
    }

    public Guid PatientId { get; private set; }

    public Guid ProviderId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public PlanStatus Status { get; private set; }

    /// <summary>Hələlik həmişə 1: plan yalnız qaralamada redaktə olunur. Təklifdən sonra dəyişiklik yeni plan kimi yaradılmalıdır.</summary>
    public int Version { get; private set; } = 1;

    public DateTimeOffset? AcceptedAt { get; private set; }

    public bool AiGenerated { get; private set; }

    public string? AiModel { get; private set; }

    public IReadOnlyList<PlanItem> Items => _items;

    /// <summary>Ləğv olunmamış bəndlərin cəmi.</summary>
    public decimal Total => _items.Where(i => i.Status != ItemStatus.Cancelled).Sum(i => i.LineTotal);

    public static Result<TreatmentPlan> Create(Guid id, Guid patientId, Guid providerId, string title, IReadOnlyCollection<NewPlanItem> items, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200)
        {
            return Error.Validation("plan.invalid_title", "Title is required (max 200 characters).");
        }

        if (items.Count is 0 or > MaxItems)
        {
            return Error.Validation("plan.invalid_items", $"A plan needs between 1 and {MaxItems} items.");
        }

        foreach (var item in items)
        {
            var invalid = ValidateItem(item);
            if (invalid is not null)
            {
                return invalid;
            }
        }

        var plan = new TreatmentPlan { Id = id, PatientId = patientId, ProviderId = providerId, Title = title.Trim(), Status = PlanStatus.Draft };
        var order = 0;
        foreach (var item in items)
        {
            plan._items.Add(PlanItem.Create(id, item, order++));
        }

        return plan;
    }

    public Result Propose()
    {
        if (Status != PlanStatus.Draft)
        {
            return Error.Conflict("plan.invalid_state", $"Only a draft plan can be proposed (current: {Status}).");
        }

        Status = PlanStatus.Proposed;
        return Result.Success();
    }

    public Result Accept(DateTimeOffset now)
    {
        if (Status != PlanStatus.Proposed)
        {
            return Error.Conflict("plan.invalid_state", $"Only a proposed plan can be accepted (current: {Status}).");
        }

        Status = PlanStatus.Accepted;
        AcceptedAt = now;
        Raise(new TreatmentPlanAccepted(Guid.NewGuid(), now, Id, PatientId, ProviderId, Total));
        return Result.Success();
    }

    public Result Reject()
    {
        if (Status != PlanStatus.Proposed)
        {
            return Error.Conflict("plan.invalid_state", $"Only a proposed plan can be rejected (current: {Status}).");
        }

        Status = PlanStatus.Rejected;
        return Result.Success();
    }

    /// <summary>Ləğv: icra olunmuş bəndlər qalır (faktura onlara əsaslanır), qalanları ləğv olunur.</summary>
    public Result Cancel()
    {
        if (Status is PlanStatus.Completed or PlanStatus.Rejected or PlanStatus.Cancelled)
        {
            return Error.Conflict("plan.invalid_state", $"A plan in status '{Status}' cannot be cancelled.");
        }

        foreach (var item in _items.Where(i => i.Status is ItemStatus.Planned or ItemStatus.Scheduled))
        {
            item.MarkCancelled();
        }

        Status = PlanStatus.Cancelled;
        return Result.Success();
    }

    public Result<PlanItem> PerformItem(Guid itemId, Guid visitId, Guid performedBy, DateTimeOffset now)
    {
        if (Status is not (PlanStatus.Accepted or PlanStatus.InProgress))
        {
            return Error.Conflict("plan.not_accepted", "Procedures can only be performed under an accepted plan.");
        }

        var item = _items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
        {
            return Error.NotFound("plan.item_not_found", "Plan item not found.");
        }

        if (item.Status is not (ItemStatus.Planned or ItemStatus.Scheduled))
        {
            return Error.Conflict("plan.item_not_pending", $"The item is already '{item.Status}'.");
        }

        item.MarkDone(visitId, performedBy, now);
        Raise(new ProcedurePerformed(
            Guid.NewGuid(), now, Id, item.Id, visitId, PatientId, ProviderId, item.ProcedureCode, item.ToothFdi, item.Quantity, item.UnitPrice, item.DiscountPercent));

        Status = PlanStatus.InProgress;
        if (_items.All(i => i.Status is ItemStatus.Done or ItemStatus.Cancelled))
        {
            Status = PlanStatus.Completed;
            Raise(new TreatmentPlanCompleted(Guid.NewGuid(), now, Id, PatientId));
        }

        return item;
    }

    private static Error? ValidateItem(NewPlanItem i)
    {
        if (string.IsNullOrWhiteSpace(i.ProcedureCode))
        {
            return Error.Validation("plan.invalid_item", "Procedure code is required.");
        }

        if (i.ToothFdi is { } t && !Fdi.IsValid(t))
        {
            return Error.Validation("plan.invalid_tooth", "Tooth number must be a valid FDI number.");
        }

        if (i.Surface is { } s && !ToothRecord.Surfaces.Contains(s, StringComparison.Ordinal))
        {
            return Error.Validation("plan.invalid_surface", "Surface must be one of M, D, O, B, L, I, F, P.");
        }

        if (i.Phase is < 1 or > 20 || i.Quantity is < 1 or > 99)
        {
            return Error.Validation("plan.invalid_item", "Phase must be 1-20 and quantity 1-99.");
        }

        if (i.UnitPrice is < 0 or > 10_000_000 || i.DiscountPercent is < 0 or > 100)
        {
            return Error.Validation("plan.invalid_price", "Price must be non-negative and discount 0-100%.");
        }

        return null;
    }
}
