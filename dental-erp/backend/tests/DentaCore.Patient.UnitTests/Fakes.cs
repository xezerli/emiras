using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Security;
using DentaCore.Patient.Application;
using DentaCore.Patient.Domain;
using Microsoft.Extensions.Options;

namespace DentaCore.Patient.UnitTests;

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; } = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
}

internal sealed class FakePatientUow : IPatientUnitOfWork
{
    public System.Reflection.Assembly ApplicationAssembly => typeof(IPatientUnitOfWork).Assembly;

    public int Saves { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Saves++;
        return Task.FromResult(0);
    }

    public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class FakePatients : IPatientRepository
{
    public List<Domain.Patient> Items { get; } = [];

    public List<PatientAllergy> Allergies { get; } = [];

    public bool SevereAllergy { get; set; }

    public void Add(Domain.Patient patient) => Items.Add(patient);

    public void AddAllergy(PatientAllergy allergy) => Allergies.Add(allergy);

    public Task<Domain.Patient?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(p => p.Id == id && p.DeletedAt is null));

    public Task<DuplicateMatch?> FindDuplicateAsync(byte[]? phoneHash, byte[]? nationalIdHash, Guid? excludePatientId, CancellationToken cancellationToken)
    {
        var match = Items.FirstOrDefault(p => p.Id != excludePatientId
            && ((phoneHash is not null && p.Phone is not null && p.Phone.Hash.SequenceEqual(phoneHash))
             || (nationalIdHash is not null && p.NationalId is not null && p.NationalId.Hash.SequenceEqual(nationalIdHash))));
        return Task.FromResult(match is null ? null : new DuplicateMatch(match.Id, match.BranchId, match.CreatedBy));
    }

    public Task<bool> HasSevereAllergyAsync(Guid patientId, CancellationToken cancellationToken) => Task.FromResult(SevereAllergy);
}

internal sealed class FakeReadModel : IPatientReadModel
{
    public PatientSearchCriteria? LastCriteria { get; private set; }

    public List<PatientListRow> Rows { get; } = [];

    public Task<IReadOnlyList<PatientListRow>> SearchAsync(PatientSearchCriteria criteria, CancellationToken cancellationToken)
    {
        LastCriteria = criteria;
        return Task.FromResult<IReadOnlyList<PatientListRow>>(Rows);
    }

    public Task<MedicalProfileData> GetMedicalProfileAsync(Guid patientId, CancellationToken cancellationToken) =>
        Task.FromResult(new MedicalProfileData([], [], [], null));
}

internal sealed class FakeCare : DentaCore.Scheduling.Contracts.ICareRelationships
{
    public HashSet<(Guid Provider, Guid Patient)> Links { get; } = [];

    public Task<bool> HasAsync(Guid providerId, Guid patientId, CancellationToken cancellationToken) => Task.FromResult(Links.Contains((providerId, patientId)));

    public Task<IReadOnlyCollection<Guid>> PatientIdsAsync(Guid providerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<Guid>>(Links.Where(l => l.Provider == providerId).Select(l => l.Patient).ToList());
}

internal static class Factory
{
    public static readonly Guid BranchA = Guid.NewGuid();
    public static readonly Guid BranchB = Guid.NewGuid();

    public static AesGcmPiiProtector Pii() => new(Options.Create(new PiiOptions
    {
        PiiEncryptionKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()),
        PiiHashKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 101)).ToArray()),
    }));

    public static IOptions<PatientOptions> Opts() => Options.Create(new PatientOptions());

    public static CurrentUser User(Guid id, string[] perms, string[]? branches = null) => new(id, perms, branches ?? ["*"], null);
}
