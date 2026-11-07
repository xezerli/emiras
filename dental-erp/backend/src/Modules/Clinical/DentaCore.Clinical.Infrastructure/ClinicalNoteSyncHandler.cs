using System.Text.Json;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Clinical.Application;
using DentaCore.Sync.Contracts;

namespace DentaCore.Clinical.Infrastructure;

/// <summary>
/// Offline yazılan SOAP qeydləri. Hər dəyişiklik adi əmrlərlə gedir (<see cref="CreateNoteCommand"/>, <see cref="EditNoteCommand"/>):
/// icazə, pasiyent scope-u, açıq vizit qaydası, "yalnız müəllif" və "imzalanmış qeyd dəyişməzdir" burada təkrar yazılmır.
/// Klient id-ni özü yaradır (create idempotentdir). Redaktədə <c>baseVersion</c> uyğun deyilsə konflikt qaytarılır:
/// mətni avtomatik birləşdirmək təhlükəlidir (klinik məzmun), klient iki versiyanı göstərib həkimə seçdirir.
/// Silmə qadağandır: klinik qeyd hüquqi sənəddir.
/// </summary>
internal sealed class ClinicalNoteSyncHandler(ISender sender, IClinicalRepository repository, ICurrentUser user) : ISyncEntityHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Entity => "clinical_notes";

    private sealed record NoteData(Guid? PatientId, Guid? VisitId, string? Subjective, string? Objective, string? Assessment, string? Plan, string? Source, Guid? AddendumOf);

    public async Task<SyncApplyOutcome> ApplyAsync(SyncChangeRequest change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        switch (change.Op)
        {
            case "D":
                return SyncApplyOutcome.Rejected("note.not_deletable", "Clinical notes cannot be deleted; amend them instead.");
            case "I":
                return await CreateAsync(change, cancellationToken);
            case "U":
                return await UpdateAsync(change, cancellationToken);
            default:
                return SyncApplyOutcome.Rejected("sync.invalid_op", "Op must be I, U or D.");
        }
    }

    private async Task<SyncApplyOutcome> CreateAsync(SyncChangeRequest change, CancellationToken cancellationToken)
    {
        var data = Parse(change.Data);
        if (data?.PatientId is not { } patientId)
        {
            return SyncApplyOutcome.Rejected("note.invalid_data", "patientId is required.");
        }

        var created = await sender.Send(
            new CreateNoteCommand(patientId, data.VisitId, data.Subjective, data.Objective, data.Assessment, data.Plan, data.Source, data.AddendumOf, change.EntityId), cancellationToken);
        return created.IsSuccess ? SyncApplyOutcome.Applied(created.Value.RowVersion) : Reject(created.Error!);
    }

    private async Task<SyncApplyOutcome> UpdateAsync(SyncChangeRequest change, CancellationToken cancellationToken)
    {
        var data = Parse(change.Data);
        if (data is null)
        {
            return SyncApplyOutcome.Rejected("note.invalid_data", "data is required.");
        }

        if (change.BaseVersion is not { } baseVersion)
        {
            return SyncApplyOutcome.Rejected("sync.base_version_required", "baseVersion is required to update a note.");
        }

        var note = await repository.GetNoteAsync(change.EntityId, cancellationToken);
        if (note is not null && note.AuthorId == user.UserId && SameText(note, data))
        {
            // Klientin göndərdiyi mətn serverdəkinə bərabərdir: əvvəlki cəhd tətbiq olunub (cavab itib) və ya iki tərəf eyni nəticəyə gəlib
            return SyncApplyOutcome.Applied(note.RowVersion);
        }

        var edited = await sender.Send(new EditNoteCommand(change.EntityId, data.Subjective, data.Objective, data.Assessment, data.Plan, baseVersion), cancellationToken);
        if (edited.IsSuccess)
        {
            return SyncApplyOutcome.Applied(edited.Value.RowVersion);
        }

        if (edited.Error!.Type == ErrorType.PreconditionFailed && note is not null && note.AuthorId == user.UserId)
        {
            var server = new
            {
                patientId = note.PatientId, visitId = note.VisitId, authorId = note.AuthorId, subjective = note.Subjective, objective = note.Objective,
                assessment = note.Assessment, plan = note.Plan, source = note.Source, signedAt = note.SignedAt, addendumOf = note.AddendumOf, createdAt = note.CreatedAt,
            };
            return SyncApplyOutcome.Conflict(note.RowVersion, JsonSerializer.SerializeToElement(server, Json));
        }

        return Reject(edited.Error!);
    }

    private static bool SameText(Domain.ClinicalNote note, NoteData data) =>
        Norm(note.Subjective) == Norm(data.Subjective) && Norm(note.Objective) == Norm(data.Objective)
        && Norm(note.Assessment) == Norm(data.Assessment) && Norm(note.Plan) == Norm(data.Plan);

    private static string Norm(string? value) => (value ?? string.Empty).Trim();

    private static SyncApplyOutcome Reject(Error error) => SyncApplyOutcome.Rejected(error.Code, error.Message);

    private static NoteData? Parse(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        try
        {
            return element.Deserialize<NoteData>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
