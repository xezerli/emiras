using DentaCore.Patient.Application;
using DentaCore.Patient.Domain;
using Microsoft.EntityFrameworkCore;

namespace DentaCore.Patient.Infrastructure.Persistence;

internal sealed class PatientRepository(PatientDbContext db) : IPatientRepository
{
    public void Add(Domain.Patient patient) => db.Patients.Add(patient);

    public void AddAllergy(PatientAllergy allergy) => db.Allergies.Add(allergy);

    public Task<Domain.Patient?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Patients.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<DuplicateMatch?> FindDuplicateAsync(byte[]? phoneHash, byte[]? nationalIdHash, Guid? excludePatientId, CancellationToken cancellationToken)
    {
        if (phoneHash is null && nationalIdHash is null)
        {
            return null;
        }

        return await db.Patients
            .Where(p => excludePatientId == null || p.Id != excludePatientId)
            .Where(p => (phoneHash != null && p.Phone != null && p.Phone.Hash == phoneHash)
                     || (nationalIdHash != null && p.NationalId != null && p.NationalId.Hash == nationalIdHash))
            .Select(p => new DuplicateMatch(p.Id, p.BranchId, p.CreatedBy))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<bool> HasSevereAllergyAsync(Guid patientId, CancellationToken cancellationToken) =>
        db.Allergies.AnyAsync(
            a => a.PatientId == patientId && a.IsActive && (a.Severity == AllergySeverity.Severe || a.Severity == AllergySeverity.Anaphylaxis),
            cancellationToken);
}
