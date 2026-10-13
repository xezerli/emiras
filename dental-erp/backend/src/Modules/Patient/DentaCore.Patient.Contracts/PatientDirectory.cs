namespace DentaCore.Patient.Contracts;

/// <summary>Başqa modulların (Scheduling, Clinical, Billing) pasiyent haqqında bilməli olduğu minimum.</summary>
public sealed record PatientRef(Guid Id, Guid BranchId, Guid? CreatedBy, string FullName, string PreferredChannel);

public sealed record AllergyRef(string Substance, string Severity);

public interface IPatientDirectory
{
    /// <summary>Pasiyentin aktiv allergiyaları (resept yoxlaması üçün).</summary>
    Task<IReadOnlyList<AllergyRef>> GetActiveAllergiesAsync(Guid patientId, CancellationToken cancellationToken);

    /// <summary>Silinməmiş pasiyent. Tapılmasa null.</summary>
    Task<PatientRef?> FindAsync(Guid patientId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IReadOnlyCollection<Guid> patientIds, CancellationToken cancellationToken);
}
