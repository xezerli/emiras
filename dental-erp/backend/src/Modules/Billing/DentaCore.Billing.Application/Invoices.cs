using DentaCore.Billing.Domain;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Patient.Contracts;
using DentaCore.Scheduling.Contracts;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace DentaCore.Billing.Application;

public sealed record InvoiceItemDto(Guid Id, Guid? ServiceId, Guid? PlanItemId, string Description, int? ToothFdi, Guid? ProviderId, int Quantity, decimal UnitPrice, decimal Discount, decimal VatRate, decimal LineTotal);

public sealed record PaymentDto(Guid Id, Guid InvoiceId, string Kind, string Method, decimal Amount, string Currency, string? Reference, Guid? ShiftId, Guid? RefundOf, string? Reason, DateTimeOffset PaidAt, string? InvoiceStatus);

public sealed record InvoiceDto(
    Guid Id, string Number, string Kind, Guid BranchId, Guid PatientId, Guid? ProviderId, Guid? VisitId, Guid? PlanId, string Status, string Currency,
    decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal Total, decimal PaidTotal, decimal InsuranceAmount, decimal Balance,
    DateTimeOffset? IssuedAt, DateOnly? DueDate, DateTimeOffset CreatedAt, int RowVersion,
    IReadOnlyList<InvoiceItemDto> Items, IReadOnlyList<PaymentDto>? Payments);

internal static class BillingMapper
{
    public static InvoiceDto ToDto(Invoice i, IReadOnlyList<Payment>? payments = null) => new(
        i.Id, i.Number, i.Kind.ToString().ToLowerInvariant(), i.BranchId, i.PatientId, i.ProviderId, i.VisitId, i.PlanId, BillingText.Status(i.Status), i.Currency,
        i.Subtotal, i.DiscountTotal, i.TaxTotal, i.Total, i.PaidTotal, i.InsuranceAmount, i.Balance,
        i.IssuedAt, i.DueDate, i.CreatedAt, i.RowVersion,
        i.Items.Select(x => new InvoiceItemDto(x.Id, x.ServiceId, x.PlanItemId, x.Description, x.ToothFdi, x.ProviderId, x.Quantity, x.UnitPrice, x.Discount, x.VatRate, x.LineTotal)).ToList(),
        payments?.Select(p => ToDto(p, null)).ToList());

    public static PaymentDto ToDto(Payment p, string? invoiceStatus) => new(
        p.Id, p.InvoiceId, p.Kind.ToString().ToLowerInvariant(), p.Method, p.Amount, p.Currency, p.Reference, p.ShiftId, p.RefundOf, p.Reason, p.PaidAt, invoiceStatus);
}

public sealed record InvoiceItemInput(Guid? ServiceId, Guid? PlanItemId, string? Description, int? ToothFdi, Guid? ProviderId, int Quantity, decimal? UnitPrice, decimal? Discount);

public sealed record CreateInvoiceCommand(
    string Kind, Guid PatientId, Guid BranchId, Guid? VisitId, Guid? PlanId, string? PromoCode, Guid? InsurancePolicyId, DateOnly? DueDate, IReadOnlyList<InvoiceItemInput> Items)
    : ICommand<InvoiceDto>, IRequiresAccess, IAuditable<InvoiceDto>
{
    public string Permission => BillingPermissions.InvoiceWrite;

    public AuditDescriptor Describe(InvoiceDto response) =>
        new("invoice.create", "invoice", response?.Id, BillingText.ToJson(new { kind = response?.Kind, total = response?.Total }));
}

public sealed class CreateInvoiceCommandValidator : AbstractValidator<CreateInvoiceCommand>
{
    public CreateInvoiceCommandValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x.Kind).Must(k => k is "estimate" or "invoice").WithMessage("Kind must be 'estimate' or 'invoice'.");
        RuleFor(x => x.Items).NotNull().Must(i => i is { Count: > 0 and <= Invoice.MaxItems }).WithMessage($"Between 1 and {Invoice.MaxItems} items are required.");
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.Quantity).InclusiveBetween(1, 1000);
            item.RuleFor(i => i.Description).MaximumLength(500);
            item.RuleFor(i => i).Must(i => i.ServiceId is not null || (!string.IsNullOrWhiteSpace(i.Description) && i.UnitPrice is not null))
                .WithMessage("An item needs either a serviceId or both a description and a unitPrice.");
        });
    }
}

