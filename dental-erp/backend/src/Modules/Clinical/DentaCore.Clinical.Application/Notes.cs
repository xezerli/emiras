using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Clinical.Domain;
using DentaCore.Patient.Contracts;
using FluentValidation;

namespace DentaCore.Clinical.Application;

public sealed record NoteDto(
    Guid Id, Guid PatientId, Guid? VisitId, Guid AuthorId, string? Subjective, string? Objective, string? Assessment, string? Plan,
    string Source, DateTimeOffset? SignedAt, Guid? AddendumOf, DateTimeOffset CreatedAt, int RowVersion);

internal static class NoteMapper
{
    public static NoteDto ToDto(ClinicalNote n) =>
        new(n.Id, n.PatientId, n.VisitId, n.AuthorId, n.Subjective, n.Objective, n.Assessment, n.Plan, n.Source, n.SignedAt, n.AddendumOf, n.CreatedAt, n.RowVersion);
}

public sealed record CreateNoteCommand(Guid PatientId, Guid? VisitId, string? Subjective, string? Objective, string? Assessment, string? Plan, string? Source, Guid? AddendumOf)
    : ICommand<NoteDto>, IRequiresAccess, IAuditable<NoteDto>
{
    public string Permission => ClinicalPermissions.Write;

    public AuditDescriptor Describe(NoteDto response) => new(AddendumOf is null ? "note.create" : "note.addendum", "clinical_note", response?.Id);
}

public sealed class CreateNoteCommandValidator : AbstractValidator<CreateNoteCommand>
{
    public CreateNoteCommandValidator() => RuleFor(x => x.PatientId).NotEmpty();
}

internal sealed class CreateNoteCommandHandler(IClinicalRepository repository, ClinicalAccess access, ICurrentUser user, IClock clock)
    : IRequestHandler<CreateNoteCommand, NoteDto>
{
    public async Task<Result<NoteDto>> Handle(CreateNoteCommand request, CancellationToken cancellationToken)
    {
        var patient = await access.RequireAsync(request.PatientId, ClinicalPermissions.Write, cancellationToken);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        if (request.VisitId is { } visitId)
        {
            var visit = await VisitGuard.RequireOpenAsync(repository, visitId, request.PatientId, cancellationToken);
            if (visit.IsFailure)
            {
                return visit.Error!;
            }
        }

        if (request.AddendumOf is { } originalId)
        {
            var original = await repository.GetNoteAsync(originalId, cancellationToken);
            if (original is null || original.PatientId != request.PatientId)
            {
                return Error.Validation("note.original_not_found", "The note to amend does not exist for this patient.");
            }

            if (!original.IsSigned)
            {
                return Error.Validation("note.original_not_signed", "Only a signed note can be amended; edit the draft instead.");
            }
        }

        var note = ClinicalNote.Create(
            Guid.NewGuid(), request.PatientId, request.VisitId, user.UserId,
            new SoapText(request.Subjective, request.Objective, request.Assessment, request.Plan), request.Source ?? "typed", request.AddendumOf, clock.UtcNow);
        if (note.IsFailure)
        {
            return note.Error!;
        }

        repository.Add(note.Value);
        return NoteMapper.ToDto(note.Value);
    }
}

public sealed record EditNoteCommand(Guid NoteId, string? Subjective, string? Objective, string? Assessment, string? Plan)
    : ICommand<NoteDto>, IRequiresAccess, IAuditable<NoteDto>
{
    public string Permission => ClinicalPermissions.Write;

    public AuditDescriptor Describe(NoteDto response) => new("note.update", "clinical_note", NoteId);
}

internal sealed class EditNoteCommandHandler(IClinicalRepository repository, ClinicalAccess access, ICurrentUser user)
    : IRequestHandler<EditNoteCommand, NoteDto>
{
    public async Task<Result<NoteDto>> Handle(EditNoteCommand request, CancellationToken cancellationToken)
    {
        var note = await repository.GetNoteAsync(request.NoteId, cancellationToken);
        if (note is null || (await access.RequireAsync(note.PatientId, ClinicalPermissions.Write, cancellationToken)).IsFailure)
        {
            return Error.NotFound("note.not_found", "Note not found.");
        }

        if (note.AuthorId != user.UserId)
        {
            return Error.Forbidden("note.not_author", "Only the author can change a note.");
        }

        var edited = note.Edit(new SoapText(request.Subjective, request.Objective, request.Assessment, request.Plan));
        return edited.IsFailure ? edited.Error! : NoteMapper.ToDto(note);
    }
}

