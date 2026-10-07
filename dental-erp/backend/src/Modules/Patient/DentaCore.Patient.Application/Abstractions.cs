using DentaCore.BuildingBlocks.Application;
using DentaCore.Patient.Domain;

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
