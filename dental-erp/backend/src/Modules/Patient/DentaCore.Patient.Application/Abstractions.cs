using DentaCore.BuildingBlocks.Application;
using DentaCore.Patient.Domain;
using DentaCore.Scheduling.Contracts;

namespace DentaCore.Patient.Application;

public static class PatientPermissions
{
    public const string Read = "patient:read";
    public const string ReadSensitive = "patient:read_sensitive";
    public const string Write = "patient:write";
    public const string ClinicalRead = "clinical:read";
    public const string ClinicalWrite = "clinical:write";
}

public interface IPatientUnitOfWork : IModuleUnitOfWork
{
}

/// <summary>Dublikat tapıldıqda: id və filial (istifadəçinin həmin pasiyenti görməyə haqqı varsa id-ni açıqlamaq üçün).</summary>
public sealed record DuplicateMatch(Guid PatientId, Guid BranchId, Guid? CreatedBy);

public interface IPatientRepository
{
    void Add(Domain.Patient patient);

    void AddAllergy(PatientAllergy allergy);

    /// <summary>Silinməmiş pasiyent (soft-delete filtrlənir).</summary>
    Task<Domain.Patient?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<DuplicateMatch?> FindDuplicateAsync(byte[]? phoneHash, byte[]? nationalIdHash, Guid? excludePatientId, CancellationToken cancellationToken);

    Task<bool> HasSevereAllergyAsync(Guid patientId, CancellationToken cancellationToken);
}

public sealed record PatientListRow(Guid Id, long ChartNo, Guid BranchId, string FirstName, string LastName, DateOnly? BirthDate, byte[]? PhoneEnc, DateTimeOffset CreatedAt);

public sealed record PatientSearchCriteria(
    string[] NameTokens,
    byte[]? PhoneHash,
    byte[]? NationalIdHash,
    long? ChartNo,
    Guid? BranchId,
    IReadOnlyCollection<Guid>? AllowedBranchIds,
    Guid? OwnerUserId,
    IReadOnlyCollection<Guid>? CarePatientIds,
    (DateTimeOffset CreatedAt, Guid Id)? After,
    int Limit)
{
    public bool HasTextFilter => NameTokens.Length > 0 || PhoneHash is not null || NationalIdHash is not null || ChartNo is not null;
}

public sealed record AllergyRow(Guid Id, string Substance, string? Reaction, string Severity);

public sealed record ConditionRow(string? Icd10Code, string Name, DateOnly? Since);

public sealed record MedicationRow(string Name, string? Dose, string? Frequency);

public sealed record MedicalProfileData(
    IReadOnlyList<AllergyRow> Allergies,
    IReadOnlyList<ConditionRow> Conditions,
    IReadOnlyList<MedicationRow> Medications,
    string? LatestAnamnesisJson);

/// <summary>Oxu modeli (CQRS query tərəfi): aggregate yüklənmədən birbaşa SQL.</summary>
public interface IPatientReadModel
{
    Task<IReadOnlyList<PatientListRow>> SearchAsync(PatientSearchCriteria criteria, CancellationToken cancellationToken);

    Task<MedicalProfileData> GetMedicalProfileAsync(Guid patientId, CancellationToken cancellationToken);
}

/// <summary>
/// Pasiyentə giriş qərarı. <c>own</c> scope-lu istifadəçi (həkim) üçün "öz pasiyenti": özünün qeydiyyata aldığı
/// VƏ YA onunla (ləğv olunmamış) qəbulu olan pasiyent (Scheduling modulunun ICareRelationships kontraktı).
/// </summary>
public sealed class PatientAccess(ICurrentUser user, ICareRelationships care)
{
    public async Task<bool> CanAsync(string permission, Domain.Patient patient, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patient);
        if (user.CanAccess(permission, patient.BranchId, patient.CreatedBy))
        {
            return true;
        }

        return user.ScopeOf(permission) == PermissionScope.Own && await care.HasAsync(user.UserId, patient.Id, cancellationToken);
    }
}
