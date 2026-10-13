using DentaCore.BuildingBlocks.Domain;

namespace DentaCore.Clinical.Domain;

/// <summary>FDI diş nömrələmə: daimi 11–18, 21–28, 31–38, 41–48; süd 51–55, 61–65, 71–75, 81–85.</summary>
public static class Fdi
{
    public static bool IsValid(int tooth)
    {
        var quadrant = tooth / 10;
        var position = tooth % 10;
        return (quadrant is >= 1 and <= 4 && position is >= 1 and <= 8)
            || (quadrant is >= 5 and <= 8 && position is >= 1 and <= 5);
    }

    public static bool IsPrimary(int tooth) => tooth / 10 >= 5;
}

public enum ToothCondition
{
    Healthy,
    Caries,
    Filling,
    Crown,
    Bridge,
    Implant,
    RootCanal,
    Extracted,
    Missing,
    Impacted,
    Fracture,
    PeriapicalLesion,
    Veneer,
}

/// <summary>
/// Odontoqram qeydi. Yeni qeyd köhnəni əvəz edir (<see cref="SupersededAt"/>), heç nə silinmir: diş vəziyyətinin tarixçəsi qalır.
/// </summary>
public sealed class ToothRecord : Entity<Guid>
{
    public const string Surfaces = "MDOBLIFP";

    private ToothRecord()
        : base(Guid.Empty)
    {
    }

    public Guid PatientId { get; private set; }

    public Guid? VisitId { get; private set; }

    public int ToothFdi { get; private set; }

    /// <summary>null = bütün diş.</summary>
    public char? Surface { get; private set; }

    public ToothCondition Condition { get; private set; }

    public string? Material { get; private set; }

    public string? Notes { get; private set; }

    public Guid? RecordedBy { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    public DateTimeOffset? SupersededAt { get; private set; }

    public static Result<ToothRecord> Create(
        Guid id, Guid patientId, Guid? visitId, int toothFdi, char? surface, ToothCondition condition, string? material, string? notes, Guid recordedBy, DateTimeOffset now)
    {
        if (!Fdi.IsValid(toothFdi))
        {
            return Error.Validation("odontogram.invalid_tooth", "Tooth number must be a valid FDI number (11-48 or 51-85).");
        }

        if (surface is { } s && !Surfaces.Contains(s, StringComparison.Ordinal))
        {
            return Error.Validation("odontogram.invalid_surface", "Surface must be one of M, D, O, B, L, I, F, P.");
        }

        if (material is { Length: > 100 } || notes is { Length: > 500 })
        {
            return Error.Validation("odontogram.text_too_long", "Material (max 100) or notes (max 500) is too long.");
        }

        return new ToothRecord
        {
            Id = id,
            PatientId = patientId,
            VisitId = visitId,
            ToothFdi = toothFdi,
            Surface = surface,
            Condition = condition,
            Material = string.IsNullOrWhiteSpace(material) ? null : material.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            RecordedBy = recordedBy,
            RecordedAt = now,
        };
    }
}
