using DentaCore.Billing.Domain;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using FluentValidation;

namespace DentaCore.Billing.Application;

public sealed record RecordPaymentCommand(Guid InvoiceId, string Method, decimal Amount, string? Reference, string IdempotencyKey)
    : ICommand<PaymentDto>, IRequiresAccess, IAuditable<PaymentDto>
{
    public string Permission => BillingPermissions.PaymentWrite;

    public AuditDescriptor Describe(PaymentDto response) =>
        new("payment.receive", "invoice", InvoiceId, BillingText.ToJson(new { paymentId = response?.Id, amount = response?.Amount, method = response?.Method }));
}

public sealed class RecordPaymentCommandValidator : AbstractValidator<RecordPaymentCommand>
{
    public RecordPaymentCommandValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.Method).NotEmpty();
        RuleFor(x => x.Reference).MaximumLength(200);
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(64);
    }
}

/// <summary>
/// Ödəniş qəbulu. Ardıcıllıq: icazə → idempotency → tranzaksiya → faktura sətrini kilidlə (FOR UPDATE) → smen kilidi (nağd) → tətbiq et → yaz.
/// Kilid sayəsində eyni fakturaya paralel iki ödəniş ardıcıllaşır və ikincisi artıq güncəllənmiş qalıq borcu görür.
/// </summary>
internal sealed class RecordPaymentCommandHandler(
    IBillingRepository repository, BillingAccess access, ICurrentUser user, IBillingUnitOfWork unitOfWork, IClock clock)
    : IRequestHandler<RecordPaymentCommand, PaymentDto>
{
    public async Task<Result<PaymentDto>> Handle(RecordPaymentCommand request, CancellationToken cancellationToken)
    {
        if (!PaymentMethods.IsSupported(request.Method))
        {
            return Error.Validation("billing.method_not_supported", $"Supported payment methods: {string.Join(", ", PaymentMethods.Supported)}.");
        }

        var preview = await repository.GetInvoiceSnapshotAsync(request.InvoiceId, cancellationToken);
        if (preview is null || !await access.CanSeeAsync(preview, BillingPermissions.PaymentWrite, cancellationToken))
        {
            return BillingAccess.NotFound();
        }

        var replay = await ReplayAsync(request, cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        try
        {
            return await ApplyAsync(request, cancellationToken);
        }
        catch (ConstraintViolationException ex) when (ex.Kind == ConstraintKind.Unique && ex.ConstraintName?.Contains("idempotency", StringComparison.Ordinal) == true)
        {
            // Eyni açarla paralel iki sorğu: biri yazdı, digəri onun nəticəsini qaytarır
            unitOfWork.DiscardChanges();
            return await ReplayAsync(request, cancellationToken) ?? Error.Conflict("payment.idempotency_conflict", "The idempotency key is in use.");
        }
    }

    private async Task<Result<PaymentDto>?> ReplayAsync(RecordPaymentCommand request, CancellationToken cancellationToken)
    {
        var existing = await repository.FindPaymentByKeyAsync(request.IdempotencyKey, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var same = existing.Kind == PaymentKind.Payment && existing.InvoiceId == request.InvoiceId && existing.Amount == request.Amount && existing.Method == request.Method;
        if (!same)
        {
            return Error.Conflict("payment.idempotency_key_reuse", "This Idempotency-Key was already used for a different request.");
        }

        var invoice = await repository.GetInvoiceSnapshotAsync(existing.InvoiceId, cancellationToken);
        return BillingMapper.ToDto(existing, invoice is null ? null : BillingText.Status(invoice.Status));
    }

    private async Task<Result<PaymentDto>> ApplyAsync(RecordPaymentCommand request, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var invoice = await repository.GetInvoiceForUpdateAsync(request.InvoiceId, cancellationToken);
        if (invoice is null)
        {
            return BillingAccess.NotFound();
        }

        var shift = await repository.GetOpenShiftAsync(user.UserId, cancellationToken);
        var isCash = request.Method == "cash";
        if (isCash && shift is null)
        {
            return Error.Validation("billing.shift_required", "Open a cash shift before taking cash payments.");
        }

        if (shift is not null && shift.BranchId != invoice.BranchId)
        {
            if (isCash)
            {
                return Error.Validation("billing.shift_branch_mismatch", "Your open shift belongs to a different branch.");
            }

            shift = null;   // başqa filialın kassası ilə bağlanmır
        }

        if (shift is not null && !await repository.LockShiftAsync(shift.Id, exclusive: false, cancellationToken))
        {
            return Error.Conflict("billing.shift_closed", "The shift was closed. Open a new one.");
        }

        var now = clock.UtcNow;
        var paymentId = Guid.NewGuid();
        var applied = invoice.ApplyPayment(paymentId, request.Amount, request.Method, now);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        var payment = Payment.Receive(paymentId, invoice, shift?.Id, request.Method, request.Amount, request.Reference, request.IdempotencyKey, user.UserId, now);
        repository.Add(payment);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return BillingMapper.ToDto(payment, BillingText.Status(invoice.Status));
    }
}

public sealed record RefundPaymentCommand(Guid PaymentId, decimal Amount, string Reason, string IdempotencyKey)
    : ICommand<PaymentDto>, IRequiresAccess, IAuditable<PaymentDto>
{
    public string Permission => BillingPermissions.Refund;

    public AuditDescriptor Describe(PaymentDto response) =>
        new("payment.refund", "payment", PaymentId, BillingText.ToJson(new { refundId = response?.Id, amount = response?.Amount }));
}

public sealed class RefundPaymentCommandValidator : AbstractValidator<RefundPaymentCommand>
{
    public RefundPaymentCommandValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(64);
    }
}

/// <summary>
/// Geri qaytarma əks əməliyyatdır (ödəniş sətri dəyişmir). Limit rolun <c>role_permissions.max_amount</c> dəyərindən gəlir:
/// null = limitsiz, 0 = məbləğdən asılı olmayaraq rəhbər təsdiqi lazımdır. Bir ödənişdən geri qaytarıla bilən məbləğ = ödəniş − əvvəlki geri qaytarmalar.
/// </summary>
internal sealed class RefundPaymentCommandHandler(
    IBillingRepository repository, BillingAccess access, IRefundLimits limits, ICurrentUser user, IBillingUnitOfWork unitOfWork, IClock clock)
    : IRequestHandler<RefundPaymentCommand, PaymentDto>
{
    public async Task<Result<PaymentDto>> Handle(RefundPaymentCommand request, CancellationToken cancellationToken)
    {
        var original = await repository.GetPaymentAsync(request.PaymentId, cancellationToken);
        var preview = original is null ? null : await repository.GetInvoiceSnapshotAsync(original.InvoiceId, cancellationToken);
        if (original is null || preview is null || !await access.CanSeeAsync(preview, BillingPermissions.Refund, cancellationToken))
        {
            return Error.NotFound("payment.not_found", "Payment not found.");
        }

        if (original.Kind != PaymentKind.Payment)
        {
            return Error.Validation("payment.not_refundable", "Only a payment can be refunded.");
        }

        var replay = await repository.FindPaymentByKeyAsync(request.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            return replay.Kind == PaymentKind.Refund && replay.RefundOf == original.Id && replay.Amount == request.Amount
                ? BillingMapper.ToDto(replay, BillingText.Status(preview.Status))
                : Error.Conflict("payment.idempotency_key_reuse", "This Idempotency-Key was already used for a different request.");
        }

        var limit = await limits.GetAsync(user.UserId, cancellationToken);
        if (!limit.HasPermission || (limit.Max is { } max && request.Amount > max))
        {
            return Error.Forbidden("billing.refund_limit_exceeded", "The amount exceeds your refund limit. Ask a manager.") with
            {
                Details = new Dictionary<string, object?> { ["limit"] = limit.Max ?? 0m },
            };
        }

        try
        {
            return await ApplyAsync(request, original, cancellationToken);
        }
        catch (ConstraintViolationException ex) when (ex.Kind == ConstraintKind.Unique && ex.ConstraintName?.Contains("idempotency", StringComparison.Ordinal) == true)
        {
            unitOfWork.DiscardChanges();
            var raced = await repository.FindPaymentByKeyAsync(request.IdempotencyKey, cancellationToken);
            return raced is { Kind: PaymentKind.Refund } && raced.RefundOf == original.Id && raced.Amount == request.Amount
                ? BillingMapper.ToDto(raced, null)
                : Error.Conflict("payment.idempotency_conflict", "The idempotency key is in use.");
        }
    }

    private async Task<Result<PaymentDto>> ApplyAsync(RefundPaymentCommand request, Payment original, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var invoice = await repository.GetInvoiceForUpdateAsync(original.InvoiceId, cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound("payment.not_found", "Payment not found.");
        }

        var refundable = original.Amount - await repository.SumRefundedAsync(original.Id, cancellationToken);
        if (request.Amount > refundable)
        {
            return Error.Validation("billing.refund_exceeds_payment", "The amount exceeds what is left to refund on this payment.") with
            {
                Details = new Dictionary<string, object?> { ["refundable"] = refundable },
            };
        }

        var shift = await repository.GetOpenShiftAsync(user.UserId, cancellationToken);
        if (shift is not null && shift.BranchId != invoice.BranchId)
        {
            shift = null;
        }

        var isCash = original.Method == "cash";
        if (isCash && shift is null)
        {
            return Error.Validation("billing.shift_required", "Open a cash shift (in the invoice's branch) before refunding cash.");
        }

        if (shift is not null && !await repository.LockShiftAsync(shift.Id, exclusive: false, cancellationToken))
        {
            return Error.Conflict("billing.shift_closed", "The shift was closed. Open a new one.");
        }

        var now = clock.UtcNow;
        var refundId = Guid.NewGuid();
        var applied = invoice.ApplyRefund(refundId, original.Id, request.Amount, now);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        var refund = Payment.Refund(refundId, invoice, original, shift?.Id, request.Amount, request.Reason.Trim(), request.IdempotencyKey, user.UserId, now);
        repository.Add(refund);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return BillingMapper.ToDto(refund, BillingText.Status(invoice.Status));
    }
}
