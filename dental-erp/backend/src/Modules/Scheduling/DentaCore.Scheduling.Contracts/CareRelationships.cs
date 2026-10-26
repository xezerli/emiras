namespace DentaCore.Scheduling.Contracts;

/// <summary>
/// "Baxan həkim" əlaqəsi: həkimin pasiyentlə (ləğv olunmamış) qəbulu varsa, öz pasiyentidir.
/// Patient/Clinical modulları <c>own</c> scope-u bu əlaqə ilə həll edir.
/// </summary>
public interface ICareRelationships
{
    Task<bool> HasAsync(Guid providerId, Guid patientId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<Guid>> PatientIdsAsync(Guid providerId, CancellationToken cancellationToken);
}
