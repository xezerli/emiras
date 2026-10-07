using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Clinical.Application;
using DentaCore.Clinical.Domain;
using DentaCore.Scheduling.Contracts;

namespace DentaCore.Clinical.UnitTests;

public class ClinicalAccessTests
{
    [Fact]
    public async Task Own_scope_doctor_needs_the_care_relationship_and_tenant_scope_does_not()
    {
        var w = new World();
        var doctor = Guid.NewGuid();

        var own = w.Access(World.Doctor(doctor, "own"));
        Assert.Equal(ErrorType.NotFound, (await own.RequireAsync(w.Patient.Id, "clinical:read", CancellationToken.None)).Error!.Type);

        w.Care.Links.Add((doctor, w.Patient.Id));
        Assert.True((await own.RequireAsync(w.Patient.Id, "clinical:read", CancellationToken.None)).IsSuccess);

        Assert.True((await w.Access(World.Doctor()).RequireAsync(w.Patient.Id, "clinical:read", CancellationToken.None)).IsSuccess);
        Assert.Equal("patient.not_found", (await own.RequireAsync(Guid.NewGuid(), "clinical:read", CancellationToken.None)).Error!.Code);
    }

    [Fact]
    public async Task A_permission_the_user_does_not_hold_is_not_found_not_forbidden()
    {
        var w = new World();
        var readOnly = new CurrentUser(Guid.NewGuid(), ["clinical:read@tenant"], ["*"], null);

        var result = await w.Access(readOnly).RequireAsync(w.Patient.Id, "clinical:write", CancellationToken.None);

        Assert.Equal(ErrorType.NotFound, result.Error!.Type);
    }
}

public class VisitHandlerTests
{
    private readonly World _w = new();
    private readonly Guid _doctor = Guid.NewGuid();

    private StartVisitCommandHandler Start(CurrentUser? user = null) =>
        new(_w.Repo, _w.Access(user ?? World.Doctor(_doctor)), _w.Lifecycle, user ?? World.Doctor(_doctor), _w.Uow, _w.Clock);