public sealed record SignNoteCommand(Guid NoteId) : ICommand<NoteDto>, IRequiresAccess, IAuditable<NoteDto>
{
    public string Permission => ClinicalPermissions.Write;

    public AuditDescriptor Describe(NoteDto response) => new("note.sign", "clinical_note", NoteId);
}

internal sealed class SignNoteCommandHandler(IClinicalRepository repository, ClinicalAccess access, ICurrentUser user, IClock clock)
    : IRequestHandler<SignNoteCommand, NoteDto>
{
    public async Task<Result<NoteDto>> Handle(SignNoteCommand request, CancellationToken cancellationToken)
    {
        var note = await repository.GetNoteAsync(request.NoteId, cancellationToken);
        if (note is null || (await access.RequireAsync(note.PatientId, ClinicalPermissions.Write, cancellationToken)).IsFailure)
        {
            return Error.NotFound("note.not_found", "Note not found.");
        }

        if (note.AuthorId != user.UserId)
        {
            return Error.Forbidden("note.not_author", "Only the author can sign a note.");
        }

        var signed = note.Sign(clock.UtcNow);
        return signed.IsFailure ? signed.Error! : NoteMapper.ToDto(note);
    }
}

public sealed record ListNotesQuery(Guid PatientId) : IQuery<IReadOnlyList<NoteDto>>, IRequiresAccess, IAuditable<IReadOnlyList<NoteDto>>
{
    public string Permission => ClinicalPermissions.Read;

    public AuditDescriptor Describe(IReadOnlyList<NoteDto> response) => new("note.list", "patient", PatientId, ClinicalText.ToJson(new { results = response?.Count ?? 0 }));
}

internal sealed class ListNotesQueryHandler(IClinicalRepository repository, ClinicalAccess access, ICurrentUser user)
    : IRequestHandler<ListNotesQuery, IReadOnlyList<NoteDto>>
{
    public async Task<Result<IReadOnlyList<NoteDto>>> Handle(ListNotesQuery request, CancellationToken cancellationToken)
    {
        var patient = await access.RequireAsync(request.PatientId, ClinicalPermissions.Read, cancellationToken);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        // Qaralamalar yalnız müəllifə görünür; imzalanmış qeydlər isə giriş hüququ olan hər klinisistə
        IReadOnlyList<NoteDto> notes = (await repository.ListNotesAsync(request.PatientId, cancellationToken))
            .Where(n => n.IsSigned || n.AuthorId == user.UserId)
            .Select(NoteMapper.ToDto)
            .ToList();
        return Result.Success(notes);
    }
}

// ---------------- Resept ----------------
public sealed record PrescriptionItemInput(string Drug, string Dose, string? Frequency, int? Days, string? Notes);

public sealed record PrescriptionDto(
    Guid Id, Guid PatientId, Guid? VisitId, Guid ProviderId, DateTimeOffset IssuedAt, IReadOnlyList<PrescriptionItem> Items, bool AllergyCheckPassed, string? OverrideReason);

internal static class PrescriptionMapper
{
    public static PrescriptionDto ToDto(Prescription p) =>
        new(p.Id, p.PatientId, p.VisitId, p.ProviderId, p.IssuedAt, p.Items, p.AllergyCheckPassed, p.OverrideReason);
}

