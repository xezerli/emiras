using DentaCore.Clinical.Application;
using DentaCore.Clinical.Domain;
using Microsoft.EntityFrameworkCore;

namespace DentaCore.Clinical.Infrastructure.Persistence;

internal sealed class ClinicalRepository(ClinicalDbContext db) : IClinicalRepository
{
    public void Add(Visit visit) => db.Visits.Add(visit);

    public void Add(ToothRecord record) => db.ToothRecords.Add(record);

    public void Add(TreatmentPlan plan) => db.Plans.Add(plan);

    public void Add(Prescription prescription) => db.Prescriptions.Add(prescription);

    public void Add(ClinicalNote note) => db.Notes.Add(note);

    public Task<Visit?> GetVisitAsync(Guid id, CancellationToken cancellationToken) => db.Visits.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

    public Task<Visit?> FindOpenVisitAsync(Guid patientId, Guid providerId, CancellationToken cancellationToken) =>
        db.Visits.FirstOrDefaultAsync(v => v.PatientId == patientId && v.ProviderId == providerId && v.Status == VisitStatus.Open, cancellationToken);

    public async Task<IReadOnlyList<Visit>> ListVisitsAsync(Guid patientId, CancellationToken cancellationToken) =>
        await db.Visits.Where(v => v.PatientId == patientId).OrderByDescending(v => v.StartedAt).ToListAsync(cancellationToken);

    public async Task SupersedeAsync(Guid patientId, int toothFdi, char? surface, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lockKey = $":odontogram:{patientId}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended(current_schema() || {lockKey}, 0))", cancellationToken);
        var surfaceText = surface?.ToString();
        await db.Database.ExecuteSqlAsync(
            $"UPDATE tooth_records SET superseded_at = {now} WHERE patient_id = {patientId} AND tooth_fdi = {toothFdi} AND superseded_at IS NULL AND ({surfaceText}::text IS NULL OR surface = {surfaceText})",
            cancellationToken);
    }

    public async Task<IReadOnlyList<ToothRecord>> GetOdontogramAsync(Guid patientId, DateTimeOffset? at, CancellationToken cancellationToken)
    {
        var query = db.ToothRecords.Where(r => r.PatientId == patientId);
        query = at is { } moment
            ? query.Where(r => r.RecordedAt <= moment && (r.SupersededAt == null || r.SupersededAt > moment))
            : query.Where(r => r.SupersededAt == null);
        return await query.OrderBy(r => r.ToothFdi).ThenBy(r => r.RecordedAt).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ToothRecord>> GetToothHistoryAsync(Guid patientId, int toothFdi, CancellationToken cancellationToken) =>
        await db.ToothRecords.Where(r => r.PatientId == patientId && r.ToothFdi == toothFdi).OrderByDescending(r => r.RecordedAt).ToListAsync(cancellationToken);

    public Task<TreatmentPlan?> GetPlanAsync(Guid id, CancellationToken cancellationToken) =>
        db.Plans.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyList<TreatmentPlan>> ListPlansAsync(Guid patientId, CancellationToken cancellationToken) =>
        await db.Plans.Include(p => p.Items).Where(p => p.PatientId == patientId).OrderByDescending(p => EF.Property<DateTimeOffset>(p, "created_at")).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Prescription>> ListPrescriptionsAsync(Guid patientId, CancellationToken cancellationToken) =>
        await db.Prescriptions.Where(p => p.PatientId == patientId).OrderByDescending(p => p.IssuedAt).ToListAsync(cancellationToken);

    public Task<ClinicalNote?> GetNoteAsync(Guid id, CancellationToken cancellationToken) => db.Notes.FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ClinicalNote>> ListNotesAsync(Guid patientId, CancellationToken cancellationToken) =>
        await db.Notes.Where(n => n.PatientId == patientId).OrderByDescending(n => n.CreatedAt).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProcedureCodeDto>> ListProceduresAsync(string? query, CancellationToken cancellationToken)
    {
        var pattern = string.IsNullOrWhiteSpace(query) ? "%" : "%" + query.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        return await db.Database
            .SqlQuery<ProcedureCodeDto>($"SELECT code, name, category, requires_tooth, default_duration_min FROM procedure_codes WHERE is_active AND (code ILIKE {pattern} ESCAPE '\\' OR name ILIKE {pattern} ESCAPE '\\') ORDER BY code")
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, ProcedureCodeDto>> GetProceduresAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQuery<ProcedureCodeDto>($"SELECT code, name, category, requires_tooth, default_duration_min FROM procedure_codes WHERE is_active AND code = ANY({codes.ToArray()})")
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Code);
    }
}
