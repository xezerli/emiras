using System.Globalization;
using System.Text;
using System.Text.Json;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Patient.Domain;
using DentaCore.Scheduling.Contracts;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace DentaCore.Patient.Application;

public sealed record PatientDto(
    Guid Id,
    long ChartNo,
    Guid BranchId,
    string FirstName,
    string LastName,
    string? FatherName,
    DateOnly? BirthDate,
    string? Gender,
    string? Phone,
    string? Email,
    string? NationalId,
    bool SensitiveRevealed,
    JsonElement? Address,
    string PreferredChannel,
    bool MarketingOptIn,
    string? ReferralSource,
    string Status,
    int NoShowCount,
    decimal? RiskScore,
    bool HasSevereAllergy,
    int RowVersion,
    DateTimeOffset CreatedAt);

internal static class PatientMapper
{
    public static PatientDto ToDto(Domain.Patient p, IPiiProtector pii, bool reveal, bool hasSevereAllergy)
    {
        string? Show(ProtectedValue? v, Func<string, string> mask) =>
            v is null ? null : reveal ? pii.Decrypt(v.Cipher) : mask(pii.Decrypt(v.Cipher));

        return new PatientDto(
            p.Id, p.ChartNo, p.BranchId, p.FirstName, p.LastName, p.FatherName, p.BirthDate, p.Gender?.ToString(),
            Show(p.Phone, Masking.Phone), Show(p.Email, Masking.Email), Show(p.NationalId, Masking.NationalId), reveal,
            p.AddressJson is null ? null : JsonDocument.Parse(p.AddressJson).RootElement.Clone(),
            p.PreferredChannel, p.MarketingOptIn, p.ReferralSource, p.Status, p.NoShowCount, p.RiskScore,
            hasSevereAllergy, p.RowVersion, p.CreatedAt);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ToJson(object value) => JsonSerializer.Serialize(value, Json);
}

// ---------------- Register ----------------
public sealed record RegisterPatientCommand(
    Guid BranchId,
    string FirstName,
    string LastName,
    string? FatherName,
    DateOnly? BirthDate,
    string? Gender,
    string? Phone,
    string? Email,
    string? NationalId,
    JsonElement? Address,
    string? PreferredChannel,
    bool MarketingOptIn,
    string? ReferralSource,
    bool ConfirmDuplicate = false) : ICommand<PatientDto>, IRequiresAccess, IAuditable<PatientDto>
{
    public string Permission => PatientPermissions.Write;

    public AuditDescriptor Describe(PatientDto response) => new("patient.create", "patient", response?.Id);
}

public sealed class RegisterPatientCommandValidator : AbstractValidator<RegisterPatientCommand>
{
    public RegisterPatientCommandValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.FatherName).MaximumLength(100);
        RuleFor(x => x.Gender).Must(g => g is null or "M" or "F" or "O").WithMessage("Gender must be M, F or O.");
        RuleFor(x => x.Email).EmailAddress().MaximumLength(254).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.NationalId).MaximumLength(32);
        RuleFor(x => x.Phone).MaximumLength(32);
        RuleFor(x => x.ReferralSource).MaximumLength(100);
    }
}

