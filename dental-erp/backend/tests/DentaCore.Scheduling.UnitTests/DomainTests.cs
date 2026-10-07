using DentaCore.BuildingBlocks.Domain;
using DentaCore.Scheduling.Domain;

namespace DentaCore.Scheduling.UnitTests;

public class AppointmentDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly Guid Patient = Guid.NewGuid();
    private static readonly Guid Provider = Guid.NewGuid();

    private static TimeSlot Slot(int startMinFromNow, int minutes = 30) => new(Now.AddMinutes(startMinFromNow), Now.AddMinutes(startMinFromNow + minutes));

    private static Appointment Booked(int startMin = 60, int minutes = 30) =>
        Appointment.Book(Guid.NewGuid(), Branch, Patient, Provider, null, Slot(startMin, minutes), "Ağrı", "reception", Guid.NewGuid(), Now).Value;

    [Fact]
    public void Book_creates_booked_appointment_and_raises_event()
    {
        var a = Booked();

        Assert.Equal(AppointmentStatus.Booked, a.Status);
        Assert.True(a.HoldsSlot);
        var e = Assert.IsType<AppointmentBooked>(Assert.Single(a.DomainEvents));
        Assert.Equal(a.Slot.Start, e.Start);
    }

    [Theory]
    [InlineData(60, 0, "appointment.invalid_period")]
    [InlineData(60, -10, "appointment.invalid_period")]
    [InlineData(60, 4, "appointment.invalid_duration")]
    [InlineData(60, 481, "appointment.invalid_duration")]
    [InlineData(-6, 30, "appointment.in_the_past")]
    public void Invalid_slots_are_rejected(int startMin, int minutes, string code) =>
        Assert.Equal(code, Appointment.Book(Guid.NewGuid(), Branch, Patient, Provider, null, Slot(startMin, minutes), null, "reception", Guid.NewGuid(), Now).Error!.Code);

    [Fact]
    public void Walk_in_booked_within_the_grace_period_is_allowed() =>
        Assert.True(Appointment.Book(Guid.NewGuid(), Branch, Patient, Provider, null, Slot(-4), null, "reception", Guid.NewGuid(), Now).IsSuccess);

    [Fact]
    public void Booking_more_than_two_years_ahead_is_rejected() =>
        Assert.Equal("appointment.too_far", Appointment.Book(Guid.NewGuid(), Branch, Patient, Provider, null, Slot(60 * 24 * 800), null, "reception", Guid.NewGuid(), Now).Error!.Code);

    [Fact]
    public void Source_and_reason_are_validated()
    {
        Assert.Equal("appointment.invalid_source", Appointment.Book(Guid.NewGuid(), Branch, Patient, Provider, null, Slot(60), null, "fax", Guid.NewGuid(), Now).Error!.Code);
        Assert.Equal("appointment.reason_too_long", Appointment.Book(Guid.NewGuid(), Branch, Patient, Provider, null, Slot(60), new string('x', 501), "reception", Guid.NewGuid(), Now).Error!.Code);
    }

    [Fact]
    public void Slots_are_half_open_so_back_to_back_appointments_do_not_overlap()
    {
        var first = Slot(60);
        var second = Slot(90);

        Assert.False(first.Overlaps(second));
        Assert.True(first.Overlaps(Slot(75)));
    }

    [Fact]
    public void Reschedule_raises_event_only_when_time_changes()
    {
        var a = Booked();
        a.ClearDomainEvents();

        Assert.True(a.Reschedule(Guid.NewGuid(), null, a.Slot, "yeni", Now).IsSuccess);
        Assert.Empty(a.DomainEvents);

        Assert.True(a.Reschedule(a.ProviderId, null, Slot(120), null, Now).IsSuccess);
        Assert.IsType<AppointmentRescheduled>(Assert.Single(a.DomainEvents));
    }

    [Fact]
    public void Only_booked_or_confirmed_can_be_rescheduled()
    {
        var a = Booked(startMin: 1);
        a.CheckIn(Now);

        Assert.Equal("appointment.not_reschedulable", a.Reschedule(a.ProviderId, null, Slot(120), null, Now).Error!.Code);
    }

    [Theory]
    [InlineData(121, false)]    // 2 saatdan çox qalıb
    [InlineData(120, true)]
    [InlineData(5, true)]
    [InlineData(-29, true)]     // qəbul gedir
    [InlineData(-30, false)]    // qəbul bitib
    public void Check_in_window_is_two_hours_before_start_until_end(int startMinFromNow, bool allowed)
    {
        // Slot keçmişə düşə bildiyi üçün qəbul "daha əvvəl" yaradılmış sayılır (booking vaxtı start-dan əvvəldir)
        var bookedAt = Now.AddMinutes(Math.Min(0, startMinFromNow) - 10);
        var appointment = Appointment.Book(Guid.NewGuid(), Branch, Patient, Provider, null, Slot(startMinFromNow, 30), null, "reception", Guid.NewGuid(), bookedAt).Value;

        var result = appointment.CheckIn(Now);

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Equal("appointment.checkin_window", result.Error!.Code);
        }
    }

    [Fact]
    public void Check_in_sets_status_and_raises_event_and_cannot_repeat()
    {
        var a = Booked(30);
        a.ClearDomainEvents();

        Assert.True(a.CheckIn(Now).IsSuccess);

        Assert.Equal(AppointmentStatus.CheckedIn, a.Status);
        Assert.Equal(Now, a.CheckedInAt);
        Assert.IsType<PatientCheckedIn>(Assert.Single(a.DomainEvents));
        Assert.Equal("appointment.invalid_state", a.CheckIn(Now).Error!.Code);
    }

    [Fact]
    public void Cancel_frees_the_slot_and_is_final()
    {
        var a = Booked();

        Assert.True(a.Cancel("  pasiyent imtina etdi ", Now).IsSuccess);

        Assert.False(a.HoldsSlot);
        Assert.Equal("pasiyent imtina etdi", a.CancelReason);
        Assert.Equal("appointment.invalid_state", a.Cancel(null, Now).Error!.Code);
        Assert.Equal("appointment.invalid_state", a.MarkNoShow(Now.AddHours(5)).Error!.Code);
    }

    [Fact]
    public void No_show_is_possible_only_after_the_start()
    {
        var a = Booked(30);

        Assert.Equal("appointment.too_early", a.MarkNoShow(Now).Error!.Code);
        a.ClearDomainEvents();
        Assert.True(a.MarkNoShow(Now.AddMinutes(31)).IsSuccess);
        Assert.Equal(AppointmentStatus.NoShow, a.Status);
        Assert.IsType<AppointmentMissed>(Assert.Single(a.DomainEvents));
    }

    [Fact]
    public void Visit_start_moves_to_in_progress_from_booked_or_checked_in_and_completion_only_from_in_progress()
    {
        var booked = Booked(30);
        Assert.Equal("appointment.invalid_state", booked.Complete().Error!.Code);
        Assert.True(booked.Start(Now).IsSuccess);   // gəlişi qeyd olunmamış, amma həkimin yanındadır
        Assert.Equal(AppointmentStatus.InProgress, booked.Status);
        Assert.Equal(Now, booked.CheckedInAt);
        Assert.True(booked.HoldsSlot);
        Assert.Equal("appointment.invalid_state", booked.Start(Now).Error!.Code);

        Assert.True(booked.Complete().IsSuccess);
        Assert.Equal(AppointmentStatus.Completed, booked.Status);
        Assert.False(booked.HoldsSlot);   // tamamlanmış qəbul slotu buraxır
        Assert.Equal("appointment.invalid_state", booked.Complete().Error!.Code);

        var checkedIn = Booked(30);
        checkedIn.CheckIn(Now);
        Assert.True(checkedIn.Start(Now.AddMinutes(1)).IsSuccess);
        Assert.Equal(Now, checkedIn.CheckedInAt);   // əsl gəliş vaxtı saxlanır
    }

    [Fact]
    public void Cancelled_or_missed_appointments_cannot_start_a_visit()
    {
        var cancelled = Booked();
        cancelled.Cancel(null, Now);
        var missed = Booked(30);
        missed.MarkNoShow(Now.AddMinutes(31));

        Assert.Equal("appointment.invalid_state", cancelled.Start(Now).Error!.Code);
        Assert.Equal("appointment.invalid_state", missed.Start(Now).Error!.Code);
    }

    [Fact]
    public void Queue_ticket_can_only_be_called_once()
    {
        var t = QueueTicket.Issue(Guid.NewGuid(), Branch, Guid.NewGuid(), Patient, 1, new DateOnly(2026, 10, 7), Now);

        Assert.True(t.Call(Now).IsSuccess);
        Assert.Equal(QueueStatus.Called, t.Status);
        Assert.Equal("queue.invalid_state", t.Call(Now).Error!.Code);
    }

    [Fact]
    public void Waitlist_validates_priority_and_window()
    {
        Assert.Equal("waitlist.invalid_priority", WaitlistEntry.Create(Patient, Branch, null, null, null, 0).Error!.Code);
        Assert.Equal("waitlist.invalid_window", WaitlistEntry.Create(Patient, Branch, null, Now, Now.AddMinutes(-1), 5).Error!.Code);
        Assert.True(WaitlistEntry.Create(Patient, Branch, null, Now, Now.AddDays(1), 5).IsSuccess);
    }
}

