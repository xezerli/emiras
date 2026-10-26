namespace DentaCore.BuildingBlocks.Domain;

/// <summary>
/// JSON Merge Patch (RFC 7396) fərqi: "sahə göndərilməyib" (None) ilə "sahə null göndərilib" (Some(null)) eyni deyil.
/// </summary>
public readonly record struct Optional<T>(bool HasValue, T? Value);

public static class Optional
{
    public static Optional<T> None<T>() => default;

    public static Optional<T> Some<T>(T? value) => new(true, value);
}