internal sealed class RegisterPatientCommandHandler(
    IPatientRepository patients,
    IPiiProtector pii,
    ICurrentUser user,
    IPatientUnitOfWork unitOfWork,
    IOptions<PatientOptions> options,
    IClock clock) : IRequestHandler<RegisterPatientCommand, PatientDto>
{
    public async Task<Result<PatientDto>> Handle(RegisterPatientCommand request, CancellationToken cancellationToken)
    {
        // Yalnız icazə verilən filiala qeydiyyat (own scope-da öz yaratdığı pasiyent sayılır)
        if (!user.CanAccess(PatientPermissions.Write, request.BranchId, user.UserId))
        {
            return Error.Forbidden("permission.denied", "You cannot register patients in this branch.");
        }

        var cc = options.Value.DefaultCountryCode;
        ProtectedValue? phone = null;
        if (!string.IsNullOrWhiteSpace(request.Phone))
        {
            var normalized = ContactNormalizer.Phone(request.Phone, cc);
            if (normalized.IsFailure)
            {
                return normalized.Error!;
            }

            phone = Protect.Value(pii, normalized.Value);
        }

        var email = string.IsNullOrWhiteSpace(request.Email) ? null : Protect.Value(pii, ContactNormalizer.Email(request.Email));
        var nid = string.IsNullOrWhiteSpace(request.NationalId) ? null : Protect.Value(pii, ContactNormalizer.NationalId(request.NationalId));

        if (!request.ConfirmDuplicate)
        {
            var duplicate = await patients.FindDuplicateAsync(phone?.Hash, nid?.Hash, null, cancellationToken);
            if (duplicate is not null)
            {
                return DuplicateError(duplicate, user);
            }
        }

        var patient = Domain.Patient.Register(
            Guid.NewGuid(), request.BranchId, request.FirstName, request.LastName, request.FatherName, request.BirthDate,
            request.Gender?[0], nid, phone, email, request.Address?.GetRawText(), request.PreferredChannel ?? "sms",
            request.MarketingOptIn, request.ReferralSource, user.UserId, clock.UtcNow);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        patients.Add(patient.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);   // DB identity (chart_no) və row_version məlum olsun
        return PatientMapper.ToDto(patient.Value, pii, reveal: false, hasSevereAllergy: false);
    }

    internal static Error DuplicateError(DuplicateMatch match, ICurrentUser user)
    {
        var error = Error.Conflict("patient.duplicate", "A patient with the same phone or national ID already exists.");
        // Mövcud pasiyentin id-si yalnız onu görməyə haqqı olana açıqlanır (filiallararası məlumat sızması olmasın)
        return user.CanAccess(PatientPermissions.Read, match.BranchId, match.CreatedBy)
            ? error with { Details = new Dictionary<string, object?> { ["existingPatientId"] = match.PatientId } }
            : error;
    }
}

// ---------------- Get ----------------
public sealed record GetPatientQuery(Guid Id, bool RevealSensitive) : IQuery<PatientDto>, IRequiresAccess, IAuditable<PatientDto>
{
    public string Permission => PatientPermissions.Read;

    public AuditDescriptor Describe(PatientDto response) =>
        new(RevealSensitive ? "patient.read.sensitive" : "patient.read", "patient", Id);
}

internal sealed class GetPatientQueryHandler(IPatientRepository patients, IPiiProtector pii, PatientAccess access)
    : IRequestHandler<GetPatientQuery, PatientDto>
{
    public async Task<Result<PatientDto>> Handle(GetPatientQuery request, CancellationToken cancellationToken)
    {
        var patient = await patients.GetAsync(request.Id, cancellationToken);
        // Scope xaricindəki pasiyent "tapılmadı" kimi cavablanır (mövcudluq sızmasın)
        if (patient is null || !await access.CanAsync(PatientPermissions.Read, patient, cancellationToken))
        {
            return Error.NotFound("patient.not_found", "Patient not found.");
        }

        if (request.RevealSensitive && !await access.CanAsync(PatientPermissions.ReadSensitive, patient, cancellationToken))
        {
            return Error.Forbidden("permission.denied", "You do not have permission to view sensitive data.");
        }

        var severe = await patients.HasSevereAllergyAsync(patient.Id, cancellationToken);
        return PatientMapper.ToDto(patient, pii, request.RevealSensitive, severe);
    }
}

// ---------------- Search ----------------
public sealed record PatientListItem(Guid Id, long ChartNo, Guid BranchId, string FirstName, string LastName, DateOnly? BirthDate, string? PhoneMasked);

public sealed record PatientPage(IReadOnlyList<PatientListItem> Items, string? NextCursor);

public sealed record SearchPatientsQuery(string? Text, Guid? BranchId, string? Cursor, int Limit = 25)
    : IQuery<PatientPage>, IRequiresAccess, IAuditable<PatientPage>
{
    public string Permission => PatientPermissions.Read;

    // Axtarış sətri (ad/telefon ola bilər) auditə YAZILMIR, yalnız nəticə sayı
    public AuditDescriptor Describe(PatientPage response) =>
        new("patient.search", "patient", null, PatientMapper.ToJson(new { results = response?.Items.Count ?? 0, filtered = !string.IsNullOrWhiteSpace(Text) }));
}