    [Fact]
    public async Task Walk_in_visit_uses_the_patients_branch_and_saves()
    {
        var result = await Start().Handle(new StartVisitCommand(_w.Patient.Id, null, "ağrı"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(World.Branch, result.Value.BranchId);
        Assert.Empty(_w.Lifecycle.Started);
        Assert.Equal(1, _w.Uow.Saves);
    }

    [Fact]
    public async Task Visit_from_an_appointment_takes_its_branch_and_starts_the_appointment()
    {
        var appointment = Guid.NewGuid();

        var result = await Start().Handle(new StartVisitCommand(_w.Patient.Id, appointment, null), CancellationToken.None);

        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-0000000000b1"), result.Value.BranchId);
        Assert.Equal([appointment], _w.Lifecycle.Started);
    }

    [Fact]
    public async Task Appointment_problems_become_the_right_error_type_and_no_visit_is_created()
    {
        _w.Lifecycle.StartResult = LifecycleOutcome.Fail("visit.appointment_mismatch", "nope");
        var mismatch = await Start().Handle(new StartVisitCommand(_w.Patient.Id, Guid.NewGuid(), null), CancellationToken.None);

        _w.Lifecycle.StartResult = LifecycleOutcome.Fail("appointment.invalid_state", "cancelled");
        var state = await Start().Handle(new StartVisitCommand(_w.Patient.Id, Guid.NewGuid(), null), CancellationToken.None);

        Assert.Equal(ErrorType.Validation, mismatch.Error!.Type);
        Assert.Equal(ErrorType.Conflict, state.Error!.Type);
        Assert.Empty(_w.Repo.Visits);
    }

    [Fact]
    public async Task Second_open_visit_is_a_conflict_that_returns_the_existing_id()
    {
        var first = await Start().Handle(new StartVisitCommand(_w.Patient.Id, null, null), CancellationToken.None);

        var second = await Start().Handle(new StartVisitCommand(_w.Patient.Id, null, null), CancellationToken.None);

        Assert.Equal("visit.already_open", second.Error!.Code);
        Assert.Equal(first.Value.Id, second.Error.Details!["existingVisitId"]);
    }

    [Fact]
    public async Task Database_uniqueness_race_is_mapped_to_the_same_conflict()
    {
        _w.Uow.ThrowOnSave = new ConstraintViolationException(ConstraintKind.Unique, "ux_visits_one_open", new InvalidOperationException());

        var result = await Start().Handle(new StartVisitCommand(_w.Patient.Id, null, null), CancellationToken.None);

        Assert.Equal("visit.already_open", result.Error!.Code);
    }

    [Fact]
    public async Task Unknown_patient_or_missing_permission_is_not_found()
    {
        Assert.Equal(ErrorType.NotFound, (await Start().Handle(new StartVisitCommand(Guid.NewGuid(), null, null), CancellationToken.None)).Error!.Type);
        var reception = new CurrentUser(Guid.NewGuid(), ["clinical:read@tenant"], ["*"], null);
        Assert.Equal(ErrorType.NotFound, (await Start(reception).Handle(new StartVisitCommand(_w.Patient.Id, null, null), CancellationToken.None)).Error!.Type);
    }

    [Fact]
    public async Task Only_the_owner_or_a_tenant_scoped_user_can_close_and_the_appointment_is_completed_after_saving()
    {
        var appointment = Guid.NewGuid();
        var started = await Start().Handle(new StartVisitCommand(_w.Patient.Id, appointment, null), CancellationToken.None);
        var visitId = started.Value.Id;
        var stranger = new CloseVisitCommandHandler(_w.Repo, _w.Lifecycle, World.Doctor(Guid.NewGuid(), "own"), _w.Uow, _w.Clock);
        var owner = new CloseVisitCommandHandler(_w.Repo, _w.Lifecycle, World.Doctor(_doctor), _w.Uow, _w.Clock);

        Assert.Equal(ErrorType.NotFound, (await stranger.Handle(new CloseVisitCommand(visitId), CancellationToken.None)).Error!.Type);
        Assert.Empty(_w.Lifecycle.Completed);

        var closed = await owner.Handle(new CloseVisitCommand(visitId), CancellationToken.None);

        Assert.Equal("closed", closed.Value.Status);
        Assert.Equal([appointment], _w.Lifecycle.Completed);
        Assert.Equal(2, _w.Uow.Saves);   // start + close
        Assert.Equal("visit.already_closed", (await owner.Handle(new CloseVisitCommand(visitId), CancellationToken.None)).Error!.Code);
    }

    [Fact]
    public async Task A_tenant_scoped_manager_can_close_someone_elses_visit()
    {
        var started = await Start().Handle(new StartVisitCommand(_w.Patient.Id, null, null), CancellationToken.None);
        var manager = new CloseVisitCommandHandler(_w.Repo, _w.Lifecycle, World.Doctor(Guid.NewGuid(), "tenant"), _w.Uow, _w.Clock);

        Assert.True((await manager.Handle(new CloseVisitCommand(started.Value.Id), CancellationToken.None)).IsSuccess);
    }
}

public class OdontogramHandlerTests
{
    private readonly World _w = new();
    private readonly Guid _doctor = Guid.NewGuid();

    private RecordToothCommandHandler Handler() => new(_w.Repo, _w.Access(World.Doctor(_doctor)), World.Doctor(_doctor), _w.Uow, _w.Clock);

    [Theory]
    [InlineData("caries", true)]
    [InlineData("root_canal", true)]
    [InlineData("ROOT_CANAL", true)]
    [InlineData("periapical_lesion", true)]
    [InlineData("levitating", false)]
    [InlineData("", false)]
    public void Condition_names_use_snake_case(string name, bool valid) =>
        Assert.Equal(valid, new RecordToothCommandValidator().Validate(new RecordToothCommand(Guid.NewGuid(), 16, null, name, null, null, null)).IsValid);

    [Fact]
    public async Task Recording_supersedes_the_surface_inside_a_transaction_before_inserting()
    {
        var result = await Handler().Handle(new RecordToothCommand(_w.Patient.Id, 16, "O", "filling", "kompozit", null, null), CancellationToken.None);

        Assert.Equal("filling", result.Value.Condition);
        Assert.Equal([(16, (char?)'O')], _w.Repo.Superseded);
        Assert.Equal(1, _w.Uow.Commits);
        Assert.Single(_w.Repo.Teeth);
    }

    [Fact]
    public async Task Whole_tooth_record_supersedes_without_a_surface_filter()
    {
        await Handler().Handle(new RecordToothCommand(_w.Patient.Id, 16, null, "extracted", null, null, null), CancellationToken.None);

        Assert.Equal([(16, (char?)null)], _w.Repo.Superseded);
    }

    [Fact]
    public async Task Invalid_tooth_or_closed_visit_never_reaches_the_database()
    {
        var closedVisit = Visit.Start(Guid.NewGuid(), _w.Patient.Id, _doctor, World.Branch, null, null, _w.Clock.UtcNow).Value;
        closedVisit.Close(_w.Clock.UtcNow);
        _w.Repo.Visits.Add(closedVisit);

        var badTooth = await Handler().Handle(new RecordToothCommand(_w.Patient.Id, 19, null, "caries", null, null, null), CancellationToken.None);
        var closed = await Handler().Handle(new RecordToothCommand(_w.Patient.Id, 16, null, "caries", null, null, closedVisit.Id), CancellationToken.None);
        var foreign = await Handler().Handle(new RecordToothCommand(_w.Patient.Id, 16, null, "caries", null, null, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal("odontogram.invalid_tooth", badTooth.Error!.Code);
        Assert.Equal("visit.already_closed", closed.Error!.Code);
        Assert.Equal("visit.not_found", foreign.Error!.Code);
        Assert.Empty(_w.Repo.Superseded);
    }

    [Fact]
    public async Task Unique_violation_becomes_a_retryable_conflict()
    {
        _w.Uow.ThrowOnSave = new ConstraintViolationException(ConstraintKind.Unique, "ux_tooth_current_surface", new InvalidOperationException());

        var result = await Handler().Handle(new RecordToothCommand(_w.Patient.Id, 16, "O", "caries", null, null, null), CancellationToken.None);

        Assert.Equal("odontogram.concurrent_update", result.Error!.Code);
        Assert.Equal(0, _w.Uow.Commits);
    }

    [Fact]
    public async Task Dentition_is_derived_from_the_recorded_teeth()
    {
        var read = new GetOdontogramQueryHandler(_w.Repo, _w.Access(World.Doctor(_doctor)));
        await Handler().Handle(new RecordToothCommand(_w.Patient.Id, 55, null, "caries", null, null, null), CancellationToken.None);
        Assert.Equal("primary", (await read.Handle(new GetOdontogramQuery(_w.Patient.Id, null), CancellationToken.None)).Value.Dentition);

        await Handler().Handle(new RecordToothCommand(_w.Patient.Id, 16, null, "healthy", null, null, null), CancellationToken.None);
        Assert.Equal("mixed", (await read.Handle(new GetOdontogramQuery(_w.Patient.Id, null), CancellationToken.None)).Value.Dentition);
    }
}

public class PlanHandlerTests
{
    private readonly World _w = new();
    private readonly Guid _doctor = Guid.NewGuid();

    private CreatePlanCommandHandler Create() => new(_w.Repo, _w.Access(World.Doctor(_doctor)), World.Doctor(_doctor), _w.Clock);

    [Fact]
    public async Task Catalog_rules_are_enforced_when_creating_a_plan()
    {
        var unknown = await Create().Handle(new CreatePlanCommand(_w.Patient.Id, "T", [new("X1", 16, null, null, null, 10, null)]), CancellationToken.None);
        var noTooth = await Create().Handle(new CreatePlanCommand(_w.Patient.Id, "T", [new("D2391", null, null, null, null, 10, null)]), CancellationToken.None);
        var ok = await Create().Handle(new CreatePlanCommand(_w.Patient.Id, "T", [new("D2391", 16, "O", null, 2, 100, 10), new("D1110", null, null, 2, null, 250, null)]), CancellationToken.None);

        Assert.Equal("plan.unknown_procedure", unknown.Error!.Code);
        Assert.Equal("plan.tooth_required", noTooth.Error!.Code);
        Assert.True(ok.IsSuccess);
        Assert.Equal(430m, ok.Value.Total);
        Assert.Equal(_doctor, ok.Value.ProviderId);
        Assert.Equal([1, 2], ok.Value.Items.Select(i => i.Phase));
    }

    private async Task<(PlanDto Plan, Visit Visit)> AcceptedPlanWithVisit(Guid visitOwner)
    {
        var plan = (await Create().Handle(new CreatePlanCommand(_w.Patient.Id, "T", [new("D2391", 16, null, null, null, 100, null)]), CancellationToken.None)).Value;
        var transition = new TransitionPlanCommandHandler(_w.Repo, _w.Access(World.Doctor(_doctor)), _w.Clock);
        await transition.Handle(new TransitionPlanCommand(plan.Id, PlanAction.Propose), CancellationToken.None);
        await transition.Handle(new TransitionPlanCommand(plan.Id, PlanAction.Accept), CancellationToken.None);
        var visit = Visit.Start(Guid.NewGuid(), _w.Patient.Id, visitOwner, World.Branch, null, null, _w.Clock.UtcNow).Value;
        _w.Repo.Visits.Add(visit);
        return (plan, visit);
    }

    [Fact]
    public async Task Perform_requires_the_doctors_own_open_visit()
    {
        var (plan, foreignVisit) = await AcceptedPlanWithVisit(Guid.NewGuid());
        var perform = new PerformPlanItemCommandHandler(_w.Repo, _w.Access(World.Doctor(_doctor)), World.Doctor(_doctor), _w.Clock);

        var result = await perform.Handle(new PerformPlanItemCommand(plan.Id, plan.Items[0].Id, foreignVisit.Id), CancellationToken.None);

        Assert.Equal("plan.not_your_visit", result.Error!.Code);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
        Assert.Equal(ItemStatus.Planned, _w.Repo.Plans[0].Items[0].Status);
    }

    [Fact]
    public async Task Perform_marks_the_item_done_and_closed_visits_are_refused()
    {
        var (plan, visit) = await AcceptedPlanWithVisit(_doctor);
        var perform = new PerformPlanItemCommandHandler(_w.Repo, _w.Access(World.Doctor(_doctor)), World.Doctor(_doctor), _w.Clock);

        var done = await perform.Handle(new PerformPlanItemCommand(plan.Id, plan.Items[0].Id, visit.Id), CancellationToken.None);

        Assert.Equal("completed", done.Value.Status);
        Assert.Equal("done", done.Value.Items[0].Status);
        Assert.Contains(_w.Repo.Plans[0].DomainEvents, e => e is ProcedurePerformed);

        visit.Close(_w.Clock.UtcNow);
        Assert.Equal("visit.already_closed", (await perform.Handle(new PerformPlanItemCommand(plan.Id, plan.Items[0].Id, visit.Id), CancellationToken.None)).Error!.Code);
    }

    [Fact]
    public async Task Plans_of_inaccessible_patients_are_not_found()
    {
        var (plan, _) = await AcceptedPlanWithVisit(_doctor);
        var outsider = World.Doctor(Guid.NewGuid(), "own");

        var get = await new GetPlanQueryHandler(_w.Repo, _w.Access(outsider)).Handle(new GetPlanQuery(plan.Id), CancellationToken.None);
        var cancel = await new TransitionPlanCommandHandler(_w.Repo, _w.Access(outsider), _w.Clock).Handle(new TransitionPlanCommand(plan.Id, PlanAction.Cancel), CancellationToken.None);

        Assert.Equal(ErrorType.NotFound, get.Error!.Type);
        Assert.Equal(ErrorType.NotFound, cancel.Error!.Type);
    }
}

public class NoteAndPrescriptionHandlerTests
{
    private readonly World _w = new();
    private readonly Guid _author = Guid.NewGuid();

    private CreateNoteCommandHandler Create(Guid? user = null) => new(_w.Repo, _w.Access(World.Doctor(user ?? _author)), World.Doctor(user ?? _author), _w.Clock);

    [Fact]
    public async Task Only_the_author_edits_and_signs_and_other_clinicians_see_signed_notes_only()
    {
        var created = await Create().Handle(new CreateNoteCommand(_w.Patient.Id, null, "Ağrı", null, null, null, null, null), CancellationToken.None);
        var noteId = created.Value.Id;
        var colleague = Guid.NewGuid();
        var colleagueAccess = _w.Access(World.Doctor(colleague));

        var edit = await new EditNoteCommandHandler(_w.Repo, colleagueAccess, World.Doctor(colleague)).Handle(new EditNoteCommand(noteId, "x", null, null, null), CancellationToken.None);
        var sign = await new SignNoteCommandHandler(_w.Repo, colleagueAccess, World.Doctor(colleague), _w.Clock).Handle(new SignNoteCommand(noteId), CancellationToken.None);
        var list = new ListNotesQueryHandler(_w.Repo, colleagueAccess, World.Doctor(colleague));
        var before = await list.Handle(new ListNotesQuery(_w.Patient.Id), CancellationToken.None);

        Assert.Equal("note.not_author", edit.Error!.Code);
        Assert.Equal("note.not_author", sign.Error!.Code);
        Assert.Empty(before.Value);

        await new SignNoteCommandHandler(_w.Repo, _w.Access(World.Doctor(_author)), World.Doctor(_author), _w.Clock).Handle(new SignNoteCommand(noteId), CancellationToken.None);
        Assert.Single((await list.Handle(new ListNotesQuery(_w.Patient.Id), CancellationToken.None)).Value);
    }

    [Fact]
    public async Task Addendum_needs_a_signed_original_of_the_same_patient()
    {
        var draft = (await Create().Handle(new CreateNoteCommand(_w.Patient.Id, null, "Ağrı", null, null, null, null, null), CancellationToken.None)).Value;

        var onDraft = await Create().Handle(new CreateNoteCommand(_w.Patient.Id, null, "əlavə", null, null, null, null, draft.Id), CancellationToken.None);
        var ghost = await Create().Handle(new CreateNoteCommand(_w.Patient.Id, null, "əlavə", null, null, null, null, Guid.NewGuid()), CancellationToken.None);
        _w.Repo.Notes[0].Sign(_w.Clock.UtcNow);
        var ok = await Create().Handle(new CreateNoteCommand(_w.Patient.Id, null, "əlavə", null, null, null, null, draft.Id), CancellationToken.None);

        Assert.Equal("note.original_not_signed", onDraft.Error!.Code);
        Assert.Equal("note.original_not_found", ghost.Error!.Code);
        Assert.Equal(draft.Id, ok.Value.AddendumOf);
        Assert.Equal("note.addendum", new CreateNoteCommand(_w.Patient.Id, null, "x", null, null, null, null, draft.Id).Describe(ok.Value).Action);
    }

    private IssuePrescriptionCommandHandler Issue(CurrentUser? user = null) =>
        new(_w.Repo, _w.Access(user ?? World.Doctor(_author)), _w.Patients, user ?? World.Doctor(_author), _w.Clock);

    [Fact]
    public async Task Allergy_conflict_blocks_and_lists_the_conflicts_without_saving()
    {
        _w.Patients.Allergies.Add(new("Penisillin", "severe"));

        var result = await Issue().Handle(new IssuePrescriptionCommand(_w.Patient.Id, null, [new("Amoksisillin", "500 mq", null, 7, null)], false, null), CancellationToken.None);

        Assert.Equal("prescription.allergy_conflict", result.Error!.Code);
        var conflicts = Assert.IsAssignableFrom<IReadOnlyList<AllergyConflict>>(result.Error.Details!["conflicts"]);
        Assert.Equal("Penisillin", Assert.Single(conflicts).Allergen);
        Assert.Empty(_w.Repo.Prescriptions);
    }

    [Fact]
    public async Task Override_with_a_reason_is_stored_and_audited_as_an_override()
    {
        _w.Patients.Allergies.Add(new("Penisillin", "severe"));
        var command = new IssuePrescriptionCommand(_w.Patient.Id, null, [new("Amoksisillin", "500 mq", null, null, null)], true, "Başqa seçim yoxdur, pasiyent razıdır");

        var result = await Issue().Handle(command, CancellationToken.None);

        Assert.False(result.Value.AllergyCheckPassed);
        Assert.Equal("prescription.allergy_override", command.Describe(result.Value).Action);
        Assert.DoesNotContain("Amoksisillin", command.Describe(result.Value).DetailsJson, StringComparison.Ordinal);
        Assert.Equal("prescription.override_reason_required", (await Issue().Handle(command with { OverrideReason = "yox" }, CancellationToken.None)).Error!.Code);
    }

    [Fact]
    public async Task Override_flag_without_a_conflict_changes_nothing()
    {
        var command = new IssuePrescriptionCommand(_w.Patient.Id, null, [new("Paracetamol", "500 mq", null, null, null)], true, null);

        var result = await Issue().Handle(command, CancellationToken.None);

        Assert.True(result.Value.AllergyCheckPassed);
        Assert.Null(result.Value.OverrideReason);
        Assert.Equal("prescription.issue", command.Describe(result.Value).Action);
    }

    [Fact]
    public async Task Prescribing_needs_the_prescribe_permission_on_the_patient()
    {
        var onlyClinical = new CurrentUser(Guid.NewGuid(), ["clinical:read@tenant", "clinical:write@tenant"], ["*"], null);

        var result = await Issue(onlyClinical).Handle(new IssuePrescriptionCommand(_w.Patient.Id, null, [new("Paracetamol", "500 mq", null, null, null)], false, null), CancellationToken.None);

        Assert.Equal(ErrorType.NotFound, result.Error!.Type);
    }
}
