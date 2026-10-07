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