public sealed class SearchPatientsQueryValidator : AbstractValidator<SearchPatientsQuery>
{
    public SearchPatientsQueryValidator()
    {
        RuleFor(x => x.Text).MaximumLength(100);
        RuleFor(x => x.Limit).InclusiveBetween(1, 100);
    }
}

internal sealed class SearchPatientsQueryHandler(IPatientReadModel readModel, IPiiProtector pii, ICurrentUser user, IOptions<PatientOptions> options, ICareRelationships care)
    : IRequestHandler<SearchPatientsQuery, PatientPage>
{
    public async Task<Result<PatientPage>> Handle(SearchPatientsQuery request, CancellationToken cancellationToken)
    {
        (DateTimeOffset, Guid)? after = null;
        if (!string.IsNullOrEmpty(request.Cursor))
        {
            var decoded = Cursor.TryDecode(request.Cursor);
            if (decoded is null)
            {
                return Error.Validation("cursor.invalid", "Cursor is not valid.");
            }

            after = decoded;
        }

        var scope = user.ScopeOf(PatientPermissions.Read);
        // own scope: özünün qeydiyyata aldığı + baxdığı (qəbulu olan) pasiyentlər
        var carePatients = scope == PermissionScope.Own ? await care.PatientIdsAsync(user.UserId, cancellationToken) : null;
        var criteria = BuildCriteria(request, scope, after, carePatients);
        if (!string.IsNullOrWhiteSpace(request.Text) && !criteria.HasTextFilter)
        {
            // Mətn verilib, amma ad/telefon/FİN/kart nömrəsi kimi tanınmır (məs. "%", "_"): süzgəcsiz bütün siyahını qaytarmaq olmaz
            return new PatientPage([], null);
        }

        var rows = await readModel.SearchAsync(criteria, cancellationToken);

        var page = rows.Take(request.Limit).ToList();
        var next = rows.Count > request.Limit ? Cursor.Encode(page[^1].CreatedAt, page[^1].Id) : null;
        var items = page.Select(r => new PatientListItem(
            r.Id, r.ChartNo, r.BranchId, r.FirstName, r.LastName, r.BirthDate,
            r.PhoneEnc is null ? null : Masking.Phone(pii.Decrypt(r.PhoneEnc)))).ToList();
        return new PatientPage(items, next);
    }

    private PatientSearchCriteria BuildCriteria(SearchPatientsQuery request, PermissionScope? scope, (DateTimeOffset, Guid)? after, IReadOnlyCollection<Guid>? carePatients)
    {
        var text = request.Text?.Trim() ?? string.Empty;
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        byte[]? phoneHash = null;
        byte[]? nidHash = null;
        long? chartNo = null;
        var nameTokens = Array.Empty<string>();

        if (tokens.Length > 0)
        {
            var phone = ContactNormalizer.TryPhone(text, options.Value.DefaultCountryCode);
            if (phone is not null)
            {
                phoneHash = pii.BlindIndex(phone);
            }

            if (tokens.Length == 1 && tokens[0].All(char.IsAsciiDigit) && tokens[0].Length <= 18 && long.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out var chart))
            {
                chartNo = chart;
            }

            if (tokens.Length == 1 && tokens[0].Length is >= 6 and <= 32 && tokens[0].All(char.IsLetterOrDigit))
            {
                nidHash = pii.BlindIndex(ContactNormalizer.NationalId(tokens[0]));
            }

            // Yalnız rəqəmlərdən ibarət tokenlər ad axtarışına daxil edilmir
            nameTokens = tokens.Where(t => t.Any(char.IsLetter)).Take(4).Select(t => t.Length > 50 ? t[..50] : t).ToArray();
        }

        return new PatientSearchCriteria(
            nameTokens, phoneHash, nidHash, chartNo, request.BranchId,
            scope == PermissionScope.Own ? [] : user.BranchFilterFor(PatientPermissions.Read),
            scope == PermissionScope.Own ? user.UserId : null,
            carePatients,
            after, request.Limit + 1);
    }
}

