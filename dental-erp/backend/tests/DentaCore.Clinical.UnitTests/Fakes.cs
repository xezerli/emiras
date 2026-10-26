using DentaCore.BuildingBlocks.Application;
using DentaCore.Clinical.Application;
using DentaCore.Clinical.Domain;
using DentaCore.Patient.Contracts;
using DentaCore.Scheduling.Contracts;

namespace DentaCore.Clinical.UnitTests;

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
}

internal sealed class FakeUow : IClinicalUnitOfWork
{
    public System.Reflection.Assembly ApplicationAssembly => typeof(IClinicalUnitOfWork).Assembly;

    public int Saves { get; private set; }

    public int Commits { get; private set; }

    public ConstraintViolationException? ThrowOnSave { get; set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Saves++;
        return ThrowOnSave is { } ex ? throw ex : Task.FromResult(0);
    }

    public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IUnitOfWorkTransaction>(new Tx(this));

    private sealed class Tx(FakeUow owner) : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            owner.Commits++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FakeRepo : IClinicalRepository
{
    public List<Visit> Visits { get; } = [];

    public List<ToothRecord> Teeth { get; } = [];

    public List<TreatmentPlan> Plans { get; } = [];

    public List<Prescription> Prescriptions { get; } = [];

    public List<ClinicalNote> Notes { get; } = [];

    public List<(int Tooth, char? Surface)> Superseded { get; } = [];

    public Dictionary<string, ProcedureCodeDto> Catalog { get; } = new()
    {
        ["D2391"] = new("D2391", "Kompozit plomb", "Terapiya", true, 40),
        ["D1110"] = new("D1110", "Təmizlik", "Profilaktika", false, 45),
    };

    public void Add(Visit visit) => Visits.Add(visit);

    public void Add(ToothRecord record) => Teeth.Add(record);

    public void Add(TreatmentPlan plan) => Plans.Add(plan);

    public void Add(Prescription prescription) => Prescriptions.Add(prescription);

    public void Add(ClinicalNote note) => Notes.Add(note);

    public Task<Visit?> GetVisitAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Visits.FirstOrDefault(v => v.Id == id));

    public Task<Visit?> FindOpenVisitAsync(Guid patientId, Guid providerId, CancellationToken cancellationToken) =>
        Task.FromResult(Visits.FirstOrDefault(v => v.PatientId == patientId && v.ProviderId == providerId && v.Status == VisitStatus.Open));

    public Task<IReadOnlyList<Visit>> ListVisitsAsync(Guid patientId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Visit>>(Visits.Where(v => v.PatientId == patientId).ToList());

    public Task SupersedeAsync(Guid patientId, int toothFdi, char? surface, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Superseded.Add((toothFdi, surface));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ToothRecord>> GetOdontogramAsync(Guid patientId, DateTimeOffset? at, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ToothRecord>>(Teeth.Where(t => t.PatientId == patientId).ToList());

    public Task<IReadOnlyList<ToothRecord>> GetToothHistoryAsync(Guid patientId, int toothFdi, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ToothRecord>>(Teeth.Where(t => t.PatientId == patientId && t.ToothFdi == toothFdi).ToList());

    public Task<TreatmentPlan?> GetPlanAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Plans.FirstOrDefault(p => p.Id == id));

    public Task<IReadOnlyList<TreatmentPlan>> ListPlansAsync(Guid patientId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TreatmentPlan>>(Plans.Where(p => p.PatientId == patientId).ToList());

    public Task<IReadOnlyList<Prescription>> ListPrescriptionsAsync(Guid patientId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Prescription>>(Prescriptions.Where(p => p.PatientId == patientId).ToList());

    public Task<ClinicalNote?> GetNoteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Notes.FirstOrDefault(n => n.Id == id));

    public Task<IReadOnlyList<ClinicalNote>> ListNotesAsync(Guid patientId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ClinicalNote>>(Notes.Where(n => n.PatientId == patientId).ToList());

    public Task<IReadOnlyList<ProcedureCodeDto>> ListProceduresAsync(string? query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProcedureCodeDto>>(Catalog.Values.ToList());

    public Task<IReadOnlyDictionary<string, ProcedureCodeDto>> GetProceduresAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, ProcedureCodeDto>>(Catalog.Where(c => codes.Contains(c.Key)).ToDictionary(c => c.Key, c => c.Value));
}

internal sealed class FakePatients : IPatientDirectory
{
    public Dictionary<Guid, PatientRef> Items { get; } = [];

    public List<AllergyRef> Allergies { get; } = [];

    public Task<PatientRef?> FindAsync(Guid patientId, CancellationToken cancellationToken) => Task.FromResult(Items.GetValueOrDefault(patientId));

    public Task<IReadOnlyList<AllergyRef>> GetActiveAllergiesAsync(Guid patientId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AllergyRef>>(Allergies);

    public Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IReadOnlyCollection<Guid> patientIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(Items.Where(i => patientIds.Contains(i.Key)).ToDictionary(i => i.Key, i => i.Value.FullName));
}

internal sealed class FakeCare : ICareRelationships
{
    public HashSet<(Guid Provider, Guid Patient)> Links { get; } = [];

    public Task<bool> HasAsync(Guid providerId, Guid patientId, CancellationToken cancellationToken) => Task.FromResult(Links.Contains((providerId, patientId)));

    public Task<IReadOnlyCollection<Guid>> PatientIdsAsync(Guid providerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<Guid>>(Links.Where(l => l.Provider == providerId).Select(l => l.Patient).ToList());
}

internal sealed class FakeLifecycle : IAppointmentLifecycle
{
    public LifecycleOutcome StartResult { get; set; } = new(true, BranchId: Guid.Parse("00000000-0000-0000-0000-0000000000b1"));

    public List<Guid> Completed { get; } = [];

    public List<Guid> Started { get; } = [];

    public Task<LifecycleOutcome> StartAsync(Guid appointmentId, Guid providerId, Guid patientId, CancellationToken cancellationToken)
    {
        Started.Add(appointmentId);
        return Task.FromResult(StartResult);
    }

    public Task<LifecycleOutcome> CompleteAsync(Guid appointmentId, CancellationToken cancellationToken)
    {
        Completed.Add(appointmentId);
        return Task.FromResult(LifecycleOutcome.Ok);
    }
}

internal sealed class World
{
    public static readonly Guid Branch = Guid.NewGuid();

    public FakeRepo Repo { get; } = new();

    public FakePatients Patients { get; } = new();

    public FakeCare Care { get; } = new();

    public FakeLifecycle Lifecycle { get; } = new();

    public FakeUow Uow { get; } = new();

    public FakeClock Clock { get; } = new();

    public PatientRef Patient { get; }

    public World()
    {
        Patient = new PatientRef(Guid.NewGuid(), Branch, null, "Qasımova Aysel", "sms");
        Patients.Items[Patient.Id] = Patient;
    }

    public ClinicalAccess Access(CurrentUser user) => new(user, Care, Patients);

    public static CurrentUser Doctor(Guid? id = null, string scope = "tenant") =>
        new(id ?? Guid.NewGuid(), [$"clinical:read@{scope}", $"clinical:write@{scope}", $"prescription:write@{scope}"], ["*"], null);
}