public class SchedulePlannerTests
{
    private static readonly TimeZoneInfo Baku = TimeZoneInfo.FindSystemTimeZoneById("Asia/Baku");   // UTC+4
    private static readonly DateOnly Day = new(2026, 10, 8);
    private static readonly DateTimeOffset Morning = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);   // yerli 04:00, hələ iş başlamayıb

    private static DateTimeOffset Local(int hour, int min = 0) => SchedulePlanner.ToUtc(Day, new TimeOnly(hour, min), Baku);

    private static TimeSlot LocalSlot(int fromH, int fromM, int toH, int toM) => new(Local(fromH, fromM), Local(toH, toM));

    private static ProviderDay DayOf(WorkWindow[] windows, TimeSlot[]? off = null, TimeSlot[]? busy = null) =>
        new(windows.Length > 0, windows, off ?? [], busy ?? []);

    private static readonly WorkWindow Nine13 = new(new TimeOnly(9, 0), new TimeOnly(13, 0));
    private static readonly WorkWindow Fourteen18 = new(new TimeOnly(14, 0), new TimeOnly(18, 0));

    [Fact]
    public void Local_time_is_converted_to_utc_using_the_clinic_zone() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 5, 0, 0, TimeSpan.Zero), Local(9));

    [Fact]
    public void Provider_without_any_schedule_is_unrestricted()
    {
        var day = new ProviderDay(false, [], [], []);

        Assert.Equal(ScheduleVerdict.Allowed, SchedulePlanner.Check(day, LocalSlot(3, 0, 3, 30), Baku));
    }

    [Theory]
    [InlineData(9, 0, 9, 30, ScheduleVerdict.Allowed)]
    [InlineData(12, 30, 13, 0, ScheduleVerdict.Allowed)]      // pəncərənin sonunda bitir
    [InlineData(12, 45, 13, 15, ScheduleVerdict.OutsideSchedule)]   // pəncərəni aşır
    [InlineData(8, 45, 9, 15, ScheduleVerdict.OutsideSchedule)]
    [InlineData(13, 0, 13, 30, ScheduleVerdict.OutsideSchedule)]    // nahar fasiləsi
    [InlineData(14, 0, 14, 30, ScheduleVerdict.Allowed)]
    public void Appointment_must_fit_entirely_inside_a_work_window(int fh, int fm, int th, int tm, ScheduleVerdict expected) =>
        Assert.Equal(expected, SchedulePlanner.Check(DayOf([Nine13, Fourteen18]), LocalSlot(fh, fm, th, tm), Baku));

    [Fact]
    public void Day_without_windows_but_with_a_schedule_elsewhere_is_a_day_off() =>
        Assert.Equal(ScheduleVerdict.OutsideSchedule, SchedulePlanner.Check(new ProviderDay(true, [], [], []), LocalSlot(10, 0, 10, 30), Baku));

    [Fact]
    public void Time_off_wins_over_the_schedule() =>
        Assert.Equal(ScheduleVerdict.TimeOff, SchedulePlanner.Check(DayOf([Nine13], [LocalSlot(10, 0, 12, 0)]), LocalSlot(11, 0, 11, 30), Baku));

    [Fact]
    public void Appointment_crossing_midnight_is_outside_any_schedule()
    {
        var slot = new TimeSlot(Local(23, 30), Local(23, 30).AddHours(1));

        Assert.Equal(ScheduleVerdict.OutsideSchedule, SchedulePlanner.Check(DayOf([new WorkWindow(new TimeOnly(9, 0), new TimeOnly(23, 59))]), slot, Baku));
    }

    [Fact]
    public void Free_slots_follow_the_step_and_respect_busy_and_time_off()
    {
        var day = DayOf([Nine13], off: [LocalSlot(11, 0, 12, 0)], busy: [LocalSlot(9, 0, 9, 45)]);

        var slots = SchedulePlanner.FreeSlots(day, Day, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(15), Baku, Morning);

        // 9:00-9:45 məşğul → ilk boş 9:45. 11:00-12:00 məzuniyyət → 10:45 sığmır (10:45-11:15), 12:00 boşdur
        Assert.Equal(Local(9, 45), slots[0].Start);
        Assert.DoesNotContain(slots, s => s.Overlaps(LocalSlot(11, 0, 12, 0)));
        Assert.Contains(slots, s => s.Start == Local(10, 15));
        Assert.DoesNotContain(slots, s => s.Start == Local(10, 45));
        Assert.Contains(slots, s => s.Start == Local(12, 0));
        Assert.Equal(Local(12, 30), slots[^1].Start);   // 12:30-13:00 son sığan
        Assert.All(slots, s => Assert.Equal(TimeSpan.FromMinutes(30), s.Duration));
    }

    [Fact]
    public void Past_slots_are_not_offered()
    {
        var now = Local(10, 10);

        var slots = SchedulePlanner.FreeSlots(DayOf([Nine13]), Day, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(15), Baku, now);

        Assert.Equal(Local(10, 15), slots[0].Start);
    }

    [Fact]
    public void Several_windows_are_combined_and_duration_longer_than_window_yields_nothing()
    {
        var slots = SchedulePlanner.FreeSlots(DayOf([Nine13, Fourteen18]), Day, TimeSpan.FromHours(1), TimeSpan.FromMinutes(60), Baku, Morning);

        Assert.Equal(8, slots.Count);
        Assert.Empty(SchedulePlanner.FreeSlots(DayOf([Nine13]), Day, TimeSpan.FromHours(5), TimeSpan.FromMinutes(15), Baku, Morning));
    }

    [Fact]
    public void Reminders_skip_the_past_duplicates_and_the_none_channel()
    {
        var start = Morning.AddHours(10);

        var plan = SchedulePlanner.PlanReminders(start, "sms", [1440, 120, 120, 0, -5], Morning);

        // 24 saat əvvəl artıq keçib (indi başlanğıcdan 10 saat əvvəldir), yalnız 2 saat əvvəl qalır
        Assert.Equal([("sms", start.AddMinutes(-120))], plan);
        Assert.Empty(SchedulePlanner.PlanReminders(start, "none", [120], Morning));
        Assert.Equal(2, SchedulePlanner.PlanReminders(start.AddDays(3), "whatsapp", [1440, 120], Morning).Count);
    }
}