internal static class Cursor
{
    public static string Encode(DateTimeOffset createdAt, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{createdAt.UtcTicks}|{id}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static (DateTimeOffset, Guid)? TryDecode(string cursor)
    {
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('|');
            return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && Guid.TryParse(parts[1], out var id)
                ? (new DateTimeOffset(ticks, TimeSpan.Zero), id)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

// ---------------- Update (JSON Merge Patch) ----------------
public sealed record PatientUpdateResult(PatientDto Patient, IReadOnlyList<string> ChangedFields);

public sealed record UpdatePatientCommand(Guid Id, int ExpectedRowVersion, IReadOnlyDictionary<string, JsonElement> Patch, bool ConfirmDuplicate = false)
    : ICommand<PatientUpdateResult>, IRequiresAccess, IAuditable<PatientUpdateResult>
{
    public string Permission => PatientPermissions.Write;

    // Yalnız dəyişən sahələrin adları (PHI dəyərləri yox)
    public AuditDescriptor Describe(PatientUpdateResult response) =>
        new("patient.update", "patient", Id, PatientMapper.ToJson(new { changed = response?.ChangedFields ?? [] }));
}

internal sealed class UpdatePatientCommandHandler(
    IPatientRepository patients,
    IPiiProtector pii,
    ICurrentUser user,
    IOptions<PatientOptions> options,
    IClock clock,
    IPatientUnitOfWork unitOfWork) : IRequestHandler<UpdatePatientCommand, PatientUpdateResult>
{
    public async Task<Result<PatientUpdateResult>> Handle(UpdatePatientCommand request, CancellationToken cancellationToken)
    {
        var patient = await patients.GetAsync(request.Id, cancellationToken);
        if (patient is null || !user.CanAccess(PatientPermissions.Write, patient.BranchId, patient.CreatedBy))
        {
            return Error.NotFound("patient.not_found", "Patient not found.");
        }

        if (patient.RowVersion != request.ExpectedRowVersion)
        {
            return Error.PreconditionFailed("concurrency.stale", "The patient was modified by someone else. Reload and retry.");
        }

        var patch = PatchParser.Parse(request.Patch, pii, options.Value.DefaultCountryCode);
        if (patch.IsFailure)
        {
            return patch.Error!;
        }

        var newPhone = patch.Value.Phone is { HasValue: true, Value: { } p } ? p : null;
        var newNid = patch.Value.NationalId is { HasValue: true, Value: { } n } ? n : null;
        if (!request.ConfirmDuplicate && (newPhone is not null || newNid is not null))
        {
            var duplicate = await patients.FindDuplicateAsync(newPhone?.Hash, newNid?.Hash, patient.Id, cancellationToken);
            if (duplicate is not null)
            {
                return RegisterPatientCommandHandler.DuplicateError(duplicate, user);
            }
        }

        var applied = patient.Apply(patch.Value, clock.UtcNow);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        // Saxlamadan sonra map edirik: row_version DB trigger-i ilə artır, cavabdakı ETag yeni versiya olmalıdır
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var severe = await patients.HasSevereAllergyAsync(patient.Id, cancellationToken);
        return new PatientUpdateResult(PatientMapper.ToDto(patient, pii, reveal: false, severe), applied.Value);
    }
}

internal static class PatchParser
{
    private static readonly HashSet<string> Known =
    [
        "firstName", "lastName", "fatherName", "birthDate", "gender", "phone", "email", "nationalId",
        "address", "preferredChannel", "marketingOptIn", "referralSource", "notes",
    ];

    public static Result<PatientPatch> Parse(IReadOnlyDictionary<string, JsonElement> fields, IPiiProtector pii, string countryCode)
    {
        var unknown = fields.Keys.FirstOrDefault(k => !Known.Contains(k));
        if (unknown is not null)
        {
            return Error.Validation("patch.unknown_field", $"Field '{unknown}' cannot be updated.");
        }

        var patch = new PatientPatch();
        foreach (var (key, value) in fields)
        {
            var result = Apply(patch, key, value, pii, countryCode);
            if (result.IsFailure)
            {
                return result.Error!;
            }

            patch = result.Value;
        }

        return patch;
    }

    private static Result<PatientPatch> Apply(PatientPatch patch, string key, JsonElement v, IPiiProtector pii, string cc)
    {
        var isNull = v.ValueKind == JsonValueKind.Null;
        Error Invalid(string what) => Error.Validation("patch.invalid_value", $"Field '{key}' must be {what}.");

        switch (key)
        {
            case "firstName" or "lastName" or "preferredChannel":
                if (v.ValueKind != JsonValueKind.String)
                {
                    return Invalid("a string");
                }

                return key switch
                {
                    "firstName" => patch with { FirstName = Optional.Some(v.GetString()) },
                    "lastName" => patch with { LastName = Optional.Some(v.GetString()) },
                    _ => patch with { PreferredChannel = Optional.Some(v.GetString()) },
                };

            case "fatherName" or "referralSource" or "notes":
                if (!isNull && v.ValueKind != JsonValueKind.String)
                {
                    return Invalid("a string or null");
                }

                var text = isNull ? null : v.GetString();
                return key switch
                {
                    "fatherName" => patch with { FatherName = Optional.Some<string?>(text) },
                    "referralSource" => patch with { ReferralSource = Optional.Some<string?>(text) },
                    _ => patch with { Notes = Optional.Some<string?>(text) },
                };

            case "birthDate":
                if (isNull)
                {
                    return patch with { BirthDate = Optional.Some<DateOnly?>(null) };
                }

                return v.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? patch with { BirthDate = Optional.Some<DateOnly?>(d) }
                    : Invalid("a date (yyyy-MM-dd) or null");

            case "gender":
                if (isNull)
                {
                    return patch with { Gender = Optional.Some<char?>(null) };
                }

                return v.ValueKind == JsonValueKind.String && v.GetString() is { Length: 1 } g
                    ? patch with { Gender = Optional.Some<char?>(g[0]) }
                    : Invalid("M, F, O or null");

            case "marketingOptIn":
                return v.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? patch with { MarketingOptIn = Optional.Some(v.GetBoolean()) }
                    : Invalid("a boolean");

            case "address":
                if (!isNull && v.ValueKind != JsonValueKind.Object)
                {
                    return Invalid("an object or null");
                }

                return patch with { AddressJson = Optional.Some<string?>(isNull ? null : v.GetRawText()) };

            case "phone" or "email" or "nationalId":
                if (isNull)
                {
                    return key switch
                    {
                        "phone" => patch with { Phone = Optional.Some<ProtectedValue?>(null) },
                        "email" => patch with { Email = Optional.Some<ProtectedValue?>(null) },
                        _ => patch with { NationalId = Optional.Some<ProtectedValue?>(null) },
                    };
                }

                if (v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
                {
                    return Invalid("a non-empty string or null");
                }

                var raw = v.GetString()!;
                switch (key)
                {
                    case "phone":
                        var phone = ContactNormalizer.Phone(raw, cc);
                        return phone.IsFailure ? phone.Error! : patch with { Phone = Optional.Some<ProtectedValue?>(Protect.Value(pii, phone.Value)) };
                    case "email":
                        return raw.Contains('@', StringComparison.Ordinal) && raw.Length <= 254
                            ? patch with { Email = Optional.Some<ProtectedValue?>(Protect.Value(pii, ContactNormalizer.Email(raw))) }
                            : Error.Validation("patient.invalid_email", "Email is not valid.");
                    default:
                        return raw.Length <= 32
                            ? patch with { NationalId = Optional.Some<ProtectedValue?>(Protect.Value(pii, ContactNormalizer.NationalId(raw))) }
                            : Invalid("at most 32 characters");
                }

            default:
                return Error.Validation("patch.unknown_field", $"Field '{key}' cannot be updated.");
        }
    }
}

// ---------------- Delete ----------------
public sealed record DeletePatientCommand(Guid Id) : ICommand<Unit>, IRequiresAccess, IAuditable<Unit>
{
    public string Permission => PatientPermissions.Write;

    public AuditDescriptor Describe(Unit response) => new("patient.delete", "patient", Id);
}

internal sealed class DeletePatientCommandHandler(IPatientRepository patients, ICurrentUser user, IClock clock)
    : IRequestHandler<DeletePatientCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DeletePatientCommand request, CancellationToken cancellationToken)
    {
        var patient = await patients.GetAsync(request.Id, cancellationToken);
        if (patient is null || !user.CanAccess(PatientPermissions.Write, patient.BranchId, patient.CreatedBy))
        {
            return Error.NotFound("patient.not_found", "Patient not found.");
        }

        var result = patient.SoftDelete(clock.UtcNow);
        return result.IsFailure ? result.Error! : new Unit();
    }
}

// ---------------- Allergies & medical profile ----------------
public sealed record AllergyDto(Guid Id, string Substance, string? Reaction, string Severity);

public sealed record AddAllergyCommand(Guid PatientId, string Substance, string? Reaction, string Severity)
    : ICommand<AllergyDto>, IRequiresAccess, IAuditable<AllergyDto>
{
    public string Permission => PatientPermissions.ClinicalWrite;

    // Maddənin adı sağlamlıq məlumatıdır: auditə yalnız faktı yazılır
    public AuditDescriptor Describe(AllergyDto response) => new("allergy.create", "patient", PatientId);
}

public sealed class AddAllergyCommandValidator : AbstractValidator<AddAllergyCommand>
{
    public AddAllergyCommandValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.Substance).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Reaction).MaximumLength(500);
        RuleFor(x => x.Severity).Must(s => Enum.TryParse<AllergySeverity>(s, true, out _)).WithMessage("Severity must be mild, moderate, severe or anaphylaxis.");
    }
}

