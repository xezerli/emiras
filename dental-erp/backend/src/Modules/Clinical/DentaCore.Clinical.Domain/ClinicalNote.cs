using DentaCore.BuildingBlocks.Domain;

namespace DentaCore.Clinical.Domain;

public sealed record ClinicalNoteSigned(Guid EventId, DateTimeOffset OccurredAt, Guid NoteId, Guid PatientId, Guid AuthorId, Guid? AddendumOf)
    : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "clinical.note-signed";
}

public sealed record SoapText(string? Subjective, string? Objective, string? Assessment, string? Plan)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Subjective) && string.IsNullOrWhiteSpace(Objective)
        && string.IsNullOrWhiteSpace(Assessment) && string.IsNullOrWhiteSpace(Plan);
}

/// <summary>
/// SOAP qeydi. İmzalandıqdan sonra dəyişməzdir (domen də, DB trigger-i də buraxmır). Düzəliş = addendum (yeni qeyd, <see cref="AddendumOf"/>).
/// </summary>
public sealed class ClinicalNote : AggregateRoot<Guid>
{
    private static readonly string[] Sources = ["typed", "voice", "ai_draft"];

    private ClinicalNote()
        : base(Guid.Empty)
    {
    }

    public Guid PatientId { get; private set; }

    public Guid? VisitId { get; private set; }

    public Guid AuthorId { get; private set; }

    public string? Subjective { get; private set; }

    public string? Objective { get; private set; }

    public string? Assessment { get; private set; }

    public string? Plan { get; private set; }

    public string Source { get; private set; } = "typed";

    public DateTimeOffset? SignedAt { get; private set; }

    public Guid? AddendumOf { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsSigned => SignedAt is not null;

    public static Result<ClinicalNote> Create(Guid id, Guid patientId, Guid? visitId, Guid authorId, SoapText text, string source, Guid? addendumOf, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Sources.Contains(source))
        {
            return Error.Validation("note.invalid_source", "Source must be typed, voice or ai_draft.");
        }

        var invalid = Validate(text);
        if (invalid is not null)
        {
            return invalid;
        }

        return new ClinicalNote
        {
            Id = id,
            PatientId = patientId,
            VisitId = visitId,
            AuthorId = authorId,
            Subjective = Clean(text.Subjective),
            Objective = Clean(text.Objective),
            Assessment = Clean(text.Assessment),
            Plan = Clean(text.Plan),
            Source = source,
            AddendumOf = addendumOf,
            CreatedAt = now,
        };
    }

    public Result Edit(SoapText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (IsSigned)
        {
            return Error.Conflict("clinical.note_signed", "A signed note cannot be changed. Create an addendum instead.");
        }

        var invalid = Validate(text);
        if (invalid is not null)
        {
            return invalid;
        }

        Subjective = Clean(text.Subjective);
        Objective = Clean(text.Objective);
        Assessment = Clean(text.Assessment);
        Plan = Clean(text.Plan);
        return Result.Success();
    }

    public Result Sign(DateTimeOffset now)
    {
        if (IsSigned)
        {
            return Error.Conflict("clinical.note_signed", "The note is already signed.");
        }

        SignedAt = now;
        Raise(new ClinicalNoteSigned(Guid.NewGuid(), now, Id, PatientId, AuthorId, AddendumOf));
        return Result.Success();
    }

    private static Error? Validate(SoapText t)
    {
        if (t.IsEmpty)
        {
            return Error.Validation("note.empty", "At least one SOAP section must be filled in.");
        }

        return new[] { t.Subjective, t.Objective, t.Assessment, t.Plan }.Any(s => s is { Length: > 5000 })
            ? Error.Validation("note.too_long", "Each SOAP section can have at most 5000 characters.")
            : null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
