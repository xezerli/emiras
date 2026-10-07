using DentaCore.BuildingBlocks.Domain;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace DentaCore.BuildingBlocks.Application;

/// <summary>FluentValidation xətalarını Result.Failure(Validation) kimi qaytarır, handler işə düşmür.</summary>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<Result<TResponse>> Handle(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken)
    {
        var validatorList = validators.ToList();
        if (validatorList.Count == 0)
        {
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validatorList)
        {
            var result = await validator.ValidateAsync(context, cancellationToken);
            failures.AddRange(result.Errors);
        }

        if (failures.Count == 0)
        {
            return await next();
        }

        var message = string.Join("; ", failures.Select(f => $"{f.PropertyName}: {f.ErrorMessage}"));
        return Error.Validation("validation.failed", message);
    }
}

/// <summary>
/// Əmr uğurla bitəndə dəyişiklikləri (və outbox mesajlarını) bir tranzaksiyada saxlayır. Query üçün işləmir.
/// Uğursuz nəticənin yan təsiri varsa (məs. uğursuz giriş sayğacı), handler özü SaveChangesAsync çağırır.
/// </summary>
public sealed class UnitOfWorkBehavior<TRequest, TResponse>(IEnumerable<IModuleUnitOfWork> unitsOfWork)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICommand<TResponse>
{
    public async Task<Result<TResponse>> Handle(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken)
    {
        var result = await next();
        if (result.IsSuccess)
        {
            var assembly = typeof(TRequest).Assembly;
            var unitOfWork = unitsOfWork.SingleOrDefault(u => u.ApplicationAssembly == assembly)
                ?? throw new InvalidOperationException($"No IModuleUnitOfWork registered for {assembly.GetName().Name}.");
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>Yalnız sorğunun adını və nəticəni loglayır. Sorğunun məzmunu (parol, token) heç vaxt loglanmır.</summary>
public sealed partial class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<Result<TResponse>> Handle(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken)
    {
        var result = await next();
        if (result.IsFailure)
        {
            LogFailed(logger, typeof(TRequest).Name, result.Error!.Code);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Request} failed with {ErrorCode}")]
    private static partial void LogFailed(ILogger logger, string request, string errorCode);
}

/// <summary>İcazəni handler-dən əvvəl yoxlayır. Rədd halı da auditə düşür ("kim nəyə icazəsiz cəhd etdi").</summary>
public sealed class AuthorizationBehavior<TRequest, TResponse>(ICurrentUser currentUser, IEnumerable<IAuditTrail> audit, IClock clock)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<Result<TResponse>> Handle(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not IRequiresAccess required)
        {
            return await next();
        }

        if (!currentUser.IsAuthenticated)
        {
            return Error.Unauthorized("auth.required", "Authentication is required.");
        }

        if (currentUser.ScopeOf(required.Permission) is null)
        {
            var trail = audit.FirstOrDefault();
            if (trail is not null)
            {
                await trail.RecordAsync(
                    new AuditRecord("permission.denied", null, null, currentUser.UserId, currentUser.Ip?.ToString(), clock.UtcNow, $"{{\"permission\":\"{required.Permission}\",\"request\":\"{typeof(TRequest).Name}\"}}"),
                    cancellationToken);
            }

            return Error.Forbidden("permission.denied", "You do not have permission to perform this action.");
        }

        return await next();
    }
}

/// <summary>
/// Uğurlu IAuditable sorğunu audit zəncirinə yazır. UnitOfWork-dan XARİCDƏ dayanır: dəyişiklik saxlandıqdan sonra işləyir.
/// Audit yazıla bilmirsə sorğu uğursuz olur (fail-closed): audit olunmamış PHI girişi qəbul edilmir.
/// </summary>
public sealed class AuditBehavior<TRequest, TResponse>(ICurrentUser currentUser, IEnumerable<IAuditTrail> audit, IClock clock)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<Result<TResponse>> Handle(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken)
    {
        var result = await next();
        if (result.IsFailure || request is not IAuditable<TResponse> auditable)
        {
            return result;
        }

        var trail = audit.FirstOrDefault()
            ?? throw new InvalidOperationException($"{typeof(TRequest).Name} is auditable but no IAuditTrail is registered.");
        var d = auditable.Describe(result.Value);
        await trail.RecordAsync(
            new AuditRecord(d.Action, d.Entity, d.EntityId, currentUser.IsAuthenticated ? currentUser.UserId : null, currentUser.Ip?.ToString(), clock.UtcNow, d.DetailsJson),
            cancellationToken);
        return result;
    }
}