internal sealed class AddAllergyCommandHandler(IPatientRepository patients, PatientAccess access) : IRequestHandler<AddAllergyCommand, AllergyDto>
{
    public async Task<Result<AllergyDto>> Handle(AddAllergyCommand request, CancellationToken cancellationToken)
    {
        var patient = await patients.GetAsync(request.PatientId, cancellationToken);
        if (patient is null || !await access.CanAsync(PatientPermissions.ClinicalWrite, patient, cancellationToken))
        {
            return Error.NotFound("patient.not_found", "Patient not found.");
        }

        var allergy = PatientAllergy.Create(Guid.NewGuid(), patient.Id, request.Substance, request.Reaction, Enum.Parse<AllergySeverity>(request.Severity, true));
        if (allergy.IsFailure)
        {
            return allergy.Error!;
        }

        patients.AddAllergy(allergy.Value);
        return new AllergyDto(allergy.Value.Id, allergy.Value.Substance, allergy.Value.Reaction, allergy.Value.Severity.ToString().ToLowerInvariant());
    }
}

public sealed record MedicalProfileDto(
    IReadOnlyList<AllergyRow> Allergies,
    IReadOnlyList<ConditionRow> Conditions,
    IReadOnlyList<MedicationRow> Medications,
    JsonElement? LatestAnamnesis);

