namespace DentaCore.BuildingBlocks.Domain;

public enum ErrorType
{
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    Locked,
    Failure,
}

/// <summary>Gözlənilən biznes xətası. İstisna (exception) yalnız gözlənilməz hallar üçündür.</summary>
public sealed record Error(string Code, string Message, ErrorType Type)
{
    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error Locked(string code, string message) => new(code, message, ErrorType.Locked);
}

public class Result
{
    protected Result(bool isSuccess, Error? error)
    {
        if (isSuccess == (error is not null))
        {
            throw new ArgumentException("Success result cannot carry an error and failure must.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error? Error { get; }

    public static Result Success() => new(true, null);

    public static Result Failure(Error error) => new(false, error);

    public static implicit operator Result(Error error) => Failure(error);

    public static Result<T> Success<T>(T value) => new(value, true, null);

    public static Result<T> Failure<T>(Error error) => new(default, false, error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T? value, bool isSuccess, Error? error)
        : base(isSuccess, error) => _value = value;

    /// <summary>Yalnız uğurlu nəticədə oxunur, əks halda InvalidOperationException atır.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot read Value of a failed result.");

    public static implicit operator Result<T>(Error error) => Failure<T>(error);

    public static implicit operator Result<T>(T value) => Success(value);
}
