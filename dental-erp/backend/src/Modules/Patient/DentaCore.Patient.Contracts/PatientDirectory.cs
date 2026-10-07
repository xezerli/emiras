namespace DentaCore.Patient.Contracts;

/// <summary>Başqa modulların (Scheduling, Clinical, Billing) pasiyent haqqında bilməli olduğu minimum.</summary>
public sealed record PatientRef(Guid Id, Guid BranchId, Guid? CreatedBy, string FullName, string PreferredChannel);

public interface IPatientDirectory
{
    /// <summary>Silinməmiş pasiyent. Tapılmasa null.</summary>
    Task<PatientRef?> FindAsync(Guid patientId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IReadOnlyCollection<Guid> patientIds, CancellationToken cancellationToken);
}
