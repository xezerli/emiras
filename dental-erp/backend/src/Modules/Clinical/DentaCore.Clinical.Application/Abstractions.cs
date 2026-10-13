using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Clinical.Domain;
using DentaCore.Patient.Contracts;
using DentaCore.Scheduling.Contracts;

namespace DentaCore.Clinical.Application;

public static class ClinicalPermissions
{
    public const string Read = "clinical:read";
    public const string Write = "clinical:write";
    public const string Prescribe = "prescription:write";
}

public interface IClinicalUnitOfWork : IModuleUnitOfWork
{
}

public sealed record ProcedureCodeDto(string Code, string Name, string? Category, bool RequiresTooth, int DefaultDurationMin);

public interface IClinicalRepository
{
    // Vizit
    void Add(Visit visit);

    Task<Visit?> GetVisitAsync(Guid id, CancellationToken cancellationToken);

    Task<Visit?> FindOpenVisitAsync(Guid patientId, Guid providerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Visit>> ListVisitsAsync(Guid patientId, CancellationToken cancellationToken);

    // Odontoqram
    void Add(ToothRecord record);

    /// <summary>
    /// Cari qeydi əvəz edir (superseded_at). Pasiyent üzrə advisory kilid götürür: eyni dişə paralel iki qeyd ardıcıllaşır.
    /// Surface null isə dişin bütün aktual qeydləri (səthlər daxil) əvəz olunur. Çağıran tranzaksiya açmalıdır.
    /// </summary>
    Task SupersedeAsync(Guid patientId, int toothFdi, char? surface, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>at verilibsə həmin andakı vəziyyət (time travel), əks halda aktual.</summary>
    Task<IReadOnlyList<ToothRecord>> GetOdontogramAsync(Guid patientId, DateTimeOffset? at, CancellationToken cancellationToken);

    Task<IReadOnlyList<ToothRecord>> GetToothHistoryAsync(Guid patientId, int toothFdi, CancellationToken cancellationToken);

    // Plan
    void Add(TreatmentPlan plan);

    Task<TreatmentPlan?> GetPlanAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<TreatmentPlan>> ListPlansAsync(Guid patientId, CancellationToken cancellationToken);

    // Resept
    void Add(Prescription prescription);

    Task<IReadOnlyList<Prescription>> ListPrescriptionsAsync(Guid patientId, CancellationToken cancellationToken);

    // Qeyd
    void Add(ClinicalNote note);

    Task<ClinicalNote?> GetNoteAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ClinicalNote>> ListNotesAsync(Guid patientId, CancellationToken cancellationToken);

    // Kataloq
    Task<IReadOnlyList<ProcedureCodeDto>> ListProceduresAsync(string? query, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, ProcedureCodeDto>> GetProceduresAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken);
}

/// <summary>
/// Klinik məlumata giriş qərarı: icazə + pasiyentin scope-u (tenant/filial/own). own scope-lu həkim üçün "öz pasiyenti":
/// özünün qeydiyyata aldığı və ya onunla (ləğv olunmamış) qəbulu olan. Scope xaricində "tapılmadı" qaytarılır.
/// </summary>
public sealed class ClinicalAccess(ICurrentUser user, ICareRelationships care, IPatientDirectory patients)
{
    public async Task<Result<PatientRef>> RequireAsync(Guid patientId, string permission, CancellationToken cancellationToken)
    {
        var patient = await patients.FindAsync(patientId, cancellationToken);
        if (patient is null)
        {
            return Error.NotFound("patient.not_found", "Patient not found.");
        }

        var allowed = user.CanAccess(permission, patient.BranchId, patient.CreatedBy)
            || (user.ScopeOf(permission) == PermissionScope.Own && await care.HasAsync(user.UserId, patient.Id, cancellationToken));
        return allowed ? patient : Error.NotFound("patient.not_found", "Patient not found.");
    }
}

internal static class ClinicalText
{
    public static string Condition(ToothCondition c) => c switch
    {
        ToothCondition.RootCanal => "root_canal",
        ToothCondition.PeriapicalLesion => "periapical_lesion",
        _ => c.ToString().ToLowerInvariant(),
    };

    public static bool TryParseCondition(string value, out ToothCondition condition) =>
        Enum.TryParse(value.Replace("_", string.Empty, StringComparison.Ordinal), true, out condition) && Enum.IsDefined(condition);

    public static string PlanStatus(PlanStatus s) => s == Domain.PlanStatus.InProgress ? "in_progress" : s.ToString().ToLowerInvariant();

    public static System.Text.Json.JsonSerializerOptions Json { get; } = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static string ToJson(object value) => System.Text.Json.JsonSerializer.Serialize(value, Json);
}

/// <summary>Vizitə bağlanan qeyd/resept/prosedur üçün ortaq yoxlama: vizit mövcuddur, açıqdır və həmin pasiyentə aiddir.</summary>
internal static class VisitGuard
{
    public static async Task<Result<Visit>> RequireOpenAsync(IClinicalRepository repository, Guid visitId, Guid patientId, CancellationToken cancellationToken)
    {
        var visit = await repository.GetVisitAsync(visitId, cancellationToken);
        if (visit is null || visit.PatientId != patientId)
        {
            return Error.Validation("visit.not_found", "The visit does not exist for this patient.");
        }

        return visit.Status == VisitStatus.Open ? visit : Error.Conflict("visit.already_closed", "The visit is already closed.");
    }
}
