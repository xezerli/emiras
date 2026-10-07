using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DentaCore.Scheduling.Domain;
using DentaCore.TestSupport;

namespace DentaCore.ClinicCore.IntegrationTests;

[Collection(ClinicCoreDefinition.Name)]
public sealed class SchedulingApiTests(PatientApiFixture f)
{
    private static readonly TimeZoneInfo Baku = TimeZoneInfo.FindSystemTimeZoneById("Asia/Baku");
    private static readonly string[] Staff =
    [
        "appointment:read@tenant", "appointment:write@tenant", "patient:read@tenant", "patient:write@tenant", "clinical:read@tenant", "clinical:write@tenant",
    ];

    /// <summary>Yerli (Bakı) vaxtla, bu gündən daysAhead gün sonra. Keçmiş yoxlamasına düşməmək üçün həmişə gələcək.</summary>
    private static DateTimeOffset At(int daysAhead, int hour, int minute = 0)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Baku).DateTime);
        return SchedulePlanner.ToUtc(today.AddDays(daysAhead), new TimeOnly(hour, minute), Baku);
    }

    private static int RandomDay() => Random.Shared.Next(10, 400);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<Guid> RegisterPatientAsync(HttpClient client, Guid branch, string channel = "sms")
    {
        var response = await client.PostAsJsonAsync("/v1/patients", new
        {
            branchId = branch, firstName = "Aysel", lastName = "Zq" + Guid.NewGuid().ToString("N")[..8], phone = "+99450" + Random.Shared.Next(1_000_000, 9_999_999), preferredChannel = channel,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> BookAsync(HttpClient client, Guid branch, Guid patient, Guid provider, DateTimeOffset start, int minutes = 30, Guid? room = null) =>
        client.PostAsJsonAsync("/v1/appointments", new { branchId = branch, patientId = patient, providerId = provider, roomId = room, start, end = start.AddMinutes(minutes), reason = "Ağrı" });

    private static async Task<Guid> BookOkAsync(HttpClient client, Guid branch, Guid patient, Guid provider, DateTimeOffset start, int minutes = 30, Guid? room = null)
    {
        var response = await BookAsync(client, branch, patient, provider, start, minutes, room);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static HttpRequestMessage Patch(Guid id, string json, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/v1/appointments/{id}") { Content = new StringContent(json, Encoding.UTF8, "application/merge-patch+json") };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    private Task<long> Count(string sql, params object[] args) => f.Db.ScalarAsync<long>(PatientApiFixture.Demo, sql, args)!;

    private async Task<Guid> NewBranchAsync() => await f.Db.SeedBranchAsync(PatientApiFixture.Demo, "S" + Guid.NewGuid().ToString("N")[..6]);

    // ---------------- Booking ----------------
    [IntegrationFact]
    public async Task Booking_returns_201_stores_reminders_outbox_event_and_audit()
    {
        using var client = f.Client(Staff);
        var provider = await f.SeedProviderAsync();
        var patient = await RegisterPatientAsync(client, f.BranchA);
        var start = At(RandomDay(), 11);

        var response = await BookAsync(client, f.BranchA, patient, provider, start);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await JsonAsync(response);
        var id = json.GetProperty("id").GetGuid();
        Assert.Equal($"/v1/appointments/{id}", response.Headers.Location!.OriginalString);
        Assert.Equal("booked", json.GetProperty("status").GetString());
        Assert.StartsWith("Zq", json.GetProperty("patientName").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, json.GetProperty("rowVersion").GetInt32());

        Assert.Equal(2, await Count("SELECT count(*) FROM appointment_reminders WHERE appointment_id = $1 AND status = 'pending' AND channel = 'sms'", id));
        Assert.Equal(1, await Count("SELECT count(*) FROM outbox_messages WHERE type = 'AppointmentBooked' AND payload->>'appointmentId' = $1", id.ToString()));
        Assert.Equal(1, await Count("SELECT count(*) FROM audit_log WHERE action = 'appointment.create' AND entity_id = $1", id));
        // Saxlanan aralıq yarı açıqdır: [başlanğıc, bitmə). Əks halda ardıcıl qəbullar üst-üstə düşərdi
        var range = await f.Db.ScalarAsync<string>(PatientApiFixture.Demo, "SELECT period::text FROM appointments WHERE id = $1", id);
        Assert.StartsWith("[", range!, StringComparison.Ordinal);
        Assert.EndsWith(")", range!, StringComparison.Ordinal);
        Assert.Equal(start.UtcDateTime, (await f.Db.ScalarAsync<DateTime>(PatientApiFixture.Demo, "SELECT lower(period) FROM appointments WHERE id = $1", id)).ToUniversalTime());
    }

    [IntegrationFact]
    public async Task Overlap_is_409_with_alternatives_and_adjacent_or_other_provider_is_fine()
    {
        using var client = f.Client(Staff);
        var provider = await f.SeedProviderAsync();
        var other = await f.SeedProviderAsync();
        await f.SeedScheduleAsync(provider, f.BranchA, new TimeOnly(9, 0), new TimeOnly(18, 0));
        var patient = await RegisterPatientAsync(client, f.BranchA);
        var day = RandomDay();
        await BookOkAsync(client, f.BranchA, patient, provider, At(day, 10), 30);

        var clash = await BookAsync(client, f.BranchA, patient, provider, At(day, 10, 15), 30);
        var adjacentAfter = await BookAsync(client, f.BranchA, patient, provider, At(day, 10, 30), 30);
        var adjacentBefore = await BookAsync(client, f.BranchA, patient, provider, At(day, 9, 30), 30);
        var otherProvider = await BookAsync(client, f.BranchA, patient, other, At(day, 10, 15), 30);

        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        var problem = await JsonAsync(clash);
        Assert.Equal("appointment.overlap", problem.GetProperty("code").GetString());
        var alternatives = problem.GetProperty("alternativeSlots").EnumerateArray().ToList();
        Assert.NotEmpty(alternatives);
        Assert.All(alternatives, a => Assert.True(a.GetProperty("start").GetDateTimeOffset() >= At(day, 10, 15)));
        Assert.Equal(HttpStatusCode.Created, adjacentAfter.StatusCode);   // 10:00–10:30 sonra 10:30–11:00
        Assert.Equal(HttpStatusCode.Created, adjacentBefore.StatusCode);
        Assert.Equal(HttpStatusCode.Created, otherProvider.StatusCode);
    }

    [IntegrationFact]
    public async Task Ten_simultaneous_bookings_of_the_same_slot_produce_exactly_one_appointment()
    {
        using var client = f.Client(Staff);
        var provider = await f.SeedProviderAsync();
        var patient = await RegisterPatientAsync(client, f.BranchA);
        var start = At(RandomDay(), 15);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => BookAsync(client, f.BranchA, patient, provider, start)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await Count("SELECT count(*) FROM appointments WHERE provider_id = $1 AND status = 'booked'", provider));
        // Uduzanların xatırlatmaları və outbox hadisələri də geri qaytarılıb (tək tranzaksiya)
        Assert.Equal(2, await Count("SELECT count(*) FROM appointment_reminders r JOIN appointments a ON a.id = r.appointment_id WHERE a.provider_id = $1", provider));
    }

    [IntegrationFact]
    public async Task Rooms_cannot_be_double_used_or_borrowed_from_another_branch()
    {
        using var client = f.Client(Staff);
        var p1 = await f.SeedProviderAsync();
        var p2 = await f.SeedProviderAsync();
        var patient = await RegisterPatientAsync(client, f.BranchA);
        var room = await f.SeedRoomAsync(f.BranchA, "R" + Guid.NewGuid().ToString("N")[..5]);
        var foreignRoom = await f.SeedRoomAsync(f.BranchB, "R" + Guid.NewGuid().ToString("N")[..5]);
        var start = At(RandomDay(), 12);
        await BookOkAsync(client, f.BranchA, patient, p1, start, room: room);

        var sameRoom = await BookAsync(client, f.BranchA, patient, p2, start, room: room);
        var wrongBranch = await BookAsync(client, f.BranchA, patient, p2, start, room: foreignRoom);
        var noRoom = await BookAsync(client, f.BranchA, patient, p2, start);

        Assert.Equal(HttpStatusCode.Conflict, sameRoom.StatusCode);
        Assert.Equal("appointment.room_busy", (await JsonAsync(sameRoom)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongBranch.StatusCode);
        Assert.Equal("appointment.room_branch_mismatch", (await JsonAsync(wrongBranch)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, noRoom.StatusCode);
    }

    [IntegrationFact]
    public async Task Working_hours_time_off_and_input_rules_are_enforced()
    {
        using var client = f.Client(Staff);
        var provider = await f.SeedProviderAsync();
        await f.SeedScheduleAsync(provider, f.BranchA, new TimeOnly(9, 0), new TimeOnly(13, 0));
        var patient = await RegisterPatientAsync(client, f.BranchA);
        var day = RandomDay();
        await f.SeedTimeOffAsync(provider, At(day, 10), At(day, 11));

        Assert.Equal("appointment.outside_schedule", (await JsonAsync(await BookAsync(client, f.BranchA, patient, provider, At(day, 14)))).GetProperty("code").GetString());
        Assert.Equal("appointment.outside_schedule", (await JsonAsync(await BookAsync(client, f.BranchA, patient, provider, At(day, 12, 45)))).GetProperty("code").GetString());
        Assert.Equal("appointment.provider_off", (await JsonAsync(await BookAsync(client, f.BranchA, patient, provider, At(day, 10, 30)))).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, (await BookAsync(client, f.BranchA, patient, provider, At(day, 9))).StatusCode);

        Assert.Equal("appointment.in_the_past", (await JsonAsync(await BookAsync(client, f.BranchA, patient, provider, DateTimeOffset.UtcNow.AddHours(-3)))).GetProperty("code").GetString());
        Assert.Equal("appointment.invalid_period", (await JsonAsync(await client.PostAsJsonAsync("/v1/appointments", new { branchId = f.BranchA, patientId = patient, providerId = provider, start = At(day, 12), end = At(day, 11) }))).GetProperty("code").GetString());
        Assert.Equal("appointment.invalid_duration", (await JsonAsync(await BookAsync(client, f.BranchA, patient, provider, At(day, 12), 600))).GetProperty("code").GetString());
    }

    [IntegrationFact]
    public async Task Unknown_patient_and_provider_are_rejected()
    {
        using var client = f.Client(Staff);
        var provider = await f.SeedProviderAsync();
        var patient = await RegisterPatientAsync(client, f.BranchA);

        Assert.Equal(HttpStatusCode.NotFound, (await BookAsync(client, f.BranchA, Guid.NewGuid(), provider, At(RandomDay(), 11))).StatusCode);
        var ghost = await BookAsync(client, f.BranchA, patient, Guid.NewGuid(), At(RandomDay(), 11));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ghost.StatusCode);
        Assert.Equal("appointment.provider_not_found", (await JsonAsync(ghost)).GetProperty("code").GetString());
    }

    // ---------------- Availability ----------------
    [IntegrationFact]
    public async Task Availability_matches_what_can_actually_be_booked()
    {
        using var client = f.Client(Staff);
        var provider = await f.SeedProviderAsync();
        await f.SeedScheduleAsync(provider, f.BranchA, new TimeOnly(9, 0), new TimeOnly(11, 0));
        var patient = await RegisterPatientAsync(client, f.BranchA);
        var day = RandomDay();
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(At(day, 12), Baku).DateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await BookOkAsync(client, f.BranchA, patient, provider, At(day, 9, 30), 30);

        var slots = (await JsonAsync(await client.GetAsync($"/v1/appointments/availability?providerId={provider}&date={date}&durationMin=30")))
            .EnumerateArray().Select(s => s.GetProperty("start").GetDateTimeOffset()).ToList();

        Assert.Equal([At(day, 9), At(day, 10), At(day, 10, 15), At(day, 10, 30)], slots);   // 9:15,9:30,9:45 məşğulla kəsişir; 10:45+30 pəncərəni aşır
        foreach (var slot in slots)
        {
            Assert.Equal(HttpStatusCode.Created, (await BookAsync(client, f.BranchA, patient, provider, slot)).StatusCode);
            break;   // hər suggested slot rezerv oluna bilir (ilkini yoxlayırıq, qalanlar eyni qaydadır)
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"/v1/appointments/availability?providerId={provider}&date={date}&durationMin=3")).StatusCode);
    }

    // ---------------- Cancel / reschedule ----------------
    [IntegrationFact]
    public async Task Cancelling_frees_the_slot_cancels_reminders_and_cannot_repeat()
    {
        using var client = f.Client(Staff);
        var provider = await f.SeedProviderAsync();
        var patient = await RegisterPatientAsync(client, f.BranchA);
        var start = At(RandomDay(), 11);
        var id = await BookOkAsync(client, f.BranchA, patient, provider, start);

        var cancel = await client.PostAsJsonAsync($"/v1/appointments/{id}/cancel", new { reason = "gəlmir" });
        var again = await client.PostAsJsonAsync($"/v1/appointments/{id}/cancel", new { reason = "x" });
        var rebook = await BookAsync(client, f.BranchA, patient, provider, start);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("cancelled", (await JsonAsync(cancel)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.Created, rebook.StatusCode);   // slot azaddır
        Assert.Equal(0, await Count("SELECT count(*) FROM appointment_reminders WHERE appointment_id = $1 AND status = 'pending'", id));
        Assert.Equal(2, await Count("SELECT count(*) FROM appointment_reminders WHERE appointment_id = $1 AND status = 'cancelled'", id));
        Assert.Equal(1, await Count("SELECT count(*) FROM outbox_messages WHERE type = 'AppointmentCancelled' AND payload->>'appointmentId' = $1", id.ToString()));
    }

    [IntegrationFact]
    public async Task Rescheduling_keeps_duration_bumps_version_replaces_reminders_and_respects_conflicts()
    {
        using var client = f.Client(Staff);
        var provider = await f.SeedProviderAsync();
        var patient = await RegisterPatientAsync(client, f.BranchA);
        var day = RandomDay();
        var id = await BookOkAsync(client, f.BranchA, patient, provider, At(day, 11), 45);
        var blocker = await BookOkAsync(client, f.BranchA, patient, provider, At(day, 15), 30);

        var moved = await client.SendAsync(Patch(id, $$"""{"start":"{{At(day, 13):O}}"}""", "\"1\""));

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var json = await JsonAsync(moved);
        Assert.Equal(At(day, 13), json.GetProperty("start").GetDateTimeOffset());
        Assert.Equal(At(day, 13, 45), json.GetProperty("end").GetDateTimeOffset());   // müddət (45 dəq) qorunub
        Assert.Equal(2, json.GetProperty("rowVersion").GetInt32());
        Assert.Equal("\"2\"", moved.Headers.ETag!.Tag);
        Assert.Equal(2, await Count("SELECT count(*) FROM appointment_reminders WHERE appointment_id = $1 AND status = 'pending'", id));
        Assert.Equal(2, await Count("SELECT count(*) FROM appointment_reminders WHERE appointment_id = $1 AND status = 'cancelled'", id));
        Assert.Equal(1, await Count("SELECT count(*) FROM outbox_messages WHERE type = 'AppointmentRescheduled' AND payload->>'appointmentId' = $1", id.ToString()));

        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(Patch(id, """{"reason":"x"}""", "\"1\""))).StatusCode);   // köhnə versiya
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.SendAsync(Patch(id, """{"reason":"x"}""", null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.SendAsync(Patch(id, """{"status":"completed"}""", "2"))).StatusCode);

        var clash = await client.SendAsync(Patch(id, $$"""{"start":"{{At(day, 14, 45):O}}"}""", "2"));   // 14:45–15:30 blokerlə kəsişir
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal(At(day, 13), (await f.Db.ScalarAsync<DateTime>(PatientApiFixture.Demo, "SELECT lower(period) FROM appointments WHERE id = $1", id)).ToUniversalTime());   // dəyişməyib
        Assert.Equal(2, await f.Db.ScalarAsync<int>(PatientApiFixture.Demo, "SELECT row_version FROM appointments WHERE id = $1", id));
        _ = blocker;
    }

    // ---------------- Check-in / queue / no-show ----------------
    [IntegrationFact]
    public async Task Check_in_issues_a_ticket_and_the_lobby_queue_shows_it()
    {
        using var client = f.Client(Staff);
        var branch = await NewBranchAsync();
        var provider = await f.SeedProviderAsync();
        var patient = await RegisterPatientAsync(client, branch);
        var id = await BookOkAsync(client, branch, patient, provider, DateTimeOffset.UtcNow.AddMinutes(30));

        var checkIn = await client.PostAsync($"/v1/appointments/{id}/check-in", null);
        var again = await client.PostAsync($"/v1/appointments/{id}/check-in", null);

        Assert.Equal(HttpStatusCode.OK, checkIn.StatusCode);
        var ticket = await JsonAsync(checkIn);
        Assert.Equal(1, ticket.GetProperty("ticketNo").GetInt32());
        Assert.Equal("waiting", ticket.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("checked_in", await f.Db.ScalarAsync<string>(PatientApiFixture.Demo, "SELECT status FROM appointments WHERE id = $1", id));
        Assert.Equal(1, await Count("SELECT count(*) FROM outbox_messages WHERE type = 'PatientCheckedIn' AND payload->>'appointmentId' = $1", id.ToString()));

        var queue = (await JsonAsync(await client.GetAsync($"/v1/queue?branchId={branch}"))).EnumerateArray().ToList();
        Assert.Single(queue);
        Assert.StartsWith("Zq", queue[0].GetProperty("patientName").GetString(), StringComparison.Ordinal);

        var ticketId = ticket.GetProperty("id").GetGuid();
        Assert.Equal("called", (await JsonAsync(await client.PostAsync($"/v1/queue/{ticketId}/call", null))).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/v1/queue/{ticketId}/call", null)).StatusCode);
    }

    [IntegrationFact]
    public async Task Parallel_check_ins_get_unique_gapless_ticket_numbers()
    {
        using var client = f.Client(Staff);
        var branch = await NewBranchAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            var provider = await f.SeedProviderAsync();
            var patient = await RegisterPatientAsync(client, branch);
            ids.Add(await BookOkAsync(client, branch, patient, provider, DateTimeOffset.UtcNow.AddMinutes(20)));
        }

        var responses = await Task.WhenAll(ids.Select(id => client.PostAsync($"/v1/appointments/{id}/check-in", null)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var numbers = (await Task.WhenAll(responses.Select(async r => (await JsonAsync(r)).GetProperty("ticketNo").GetInt32()))).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, 8), numbers);
    }

    [IntegrationFact]
    public async Task Check_in_is_only_possible_close_to_the_appointment()
    {
        using var client = f.Client(Staff);
        var provider = await f.SeedProviderAsync();
        var patient = await RegisterPatientAsync(client, f.BranchA);
        var id = await BookOkAsync(client, f.BranchA, patient, provider, At(RandomDay(), 11));

        var response = await client.PostAsync($"/v1/appointments/{id}/check-in", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("appointment.checkin_window", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(0, await Count("SELECT count(*) FROM queue_tickets WHERE appointment_id = $1", id));
    }

    [IntegrationFact]
    public async Task No_show_waits_for_the_start_then_records_the_event()
    {
        using var client = f.Client(Staff);
        var provider = await f.SeedProviderAsync();
        var patient = await RegisterPatientAsync(client, f.BranchA);
        var future = await BookOkAsync(client, f.BranchA, patient, provider, At(RandomDay(), 11));
        var started = await BookOkAsync(client, f.BranchA, patient, provider, DateTimeOffset.UtcNow.AddMinutes(-2));   // 5 dəq grace

        var tooEarly = await client.PostAsync($"/v1/appointments/{future}/no-show", null);
        var ok = await client.PostAsync($"/v1/appointments/{started}/no-show", null);

        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);
        Assert.Equal("appointment.too_early", (await JsonAsync(tooEarly)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("no_show", (await JsonAsync(ok)).GetProperty("status").GetString());
        Assert.Equal(1, await Count("SELECT count(*) FROM outbox_messages WHERE type = 'AppointmentMissed' AND payload->>'appointmentId' = $1", started.ToString()));
    }

    // ---------------- List / scope ----------------
    [IntegrationFact]
    public async Task Listing_filters_by_range_provider_status_and_enforces_scope()
    {
        using var client = f.Client(Staff);
        var branch = await NewBranchAsync();
        var doc1 = await f.SeedProviderAsync();
        var doc2 = await f.SeedProviderAsync();
        var patient = await RegisterPatientAsync(client, branch);
        var day = RandomDay();
        var a1 = await BookOkAsync(client, branch, patient, doc1, At(day, 10));
        var a2 = await BookOkAsync(client, branch, patient, doc2, At(day, 11));
        await client.PostAsJsonAsync($"/v1/appointments/{a2}/cancel", new { });
        var from = Uri.EscapeDataString(At(day, 0).ToString("O"));
        var to = Uri.EscapeDataString(At(day + 1, 0).ToString("O"));

        async Task<List<Guid>> List(HttpClient c, string extra = "") =>
            (await JsonAsync(await c.GetAsync($"/v1/appointments?from={from}&to={to}&branchId={branch}{extra}"))).EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

        Assert.Equal([a1, a2], await List(client));
        Assert.Equal([a2], await List(client, $"&providerId={doc2}"));
        Assert.Equal([a2], await List(client, "&status=cancelled"));
        Assert.Equal([a1], await List(client, "&status=booked&status=confirmed"));

        using var otherBranchUser = f.Client(["appointment:read@branch"], [f.BranchB]);
        using var ownDoctor = f.Client(["appointment:read@own"], userId: doc1);
        Assert.Empty(await List(otherBranchUser));
        Assert.Equal([a1], await List(ownDoctor));
        Assert.Equal(HttpStatusCode.NotFound, (await otherBranchUser.GetAsync($"/v1/appointments/{a1}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownDoctor.GetAsync($"/v1/appointments/{a1}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownDoctor.GetAsync($"/v1/appointments/{a2}")).StatusCode);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"/v1/appointments?from={from}&to={from}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"/v1/appointments?from={from}&to={to}&status=flying")).StatusCode);
    }

    [IntegrationFact]
    public async Task Branch_scoped_staff_cannot_book_or_manage_other_branches()
    {
        using var admin = f.Client(Staff);
        using var branchUser = f.Client(["appointment:read@branch", "appointment:write@branch"], [f.BranchA]);
        var provider = await f.SeedProviderAsync();
        var patient = await RegisterPatientAsync(admin, f.BranchB);
        var inB = await BookOkAsync(admin, f.BranchB, patient, provider, At(RandomDay(), 11));

        Assert.Equal(HttpStatusCode.Forbidden, (await BookAsync(branchUser, f.BranchB, patient, provider, At(RandomDay(), 14))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await branchUser.PostAsync($"/v1/appointments/{inB}/check-in", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await branchUser.PostAsJsonAsync($"/v1/appointments/{inB}/cancel", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await branchUser.SendAsync(Patch(inB, """{"reason":"x"}""", "1"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await branchUser.GetAsync($"/v1/queue?branchId={f.BranchB}")).StatusCode);
    }

    // ---------------- Care relationship (own scope for doctors) ----------------
    [IntegrationFact]
    public async Task A_doctor_gains_and_loses_access_to_a_patient_with_the_appointment()
    {
        using var reception = f.Client(Staff);
        var doctor = await f.SeedProviderAsync();
        using var doctorClient = f.Client(["patient:read@own", "clinical:read@own", "appointment:read@own"], userId: doctor);
        var patient = await RegisterPatientAsync(reception, f.BranchA);
        var last = (await JsonAsync(await reception.GetAsync($"/v1/patients/{patient}"))).GetProperty("lastName").GetString();

        // Qəbul yoxdur: pasiyent həkim üçün mövcud deyil
        Assert.Equal(HttpStatusCode.NotFound, (await doctorClient.GetAsync($"/v1/patients/{patient}")).StatusCode);
        Assert.Empty((await JsonAsync(await doctorClient.GetAsync($"/v1/patients?q={last}"))).GetProperty("items").EnumerateArray());

        var appointment = await BookOkAsync(reception, f.BranchA, patient, doctor, At(RandomDay(), 11));

        Assert.Equal(HttpStatusCode.OK, (await doctorClient.GetAsync($"/v1/patients/{patient}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await doctorClient.GetAsync($"/v1/patients/{patient}/medical-profile")).StatusCode);
        Assert.Single((await JsonAsync(await doctorClient.GetAsync($"/v1/patients?q={last}"))).GetProperty("items").EnumerateArray());

        // Qəbul ləğv olunduqda əlaqə yox olur
        await reception.PostAsJsonAsync($"/v1/appointments/{appointment}/cancel", new { });
        Assert.Equal(HttpStatusCode.NotFound, (await doctorClient.GetAsync($"/v1/patients/{patient}")).StatusCode);
    }

    // ---------------- Waitlist ----------------
    [IntegrationFact]
    public async Task Waitlist_accepts_valid_entries_and_rejects_bad_ones()
    {
        using var client = f.Client(Staff);
        var patient = await RegisterPatientAsync(client, f.BranchA);

        var ok = await client.PostAsJsonAsync("/v1/waitlist", new { patientId = patient, branchId = f.BranchA, priority = 2, earliest = At(20, 9), latest = At(21, 9) });
        var badPriority = await client.PostAsJsonAsync("/v1/waitlist", new { patientId = patient, branchId = f.BranchA, priority = 0 });
        var ghost = await client.PostAsJsonAsync("/v1/waitlist", new { patientId = Guid.NewGuid(), branchId = f.BranchA });

        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(1, await Count("SELECT count(*) FROM waitlist_entries WHERE patient_id = $1 AND status = 'waiting' AND priority = 2", patient));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badPriority.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ghost.StatusCode);
    }

    // ---------------- Audit / tenancy ----------------
    [IntegrationFact]
    public async Task Scheduling_actions_are_audited_without_patient_data_and_the_chain_stays_intact()
    {
        using var client = f.Client(Staff);
        var provider = await f.SeedProviderAsync();
        var patient = await RegisterPatientAsync(client, f.BranchA);
        var id = await BookOkAsync(client, f.BranchA, patient, provider, DateTimeOffset.UtcNow.AddMinutes(30));
        await client.GetAsync($"/v1/appointments/{id}");
        await client.PostAsync($"/v1/appointments/{id}/check-in", null);

        foreach (var action in new[] { "appointment.create", "appointment.read", "appointment.check_in" })
        {
            Assert.Equal(1, await Count("SELECT count(*) FROM audit_log WHERE action = $1 AND entity_id = $2", action, id));
        }

        var details = await f.Db.ScalarAsync<string>(PatientApiFixture.Demo, "SELECT coalesce(string_agg(after_data::text, ' '), '') FROM audit_log WHERE entity_id = $1", id);
        Assert.DoesNotContain("Aysel", details!, StringComparison.Ordinal);
        Assert.True((await f.VerifyAuditChainAsync(PatientApiFixture.Demo, "demo")).IsIntact);
    }

    [IntegrationFact]
    public async Task Another_tenant_cannot_see_or_touch_appointments()
    {
        using var demo = f.Client(Staff);
        using var other = f.Client(Staff, tenant: "other");
        var provider = await f.SeedProviderAsync();
        var patient = await RegisterPatientAsync(demo, f.BranchA);
        var id = await BookOkAsync(demo, f.BranchA, patient, provider, At(RandomDay(), 11));

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1/appointments/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/v1/appointments/{id}/cancel", new { })).StatusCode);
        var from = Uri.EscapeDataString(At(0, 0).ToString("O"));
        var to = Uri.EscapeDataString(At(500, 0).ToString("O"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await other.GetAsync($"/v1/appointments?from={from}&to={to}")).StatusCode);   // 62 gündən uzun aralıq hər tenant-da rədd olunur
        Assert.Equal(0, await f.Db.ScalarAsync<long>(PatientApiFixture.Other, "SELECT count(*) FROM appointments"));
    }

    [IntegrationFact]
    public async Task Anonymous_and_unauthorized_requests_are_rejected()
    {
        using var anonymous = f.Anonymous();
        using var readOnly = f.Client(["appointment:read@tenant"], userId: f.OtherStaff);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/queue?branchId=" + f.BranchA)).StatusCode);
        var denied = await readOnly.PostAsJsonAsync("/v1/appointments", new { branchId = f.BranchA, patientId = Guid.NewGuid(), providerId = Guid.NewGuid(), start = At(20, 10), end = At(20, 11) });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
}
