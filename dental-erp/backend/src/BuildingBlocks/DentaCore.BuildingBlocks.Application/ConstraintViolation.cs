namespace DentaCore.BuildingBlocks.Application;

public enum ConstraintKind
{
    Unique,
    ForeignKey,
    Exclusion,
}

/// <summary>
/// DB constraint pozuntusu (infrastruktur PostgreSQL xətasını bu tipə çevirir). Application qatı EF/Npgsql bilmir,
/// amma constraint adına görə biznes xətası qaytara bilir (məs. ex_provider_no_overlap → appointment.overlap).
/// </summary>
public sealed class ConstraintViolationException(ConstraintKind kind, string? constraintName, Exception inner)
    : Exception($"{kind} constraint violated: {constraintName}", inner)
{
    public ConstraintKind Kind { get; } = kind;

    public string? ConstraintName { get; } = constraintName;
}
