namespace DentaCore.Scheduling.Contracts;

/// <summary>Contracts qatı heç bir layihəyə istinad etmir, ona görə sadə nəticə tipi.</summary>
/// <param name="BranchId">Qəbulun filialı (vizitin hansı filialda keçdiyini bilmək üçün).</param>
public sealed record LifecycleOutcome(bool Succeeded, string? ErrorCode = null, string? Message = null, Guid? BranchId = null)
{
    public static LifecycleOutcome Ok { get; } = new(true);

    public static LifecycleOutcome Fail(string code, string message) => new(false, code, message);
}

/// <summary>
/// Vizit qəbulun statusunu dəyişir: vizit başlayanda <c>in_progress</c>, bağlananda <c>completed</c>.
/// Clinical modulu Scheduling-in daxilini bilmir, yalnız bu kontraktı çağırır.
/// </summary>
public interface IAppointmentLifecycle
{
    /// <summary>Qəbulun pasiyentə və həkimə aid olduğunu yoxlayır və <c>in_progress</c> edir.</summary>
    Task<LifecycleOutcome> StartAsync(Guid appointmentId, Guid providerId, Guid patientId, CancellationToken cancellationToken);

    Task<LifecycleOutcome> CompleteAsync(Guid appointmentId, CancellationToken cancellationToken);
}