public sealed record IssuePrescriptionCommand(Guid PatientId, Guid? VisitId, IReadOnlyList<PrescriptionItemInput> Items, bool OverrideAllergyWarning, string? OverrideReason)
    : ICommand<PrescriptionDto>, IRequiresAccess, IAuditable<PrescriptionDto>
{
    public string Permission => ClinicalPermissions.Prescribe;

    // Dərman adları audit-ə yazılmır (sağlamlıq məlumatı), yalnız fakt və override bayrağı
    public AuditDescriptor Describe(PrescriptionDto response) =>
        new(response is { AllergyCheckPassed: false } ? "prescription.allergy_override" : "prescription.issue", "prescription", response?.Id, ClinicalText.ToJson(new { items = Items.Count }));
}

public sealed class IssuePrescriptionCommandValidator : AbstractValidator<IssuePrescriptionCommand>
{
    public IssuePrescriptionCommandValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.Items).NotNull().Must(i => i is { Count: >= 1 and <= 20 }).WithMessage("A prescription needs 1-20 items.");
        RuleFor(x => x.OverrideReason).MaximumLength(1000);
    }
}

internal sealed class IssuePrescriptionCommandHandler(
    IClinicalRepository repository, ClinicalAccess access, IPatientDirectory patients, ICurrentUser user, IClock clock)
    : IRequestHandler<IssuePrescriptionCommand, PrescriptionDto>
{
    public async Task<Result<PrescriptionDto>> Handle(IssuePrescriptionCommand request, CancellationToken cancellationToken)
    {
        var patient = await access.RequireAsync(request.PatientId, ClinicalPermissions.Prescribe, cancellationToken);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        if (request.VisitId is { } visitId)
        {
            var visit = await VisitGuard.RequireOpenAsync(repository, visitId, request.PatientId, cancellationToken);
            if (visit.IsFailure)
            {
                return visit.Error!;
            }
        }

        // Allergiya yoxlaması SERVERDƏDİR: UI-ı keçib birbaşa API-yə yazmaq xəbərdarlığı atlamır
        var allergies = await patients.GetActiveAllergiesAsync(request.PatientId, cancellationToken);
        var conflicts = AllergyChecker.Check(request.Items.Select(i => i.Drug ?? string.Empty), allergies.Select(a => (a.Substance, a.Severity)));
        if (conflicts.Count > 0 && !request.OverrideAllergyWarning)
        {
            return Error.Validation("prescription.allergy_conflict", "The prescription conflicts with the patient's recorded allergies.") with
            {
                Details = new Dictionary<string, object?> { ["conflicts"] = conflicts },
            };
        }

        var prescription = Prescription.Issue(
            Guid.NewGuid(), request.PatientId, request.VisitId, user.UserId,
            request.Items.Select(i => new PrescriptionItem(i.Drug ?? string.Empty, i.Dose ?? string.Empty, i.Frequency, i.Days, i.Notes)).ToList(),
            conflicts.Count > 0, request.OverrideReason, clock.UtcNow);
        if (prescription.IsFailure)
        {
            return prescription.Error!;
        }

        repository.Add(prescription.Value);
        return PrescriptionMapper.ToDto(prescription.Value);
    }
}

public sealed record ListPrescriptionsQuery(Guid PatientId) : IQuery<IReadOnlyList<PrescriptionDto>>, IRequiresAccess, IAuditable<IReadOnlyList<PrescriptionDto>>
{
    public string Permission => ClinicalPermissions.Read;

    public AuditDescriptor Describe(IReadOnlyList<PrescriptionDto> response) =>
        new("prescription.list", "patient", PatientId, ClinicalText.ToJson(new { results = response?.Count ?? 0 }));
}

internal sealed class ListPrescriptionsQueryHandler(IClinicalRepository repository, ClinicalAccess access)
    : IRequestHandler<ListPrescriptionsQuery, IReadOnlyList<PrescriptionDto>>
{
    public async Task<Result<IReadOnlyList<PrescriptionDto>>> Handle(ListPrescriptionsQuery request, CancellationToken cancellationToken)
    {
        var patient = await access.RequireAsync(request.PatientId, ClinicalPermissions.Read, cancellationToken);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        IReadOnlyList<PrescriptionDto> items = (await repository.ListPrescriptionsAsync(request.PatientId, cancellationToken)).Select(PrescriptionMapper.ToDto).ToList();
        return Result.Success(items);
    }
}
