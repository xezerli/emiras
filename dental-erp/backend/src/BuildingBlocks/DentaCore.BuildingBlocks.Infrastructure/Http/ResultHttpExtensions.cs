using DentaCore.BuildingBlocks.Domain;
using Microsoft.AspNetCore.Http;

namespace DentaCore.BuildingBlocks.Infrastructure.Http;

/// <summary>Result → HTTP: gözlənilən xətalar RFC 9457 problem+json (Mərhələ 3 §2) kimi qaytarılır.</summary>
public static class ResultHttpExtensions
{
    public static IResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status422UnprocessableEntity,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Locked => StatusCodes.Status423Locked,
            _ => StatusCodes.Status500InternalServerError,
        };

        return Results.Problem(
            statusCode: status,
            title: error.Message,
            type: $"https://errors.dentacore.app/{error.Code}",
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }

    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.Value) : result.Error!.ToProblem();
    }
}