public sealed record GetMedicalProfileQuery(Guid PatientId) : IQuery<MedicalProfileDto>, IRequiresAccess, IAuditable<MedicalProfileDto>
{
    public string Permission => PatientPermissions.ClinicalRead;

    public AuditDescriptor Describe(MedicalProfileDto response) => new("patient.medical_profile.read", "patient", PatientId);
}

internal sealed class GetMedicalProfileQueryHandler(IPatientRepository patients, IPatientReadModel readModel, PatientAccess access)
    : IRequestHandler<GetMedicalProfileQuery, MedicalProfileDto>
{
    public async Task<Result<MedicalProfileDto>> Handle(GetMedicalProfileQuery request, CancellationToken cancellationToken)
    {
        var patient = await patients.GetAsync(request.PatientId, cancellationToken);
        if (patient is null || !await access.CanAsync(PatientPermissions.ClinicalRead, patient, cancellationToken))
        {
            return Error.NotFound("patient.not_found", "Patient not found.");
        }

        var data = await readModel.GetMedicalProfileAsync(patient.Id, cancellationToken);
        return new MedicalProfileDto(
            data.Allergies, data.Conditions, data.Medications,
            data.LatestAnamnesisJson is null ? null : JsonDocument.Parse(data.LatestAnamnesisJson).RootElement.Clone());
    }
}