internal sealed class CreateInvoiceCommandHandler(
    IBillingRepository repository, IPatientDirectory patients, ICurrentUser user, IBillingUnitOfWork unitOfWork, IClock clock, IOptions<BillingOptions> options)
    : IRequestHandler<CreateInvoiceCommand, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> Handle(CreateInvoiceCommand request, CancellationToken cancellationToken)
    {
        if (!user.CanAccess(BillingPermissions.InvoiceWrite, request.BranchId))
        {
            return Error.Forbidden("billing.branch_forbidden", "You cannot create invoices for this branch.");
        }

        if (request.PromoCode is not null || request.InsurancePolicyId is not null)
        {
            return Error.Validation("billing.discount_source_not_supported", "Promo codes and insurance are not supported yet.");
        }

        if (await patients.FindAsync(request.PatientId, cancellationToken) is null)
        {
            return Error.NotFound("patient.not_found", "Patient not found.");
        }

        var resolved = await ResolveItemsAsync(request.Items, cancellationToken);
        if (resolved.IsFailure)
        {
            return resolved.Error!;
        }

        var (items, currency) = resolved.Value;
        var kind = request.Kind == "estimate" ? InvoiceKind.Estimate : InvoiceKind.Invoice;
        var invoice = Invoice.Create(
            Guid.NewGuid(), kind, request.BranchId, request.PatientId, null, request.VisitId, request.PlanId, currency ?? options.Value.DefaultCurrency,
            request.DueDate, null, items, user.UserId, clock.UtcNow);
        if (invoice.IsFailure)
        {
            return invoice.Error!;
        }

        repository.Add(invoice.Value);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConstraintViolationException ex) when (ex.Kind == ConstraintKind.ForeignKey)
        {
            unitOfWork.DiscardChanges();
            return Error.Validation("billing.reference_invalid", "The branch, visit, plan, provider or plan item does not exist.");
        }
        catch (ConstraintViolationException ex) when (ex.Kind == ConstraintKind.Unique)
        {
            unitOfWork.DiscardChanges();
            return ex.ConstraintName == "ux_invoice_items_plan_item"
                ? Error.Conflict("billing.plan_item_already_invoiced", "A plan item has already been invoiced.")
                : Error.Conflict("billing.draft_exists", "A draft invoice already exists for this visit.");
        }

        return BillingMapper.ToDto(invoice.Value);
    }

    private async Task<Result<(List<NewInvoiceItem> Items, string? Currency)>> ResolveItemsAsync(IReadOnlyList<InvoiceItemInput> inputs, CancellationToken cancellationToken)
    {
        var serviceIds = inputs.Where(i => i.ServiceId is not null).Select(i => i.ServiceId!.Value).Distinct().ToList();
        var services = (await repository.GetServicesAsync(serviceIds, cancellationToken)).ToDictionary(s => s.Id);
        string? currency = null;
        var items = new List<NewInvoiceItem>(inputs.Count);
        foreach (var input in inputs)
        {
            Service? service = null;
            if (input.ServiceId is { } serviceId && (!services.TryGetValue(serviceId, out service) || !service.IsActive))
            {
                return Error.Validation("billing.service_unavailable", "A service in the list does not exist or is inactive.");
            }

            if (service is not null)
            {
                currency ??= service.Currency;
                if (!string.Equals(currency, service.Currency, StringComparison.Ordinal))
                {
                    return Error.Validation("billing.mixed_currency", "All items of an invoice must use the same currency.");
                }
            }

            items.Add(new NewInvoiceItem(
                input.ServiceId, input.PlanItemId, input.Description ?? service!.Name, input.ToothFdi, input.ProviderId, input.Quantity,
                input.UnitPrice ?? service!.Price, input.Discount ?? 0m, service?.VatRate ?? 0m));
        }

        return (items, currency);
    }
}

public sealed record GetInvoiceQuery(Guid InvoiceId) : IQuery<InvoiceDto>, IRequiresAccess, IAuditable<InvoiceDto>
{
    public string Permission => BillingPermissions.InvoiceRead;

    public AuditDescriptor Describe(InvoiceDto response) => new("invoice.read", "invoice", InvoiceId);
}

internal sealed class GetInvoiceQueryHandler(IBillingRepository repository, BillingAccess access) : IRequestHandler<GetInvoiceQuery, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> Handle(GetInvoiceQuery request, CancellationToken cancellationToken)
    {
        var invoice = await repository.GetInvoiceSnapshotAsync(request.InvoiceId, cancellationToken);
        if (invoice is null || !await access.CanSeeAsync(invoice, BillingPermissions.InvoiceRead, cancellationToken))
        {
            return BillingAccess.NotFound();
        }

        return BillingMapper.ToDto(invoice, await repository.ListPaymentsAsync(invoice.Id, cancellationToken));
    }
}

public sealed record InvoicePageDto(IReadOnlyList<InvoiceDto> Items, string? NextCursor);

