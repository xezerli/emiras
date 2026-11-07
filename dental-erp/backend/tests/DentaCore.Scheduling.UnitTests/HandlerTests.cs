using System.Text.Json;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Scheduling.Application;
using DentaCore.Scheduling.Domain;

namespace DentaCore.Scheduling.UnitTests;

public class BookHandlerTests
{
    private readonly FakeRepo _repo = new();
    private readonly FakeRead _read = new();
    private readonly FakePatients _patients = new();
    private readonly FakeUow _uow = new();
    private readonly FakeClock _clock = new(Env.Now);
    private readonly DentaCore.Patient.Contracts.PatientRef _patient = Env.Patient();

    public BookHandlerTests() => _patients.Items[_patient.Id] = _patient;

    private BookAppointmentCommandHandler Handler(CurrentUser? user = null) =>
        new(_repo, _read, _patients, user ?? Env.User(), _uow, Env.Opts(), _clock);

    private BookAppointmentCommand Cmd(int h = 14, int m = 0, int minutes = 30, Guid? room = null, int[]? reminders = null) =>
        new(Env.BranchA, _patient.Id, Env.Provider, room, Env.LocalToday(h, m), Env.LocalToday(h, m).AddMinutes(minutes), "Ağrı", null, reminders);

    [Fact]
    public async Task Books_the_appointment_plans_reminders_and_returns_the_patient_name()
    {
        var result = await Handler().Handle(Cmd(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Qasımova Aysel", result.Value.PatientName);
        Assert.Equal("booked", result.Value.Status);
        Assert.Single(_repo.Appointments);
        // Default 24 saat və 2 saat əvvəl; qəbul sabah 14:00, indi bu gün 10:00 → hər ikisi gələcəkdədir
        Assert.Equal(2, _repo.Reminders.Count);
        Assert.All(_repo.Reminders, r => Assert.Equal("sms", r.Channel));
        Assert.Equal(1, _uow.Saves);
    }

    [Fact]
    public async Task Patient_who_opted_out_of_messages_gets_no_reminders()
    {
        _patients.Items[_patient.Id] = Env.Patient("none") with { Id = _patient.Id };

        await Handler().Handle(Cmd(), CancellationToken.None);

        Assert.Empty(_repo.Reminders);
    }

    [Fact]
    public async Task Custom_reminder_minutes_override_the_defaults()
    {
        await Handler().Handle(Cmd(reminders: [30]), CancellationToken.None);

        Assert.Single(_repo.Reminders);
    }

    [Fact]
    public async Task Unknown_patient_is_not_found_and_nothing_is_saved()
    {
        var result = await Handler().Handle(Cmd() with { PatientId = Guid.NewGuid() }, CancellationToken.None);

        Assert.Equal(ErrorType.NotFound, result.Error!.Type);
        Assert.Equal(0, _uow.Saves);
    }

    [Fact]
    public async Task Branch_scope_blocks_booking_in_another_branch_and_own_scope_blocks_other_providers()
    {
        var branchUser = Env.User(perms: ["appointment:write@branch"], branches: [Env.BranchB.ToString()]);
        var doctor = Env.User(perms: ["appointment:write@own"]);

        Assert.Equal(ErrorType.Forbidden, (await Handler(branchUser).Handle(Cmd(), CancellationToken.None)).Error!.Type);
        Assert.Equal(ErrorType.Forbidden, (await Handler(doctor).Handle(Cmd(), CancellationToken.None)).Error!.Type);   // başqa həkimin təqvimi
        Assert.True((await Handler(Env.User(Env.Provider, ["appointment:write@own"])).Handle(Cmd(), CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Outside_working_hours_and_time_off_are_rejected()
    {
        _read.Day = new ProviderDay(true, [new WorkWindow(new TimeOnly(9, 0), new TimeOnly(13, 0))], [], []);
        Assert.Equal("appointment.outside_schedule", (await Handler().Handle(Cmd(14), CancellationToken.None)).Error!.Code);

        _read.Day = new ProviderDay(true, [new WorkWindow(new TimeOnly(9, 0), new TimeOnly(18, 0))], [new TimeSlot(Env.LocalToday(13), Env.LocalToday(15))], []);
        Assert.Equal("appointment.provider_off", (await Handler().Handle(Cmd(14), CancellationToken.None)).Error!.Code);
        Assert.Empty(_repo.Appointments);
    }

    [Fact]
    public async Task Room_must_exist_and_belong_to_the_same_branch()
    {
        var room = Guid.NewGuid();

        Assert.Equal("appointment.room_not_found", (await Handler().Handle(Cmd(room: room), CancellationToken.None)).Error!.Code);

        _read.RoomBranches[room] = Env.BranchB;
        Assert.Equal("appointment.room_branch_mismatch", (await Handler().Handle(Cmd(room: room), CancellationToken.None)).Error!.Code);

        _read.RoomBranches[room] = Env.BranchA;
        Assert.True((await Handler().Handle(Cmd(room: room), CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Database_overlap_becomes_409_with_alternative_slots()
    {
        _read.Day = new ProviderDay(true, [new WorkWindow(new TimeOnly(9, 0), new TimeOnly(18, 0))], [], [new TimeSlot(Env.LocalToday(14), Env.LocalToday(14, 30))]);
        _uow.ThrowOnSave = new ConstraintViolationException(ConstraintKind.Exclusion, "ex_provider_no_overlap", new InvalidOperationException());

        var result = await Handler().Handle(Cmd(14), CancellationToken.None);

        Assert.Equal("appointment.overlap", result.Error!.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        var alternatives = Assert.IsType<List<SlotDto>>(result.Error.Details!["alternativeSlots"]);
        Assert.Equal(3, alternatives.Count);
        Assert.Equal(Env.LocalToday(14, 30), alternatives[0].Start);   // məşğul slotdan dərhal sonra
    }

    [Fact]
    public async Task Room_overlap_foreign_keys_and_unknown_constraints_are_mapped_or_rethrown()
    {
        _uow.ThrowOnSave = new ConstraintViolationException(ConstraintKind.Exclusion, "ex_room_no_overlap", new InvalidOperationException());
        Assert.Equal("appointment.room_busy", (await Handler().Handle(Cmd(), CancellationToken.None)).Error!.Code);

        _uow.ThrowOnSave = new ConstraintViolationException(ConstraintKind.ForeignKey, "appointments_provider_id_fkey", new InvalidOperationException());
        Assert.Equal("appointment.provider_not_found", (await Handler().Handle(Cmd(), CancellationToken.None)).Error!.Code);

        _uow.ThrowOnSave = new ConstraintViolationException(ConstraintKind.Unique, "something_else", new InvalidOperationException());
        await Assert.ThrowsAsync<ConstraintViolationException>(() => Handler().Handle(Cmd(), CancellationToken.None));
    }

    [Fact]
    public void Validator_limits_reminders()
    {
        var validator = new BookAppointmentCommandValidator();

        Assert.True(validator.Validate(Cmd(reminders: [30, 60])).IsValid);
        Assert.False(validator.Validate(Cmd(reminders: [1])).IsValid);
        Assert.False(validator.Validate(Cmd(reminders: [10, 20, 30, 40, 50, 60])).IsValid);
        Assert.False(validator.Validate(Cmd() with { PatientId = Guid.Empty }).IsValid);
    }
}

public class RescheduleAndTransitionTests
{
    private readonly FakeRepo _repo = new();
    private readonly FakeRead _read = new();
    private readonly FakePatients _patients = new();
    private readonly FakeUow _uow = new();
    private readonly FakeClock _clock = new(Env.Now);
    private readonly Appointment _appointment;

    public RescheduleAndTransitionTests()
    {
        var patient = Env.Patient();
        _patients.Items[patient.Id] = patient;
        _appointment = Appointment.Book(Guid.NewGuid(), Env.BranchA, patient.Id, Env.Provider, null, new TimeSlot(Env.LocalToday(14), Env.LocalToday(14, 30)), null, "reception", Guid.NewGuid(), Env.Now).Value;
        _repo.Appointments.Add(_appointment);
    }

    private RescheduleAppointmentCommandHandler Reschedule(CurrentUser? user = null) =>
        new(_repo, _read, _patients, user ?? Env.User(), _uow, Env.Opts(), _clock);

    private static Dictionary<string, JsonElement> Fields(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public async Task Moving_only_the_start_keeps_the_duration_and_replaces_reminders_in_one_transaction()
    {
        var newStart = Env.LocalToday(16);

        var result = await Reschedule().Handle(new RescheduleAppointmentCommand(_appointment.Id, _appointment.RowVersion, Fields($$"""{"start":"{{newStart:O}}"}""")), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(newStart, _appointment.Slot.Start);
        Assert.Equal(TimeSpan.FromMinutes(30), _appointment.Slot.Duration);
        Assert.Contains(_appointment.Id, _repo.RemindersCancelledFor);
        Assert.NotEmpty(_repo.Reminders);
        Assert.Equal(1, _uow.Commits);
    }

    [Fact]
    public async Task Stale_version_unknown_field_and_bad_values_are_rejected()
    {
        Assert.Equal(ErrorType.PreconditionFailed, (await Reschedule().Handle(new RescheduleAppointmentCommand(_appointment.Id, 9, Fields("""{"reason":"x"}""")), CancellationToken.None)).Error!.Type);
        Assert.Equal("patch.unknown_field", (await Reschedule().Handle(new RescheduleAppointmentCommand(_appointment.Id, 0, Fields("""{"status":"completed"}""")), CancellationToken.None)).Error!.Code);
        Assert.Equal("patch.invalid_value", (await Reschedule().Handle(new RescheduleAppointmentCommand(_appointment.Id, 0, Fields("""{"start":"yesterday-ish"}""")), CancellationToken.None)).Error!.Code);
        Assert.Equal("patch.invalid_value", (await Reschedule().Handle(new RescheduleAppointmentCommand(_appointment.Id, 0, Fields("""{"providerId":5}""")), CancellationToken.None)).Error!.Code);
    }

    [Fact]
    public async Task Moving_to_another_providers_calendar_needs_scope_over_that_provider()
    {
        var doctor = Env.User(Env.Provider, ["appointment:write@own"]);
        var other = Guid.NewGuid();

        var result = await Reschedule(doctor).Handle(new RescheduleAppointmentCommand(_appointment.Id, 0, Fields($$"""{"providerId":"{{other}}"}""")), CancellationToken.None);

        Assert.Equal(ErrorType.Forbidden, result.Error!.Type);
    }

    [Fact]
    public async Task Overlap_during_reschedule_is_a_conflict_not_an_exception()
    {
        _uow.ThrowOnSave = new ConstraintViolationException(ConstraintKind.Exclusion, "ex_provider_no_overlap", new InvalidOperationException());

        var result = await Reschedule().Handle(new RescheduleAppointmentCommand(_appointment.Id, 0, Fields($$"""{"start":"{{Env.LocalToday(15):O}}"}""")), CancellationToken.None);

        Assert.Equal("appointment.overlap", result.Error!.Code);
        Assert.Equal(0, _uow.Commits);
    }

    [Fact]
    public async Task Cancel_and_no_show_cancel_pending_reminders_atomically()
    {
        var cancel = new CancelAppointmentCommandHandler(_repo, _patients, Env.User(), _uow, _clock);

        var result = await cancel.Handle(new CancelAppointmentCommand(_appointment.Id, "gəlmir"), CancellationToken.None);

        Assert.Equal("cancelled", result.Value.Status);
        Assert.Contains(_appointment.Id, _repo.RemindersCancelledFor);
        Assert.Equal(1, _uow.Commits);
        Assert.Equal("appointment.invalid_state", (await cancel.Handle(new CancelAppointmentCommand(_appointment.Id, null), CancellationToken.None)).Error!.Code);
    }

    [Fact]
    public async Task No_show_handler_enforces_the_start_time()
    {
        var handler = new MarkNoShowCommandHandler(_repo, _patients, Env.User(), _uow, _clock);

        Assert.Equal("appointment.too_early", (await handler.Handle(new MarkNoShowCommand(_appointment.Id), CancellationToken.None)).Error!.Code);

        _clock.UtcNow = Env.LocalToday(14, 5);
        Assert.Equal("no_show", (await handler.Handle(new MarkNoShowCommand(_appointment.Id), CancellationToken.None)).Value.Status);
    }

    [Fact]
    public async Task Appointment_outside_the_users_scope_is_not_found_for_every_action()
    {
        var outsider = Env.User(perms: ["appointment:write@branch"], branches: [Env.BranchB.ToString()]);

        Assert.Equal(ErrorType.NotFound, (await Reschedule(outsider).Handle(new RescheduleAppointmentCommand(_appointment.Id, 0, Fields("""{"reason":"x"}""")), CancellationToken.None)).Error!.Type);
        Assert.Equal(ErrorType.NotFound, (await new CancelAppointmentCommandHandler(_repo, _patients, outsider, _uow, _clock).Handle(new CancelAppointmentCommand(_appointment.Id, null), CancellationToken.None)).Error!.Type);
        Assert.Equal(ErrorType.NotFound, (await new GetAppointmentQueryHandler(_repo, _patients, Env.User(perms: ["appointment:read@branch"], branches: [Env.BranchB.ToString()])).Handle(new GetAppointmentQuery(_appointment.Id), CancellationToken.None)).Error!.Type);
    }
}

public class CheckInAndQueueTests
{
    private readonly FakeRepo _repo = new();
    private readonly FakeRead _read = new();
    private readonly FakePatients _patients = new();
    private readonly FakeUow _uow = new();
    private readonly FakeClock _clock = new(Env.LocalToday(13, 45));
    private readonly Appointment _appointment;

    public CheckInAndQueueTests()
    {
        var patient = Env.Patient();
        _patients.Items[patient.Id] = patient;
        _appointment = Appointment.Book(Guid.NewGuid(), Env.BranchA, patient.Id, Env.Provider, null, new TimeSlot(Env.LocalToday(14), Env.LocalToday(14, 30)), null, "reception", Guid.NewGuid(), Env.Now).Value;
        _repo.Appointments.Add(_appointment);
    }

    [Fact]
    public async Task Check_in_issues_the_next_ticket_number_in_a_transaction()
    {
        _repo.NextTicket = 7;
        var handler = new CheckInCommandHandler(_repo, _patients, Env.User(), _uow, Env.Opts(), _clock);

        var result = await handler.Handle(new CheckInCommand(_appointment.Id), CancellationToken.None);

        Assert.Equal(7, result.Value.TicketNo);
        Assert.Equal("waiting", result.Value.Status);
        Assert.Equal("Qasımova Aysel", result.Value.PatientName);
        Assert.Equal(AppointmentStatus.CheckedIn, _appointment.Status);
        Assert.Equal(1, _uow.Commits);
        Assert.Equal(new DateOnly(2026, 10, 8), _repo.Tickets[0].QueueDate);
    }

    [Fact]
    public async Task Check_in_too_early_does_not_consume_a_ticket_number()
    {
        _clock.UtcNow = Env.LocalToday(8);
        var handler = new CheckInCommandHandler(_repo, _patients, Env.User(), _uow, Env.Opts(), _clock);

        var result = await handler.Handle(new CheckInCommand(_appointment.Id), CancellationToken.None);

        Assert.Equal("appointment.checkin_window", result.Error!.Code);
        Assert.Empty(_repo.Tickets);
        Assert.Equal(1, _repo.NextTicket);
    }

    [Fact]
    public async Task Calling_a_ticket_is_scoped_and_one_shot()
    {
        var ticket = QueueTicket.Issue(Guid.NewGuid(), Env.BranchA, _appointment.Id, _appointment.PatientId, 1, new DateOnly(2026, 10, 8), _clock.UtcNow);
        _repo.Tickets.Add(ticket);
        var allowed = new CallTicketCommandHandler(_repo, _patients, Env.User(), _clock);
        var outsider = new CallTicketCommandHandler(_repo, _patients, Env.User(perms: ["appointment:write@branch"], branches: [Env.BranchB.ToString()]), _clock);

        Assert.Equal(ErrorType.NotFound, (await outsider.Handle(new CallTicketCommand(ticket.Id), CancellationToken.None)).Error!.Type);
        Assert.Equal("called", (await allowed.Handle(new CallTicketCommand(ticket.Id), CancellationToken.None)).Value.Status);
        Assert.Equal("queue.invalid_state", (await allowed.Handle(new CallTicketCommand(ticket.Id), CancellationToken.None)).Error!.Code);
    }

    [Fact]
    public async Task Queue_requires_branch_or_tenant_scope_not_own()
    {
        var own = new GetQueueQueryHandler(_read, _patients, Env.User(perms: ["appointment:read@own"]), Env.Opts(), _clock);
        var branch = new GetQueueQueryHandler(_read, _patients, Env.User(perms: ["appointment:read@branch"], branches: [Env.BranchA.ToString()]), Env.Opts(), _clock);
        _read.Queue.Add(new QueueRow(Guid.NewGuid(), 1, _appointment.PatientId, "waiting", _clock.UtcNow.AddMinutes(-12), _appointment.Id));

        Assert.Equal(ErrorType.NotFound, (await own.Handle(new GetQueueQuery(Env.BranchA), CancellationToken.None)).Error!.Type);
        var page = await branch.Handle(new GetQueueQuery(Env.BranchA), CancellationToken.None);
        Assert.Equal(12, Assert.Single(page.Value).WaitingMinutes);
        Assert.Equal(ErrorType.NotFound, (await branch.Handle(new GetQueueQuery(Env.BranchB), CancellationToken.None)).Error!.Type);
    }
}

public class ListAndAvailabilityTests
{
    private readonly FakeRead _read = new();
    private readonly FakePatients _patients = new();

    [Fact]
    public async Task Scope_becomes_query_filters()
    {
        var me = Guid.NewGuid();
        var from = Env.LocalToday(0);

        await new ListAppointmentsQueryHandler(_read, _patients, Env.User(perms: ["appointment:read@tenant"])).Handle(new ListAppointmentsQuery(from, from.AddDays(1), null, null, null, null), CancellationToken.None);
        Assert.Null(_read.LastQuery!.AllowedBranchIds);
        Assert.Null(_read.LastQuery.OwnProviderId);

        await new ListAppointmentsQueryHandler(_read, _patients, Env.User(perms: ["appointment:read@branch"], branches: [Env.BranchA.ToString()])).Handle(new ListAppointmentsQuery(from, from.AddDays(1), null, null, null, null), CancellationToken.None);
        Assert.Equal([Env.BranchA], _read.LastQuery!.AllowedBranchIds);

        await new ListAppointmentsQueryHandler(_read, _patients, Env.User(me, ["appointment:read@own"])).Handle(new ListAppointmentsQuery(from, from.AddDays(1), null, null, null, null), CancellationToken.None);
        Assert.Equal(me, _read.LastQuery!.OwnProviderId);
    }

    [Fact]
    public async Task User_with_no_branches_gets_an_empty_list_without_hitting_the_database()
    {
        var from = Env.LocalToday(0);
        var user = new CurrentUser(Guid.NewGuid(), ["appointment:read@branch"], [], null);

        var result = await new ListAppointmentsQueryHandler(_read, _patients, user).Handle(new ListAppointmentsQuery(from, from.AddDays(1), null, null, null, null), CancellationToken.None);

        Assert.Empty(result.Value);
        Assert.Null(_read.LastQuery);
    }

    [Theory]
    [InlineData(0, 1, false)]      // to == from
    [InlineData(0, 63, false)]     // 62 gündən çox
    [InlineData(0, 31, true)]
    public void Range_is_validated(int fromDays, int toDays, bool valid)
    {
        var from = Env.LocalToday(0).AddDays(fromDays);
        var to = toDays == 1 && fromDays == 0 ? from : from.AddDays(toDays);

        Assert.Equal(valid, new ListAppointmentsQueryValidator().Validate(new ListAppointmentsQuery(from, to, null, null, null, null)).IsValid);
        Assert.False(new ListAppointmentsQueryValidator().Validate(new ListAppointmentsQuery(from, from.AddDays(1), null, null, null, ["flying"])).IsValid);
    }

    [Fact]
    public async Task Availability_uses_the_clinic_zone_step_and_current_time()
    {
        _read.Day = new ProviderDay(true, [new WorkWindow(new TimeOnly(9, 0), new TimeOnly(10, 0))], [], []);
        var handler = new GetAvailabilityQueryHandler(_read, Env.Opts(), new FakeClock(Env.LocalToday(9, 20)));

        var result = await handler.Handle(new GetAvailabilityQuery(Guid.NewGuid(), new DateOnly(2026, 10, 8), 30), CancellationToken.None);

        Assert.Equal([Env.LocalToday(9, 30)], result.Value.Select(s => s.Start));   // 9:00 və 9:15 keçib, 9:45+30 pəncərəni aşır
    }

    [Fact]
    public void Availability_duration_is_validated()
    {
        var validator = new GetAvailabilityQueryValidator();

        Assert.False(validator.Validate(new GetAvailabilityQuery(Guid.NewGuid(), new DateOnly(2026, 10, 8), 4)).IsValid);
        Assert.False(validator.Validate(new GetAvailabilityQuery(Guid.NewGuid(), new DateOnly(2026, 10, 8), 481)).IsValid);
        Assert.True(validator.Validate(new GetAvailabilityQuery(Guid.NewGuid(), new DateOnly(2026, 10, 8), 45)).IsValid);
    }
}
