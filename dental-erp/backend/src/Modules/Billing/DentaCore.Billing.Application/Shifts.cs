using DentaCore.Billing.Domain;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using FluentValidation;

namespace DentaCore.Billing.Application;

public sealed record CashShiftDto(Guid Id, Guid BranchId, Guid CashierId, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, decimal OpeningCash, decimal? ClosingCash, decimal? ExpectedCash, decimal? Difference);

internal static class ShiftMapper
{
    public static CashShiftDto ToDto(CashShift s) => new(s.Id, s.BranchId, s.CashierId, s.OpenedAt, s.ClosedAt, s.OpeningCash, s.ClosingCash, s.ExpectedCash, s.Difference);
}

public sealed record OpenCashShiftCommand(Guid BranchId, decimal OpeningCash) : ICommand<CashShiftDto>, IRequiresAccess, IAuditable<CashShiftDto>
{
    public string Permission => BillingPermissions.PaymentWrite;

    public AuditDescriptor Describe(CashShiftDto response) => new("shift.open", "cash_shift", response?.Id, BillingText.ToJson(new { openingCash = OpeningCash }));
}

public sealed class OpenCashShiftCommandValidator : AbstractValidator<OpenCashShiftCommand>
{
    public OpenCashShiftCommandValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x.OpeningCash).GreaterThanOrEqualTo(0);
    }
}

internal sealed class OpenCashShiftCommandHandler(IBillingRepository repository, ICurrentUser user, IBillingUnitOfWork unitOfWork, IClock clock)
    : IRequestHandler<OpenCashShiftCommand, CashShiftDto>
{
    public async Task<Result<CashShiftDto>> Handle(OpenCashShiftCommand request, CancellationToken cancellationToken)
    {
        if (!user.CanAccess(BillingPermissions.PaymentWrite, request.BranchId))
        {
            return Error.Forbidden("billing.branch_forbidden", "You cannot open a shift in this branch.");
        }

        var existing = await repository.GetOpenShiftAsync(user.UserId, cancellationToken);
        if (existing is not null)
        {
            return ShiftOpenError(existing.Id);
        }

        var shift = CashShift.Open(Guid.NewGuid(), request.BranchId, user.UserId, request.OpeningCash, clock.UtcNow);
        if (shift.IsFailure)
        {
            return shift.Error!;
        }

        repository.Add(shift.Value);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConstraintViolationException ex) when (ex.Kind == ConstraintKind.Unique)
        {
            unitOfWork.DiscardChanges();   // ux_shift_open: paralel iki "smeni aç"
            return ShiftOpenError(null);
        }
        catch (ConstraintViolationException ex) when (ex.Kind == ConstraintKind.ForeignKey)
        {
            unitOfWork.DiscardChanges();
            return Error.Validation("billing.reference_invalid", "The branch does not exist.");
        }

        return ShiftMapper.ToDto(shift.Value);
    }

    private static Error ShiftOpenError(Guid? shiftId) =>
        Error.Conflict("shift.already_open", "You already have an open shift.") with
        {
            Details = shiftId is null ? null : new Dictionary<string, object?> { ["shiftId"] = shiftId },
        };
}

public sealed record CloseCashShiftCommand(Guid ShiftId, decimal ClosingCash) : ICommand<CashShiftDto>, IRequiresAccess, IAuditable<CashShiftDto>
{
    public string Permission => BillingPermissions.PaymentWrite;

    public AuditDescriptor Describe(CashShiftDto response) =>
        new("shift.close", "cash_shift", ShiftId, BillingText.ToJson(new { expected = response?.ExpectedCash, closing = response?.ClosingCash, difference = response?.Difference }));
}

public sealed class CloseCashShiftCommandValidator : AbstractValidator<CloseCashShiftCommand>
{
    public CloseCashShiftCommandValidator()
    {
        RuleFor(x => x.ClosingCash).GreaterThanOrEqualTo(0);
    }
}

/// <summary>Gözlənilən nağd = açılış + qəbul olunan nağd − geri qaytarılan nağd. Smen kilidi (FOR UPDATE) bağlama zamanı yeni nağd ödənişi gözləməyə məcbur edir.</summary>
internal sealed class CloseCashShiftCommandHandler(IBillingRepository repository, ICurrentUser user, IBillingUnitOfWork unitOfWork, IClock clock)
    : IRequestHandler<CloseCashShiftCommand, CashShiftDto>
{
    public async Task<Result<CashShiftDto>> Handle(CloseCashShiftCommand request, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await repository.LockShiftAsync(request.ShiftId, exclusive: true, cancellationToken);   // yeni yüklənir: köhnə izlənən nüsxə yoxdur
        var shift = await repository.GetShiftAsync(request.ShiftId, cancellationToken);
        var allowed = shift is not null
            && user.CanAccess(BillingPermissions.PaymentWrite, shift.BranchId, shift.CashierId)
            && (shift.CashierId == user.UserId || user.ScopeOf(BillingPermissions.PaymentWrite) == PermissionScope.Tenant);
        if (shift is null || !allowed)
        {
            return Error.NotFound("shift.not_found", "Shift not found.");
        }

        var expected = shift.OpeningCash + await repository.NetCashAsync(shift.Id, cancellationToken);
        var closed = shift.Close(request.ClosingCash, expected, clock.UtcNow);
        if (closed.IsFailure)
        {
            return closed.Error!;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ShiftMapper.ToDto(shift);
    }
}

public sealed record GetCurrentShiftQuery : IQuery<CashShiftDto>, IRequiresAccess
{
    public string Permission => BillingPermissions.PaymentWrite;
}

internal sealed class GetCurrentShiftQueryHandler(IBillingRepository repository, ICurrentUser user) : IRequestHandler<GetCurrentShiftQuery, CashShiftDto>
{
    public async Task<Result<CashShiftDto>> Handle(GetCurrentShiftQuery request, CancellationToken cancellationToken)
    {
        var shift = await repository.GetOpenShiftAsync(user.UserId, cancellationToken);
        return shift is null ? Error.NotFound("shift.not_found", "You have no open shift.") : ShiftMapper.ToDto(shift);
    }
}
