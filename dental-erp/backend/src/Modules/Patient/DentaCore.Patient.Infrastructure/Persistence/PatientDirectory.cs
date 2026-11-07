using DentaCore.BuildingBlocks.Application;
using DentaCore.Patient.Contracts;
using DentaCore.Scheduling.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DentaCore.Patient.Infrastructure.Persistence;

/// <summary>Başqa modulların (Scheduling və s.) istifadə etdiyi kontrakt. Yalnız ad və filial kimi minimum məlumat verir.</summary>
internal sealed class PatientDirectory(PatientDbContext db) : IPatientDirectory
{
    public async Task<PatientRef?> FindAsync(Guid patientId, CancellationToken cancellationToken) =>
        await db.Patients
            .Where(p => p.Id == patientId)
            .Select(p => new PatientRef(p.Id, p.BranchId, p.CreatedBy, p.LastName + " " + p.FirstName, p.PreferredChannel))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<AllergyRef>> GetActiveAllergiesAsync(Guid patientId, CancellationToken cancellationToken)
    {
        var rows = await db.Allergies
            .Where(a => a.PatientId == patientId && a.IsActive)
            .Select(a => new { a.Substance, a.Severity })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new AllergyRef(r.Substance, r.Severity.ToString().ToLowerInvariant())).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IReadOnlyCollection<Guid> patientIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patientIds);
        if (patientIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await db.Patients
            .Where(p => patientIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.LastName + " " + p.FirstName, cancellationToken);
    }
}

/// <summary>Scheduling modulu qoşulmayıbsa: heç kimin "baxan həkimi" yoxdur (own scope yalnız yaradıcıya məhdudlaşır).</summary>
internal sealed class NoCareRelationships : ICareRelationships
{
    public Task<bool> HasAsync(Guid providerId, Guid patientId, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<IReadOnlyCollection<Guid>> PatientIdsAsync(Guid providerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<Guid>>([]);
}