public sealed record ListInvoicesQuery(Guid? PatientId, string? Status, bool Overdue, string? Cursor, int Limit)
    : IQuery<InvoicePageDto>, IRequiresAccess, IAuditable<InvoicePageDto>
{
    public string Permission => BillingPermissions.InvoiceRead;

    public AuditDescriptor Describe(InvoicePageDto response) =>
        new("invoice.list", PatientId is null ? null : "patient", PatientId, BillingText.ToJson(new { results = response?.Items.Count ?? 0 }));
}

public sealed class ListInvoicesQueryValidator : AbstractValidator<ListInvoicesQuery>
{
    public ListInvoicesQueryValidator()
    {
        RuleFor(x => x.Limit).InclusiveBetween(1, 100);
    }
}

internal sealed class ListInvoicesQueryHandler(
    IBillingRepository repository, ICurrentUser user, ICareRelationships care, IClock clock, IOptions<BillingOptions> options)
    : IRequestHandler<ListInvoicesQuery, InvoicePageDto>
{
    public async Task<Result<InvoicePageDto>> Handle(ListInvoicesQuery request, CancellationToken cancellationToken)
    {
        InvoiceStatus? status = null;
        if (request.Status is not null)
        {
            if (!BillingText.TryParseStatus(request.Status, out var parsed))
            {
                return Error.Validation("invoice.invalid_status", "Unknown invoice status.");
            }

            status = parsed;
        }

        var scope = user.ScopeOf(BillingPermissions.InvoiceRead);
        IReadOnlyCollection<Guid>? ownPatients = null;
        Guid? ownUser = null;
        if (scope == PermissionScope.Own)
        {
            ownUser = user.UserId;
            ownPatients = await care.PatientIdsAsync(user.UserId, cancellationToken);
        }

        var filter = new InvoiceFilter(request.PatientId, status, request.Overdue, BillingClock.Today(clock, options), user.BranchFilterFor(BillingPermissions.InvoiceRead), ownUser, ownPatients);
        var page = await repository.ListInvoicesAsync(filter, request.Cursor, request.Limit, cancellationToken);
        return new InvoicePageDto(page.Items.Select(i => BillingMapper.ToDto(i)).ToList(), page.NextCursor);
    }
}

public sealed record IssueInvoiceCommand(Guid InvoiceId) : ICommand<InvoiceDto>, IRequiresAccess, IAuditable<InvoiceDto>
{
    public string Permission => BillingPermissions.InvoiceWrite;

    public AuditDescriptor Describe(InvoiceDto response) =>
        new("invoice.issue", "invoice", InvoiceId, BillingText.ToJson(new { total = response?.Total }));
}

internal sealed class IssueInvoiceCommandHandler(
    IBillingRepository repository, BillingAccess access, IBillingUnitOfWork unitOfWork, IClock clock, IOptions<BillingOptions> options)
    : IRequestHandler<IssueInvoiceCommand, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> Handle(IssueInvoiceCommand request, CancellationToken cancellationToken)
    {
        var invoice = await repository.GetInvoiceAsync(request.InvoiceId, cancellationToken);
        if (invoice is null || !await access.CanSeeAsync(invoice, BillingPermissions.InvoiceWrite, cancellationToken))
        {
            return BillingAccess.NotFound();
        }

        var issued = invoice.Issue(clock.UtcNow, BillingClock.Today(clock, options));
        if (issued.IsFailure)
        {
            return issued.Error!;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return BillingMapper.ToDto(invoice);
    }
}

public sealed record VoidInvoiceCommand(Guid InvoiceId) : ICommand<InvoiceDto>, IRequiresAccess, IAuditable<InvoiceDto>
{
    public string Permission => BillingPermissions.InvoiceWrite;

    public AuditDescriptor Describe(InvoiceDto response) => new("invoice.void", "invoice", InvoiceId);
}

internal sealed class VoidInvoiceCommandHandler(IBillingRepository repository, BillingAccess access, IBillingUnitOfWork unitOfWork, IClock clock)
    : IRequestHandler<VoidInvoiceCommand, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> Handle(VoidInvoiceCommand request, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        // Ödənişlə yarışmasın: ləğv paralel ödəniş qəbulu ilə eyni sətri kilidləyir
        var invoice = await repository.GetInvoiceForUpdateAsync(request.InvoiceId, cancellationToken);
        if (invoice is null || !await access.CanSeeAsync(invoice, BillingPermissions.InvoiceWrite, cancellationToken))
        {
            return BillingAccess.NotFound();
        }

        var voided = invoice.Void(clock.UtcNow);
        if (voided.IsFailure)
        {
            return voided.Error!;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return BillingMapper.ToDto(invoice);
    }
}
