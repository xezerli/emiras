using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DentaCore.TestSupport;

namespace DentaCore.ClinicCore.IntegrationTests;

[Collection(ClinicCoreDefinition.Name)]
public sealed class ClinicalApiTests(PatientApiFixture f)
{
    private static readonly string[] Staff =
    [
        "appointment:read@tenant", "appointment:write@tenant", "patient:read@tenant", "patient:write@tenant", "clinical:read@tenant", "clinical:write@tenant",
    ];

    private static readonly string[] DoctorPerms = ["clinical:read@tenant", "clinical:write@tenant", "prescription:write@tenant", "patient:read@tenant"];

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<Guid> RegisterPatientAsync(HttpClient client, Guid branch)
    {
        var response = await client.PostAsJsonAsync("/v1/patients", new { branchId = branch, firstName = "Aysel", lastName = "Zq" + Guid.NewGuid().ToString("N")[..8], phone = "+99450" + Random.Shared.Next(1_000_000, 9_999_999) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<(HttpClient Client, Guid Id)> DoctorAsync(string[]? perms = null)
    {
        var id = await f.SeedProviderAsync();
        return (f.Client(perms ?? DoctorPerms, userId: id), id);
    }

    private static async Task<Guid> StartVisitAsync(HttpClient doctor, Guid patient, Guid? appointment = null)
    {
        var response = await doctor.PostAsJsonAsync("/v1/visits", new { patientId = patient, appointmentId = appointment, chiefComplaint = "Soyuğa həssaslıq" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private Task<long> Count(string sql, params object[] args) => f.Db.ScalarAsync<long>(PatientApiFixture.Demo, sql, args)!;

    private static object Item(string code, int? tooth, decimal price, int qty = 1, decimal discount = 0) =>
        new { procedureCode = code, toothFdi = tooth, quantity = qty, unitPrice = price, discountPercent = discount };

    private static async Task<JsonElement> CreatePlanAsync(HttpClient doctor, Guid patient, params object[] items)
    {
        var response = await doctor.PostAsJsonAsync($"/v1/patients/{patient}/treatment-plans", new { title = "Yuxarı sağ müalicə", items });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonAsync(response);
    }

    // ---------------- Visits ----------------
    [IntegrationFact]
    public async Task Visit_from_a_checked_in_appointment_moves_it_to_in_progress_and_completed()
    {
        using var staff = f.Client(Staff);
        var (doctor, doctorId) = await DoctorAsync();
        using var _ = doctor;
        var patient = await RegisterPatientAsync(staff, f.BranchB);
        var start = DateTimeOffset.UtcNow.AddMinutes(30);
        var booked = await staff.PostAsJsonAsync("/v1/appointments", new { branchId = f.BranchB, patientId = patient, providerId = doctorId, start, end = start.AddMinutes(30) });
        var appointment = (await JsonAsync(booked)).GetProperty("id").GetGuid();
        await staff.PostAsync($"/v1/appointments/{appointment}/check-in", null);

        var started = await doctor.PostAsJsonAsync("/v1/visits", new { patientId = patient, appointmentId = appointment });

        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var visit = await JsonAsync(started);
        var visitId = visit.GetProperty("id").GetGuid();
        Assert.Equal(f.BranchB, visit.GetProperty("branchId").GetGuid());   // filial qəbuldan götürülür
        Assert.Equal("open", visit.GetProperty("status").GetString());
        Assert.Equal("in_progress", await f.Db.ScalarAsync<string>(PatientApiFixture.Demo, "SELECT status FROM appointments WHERE id = $1", appointment));

        var again = await doctor.PostAsJsonAsync("/v1/visits", new { patientId = patient });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(visitId, (await JsonAsync(again)).GetProperty("existingVisitId").GetGuid());

        var closed = await doctor.PostAsync($"/v1/visits/{visitId}/close", null);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Equal("closed", (await JsonAsync(closed)).GetProperty("status").GetString());
        Assert.Equal("completed", await f.Db.ScalarAsync<string>(PatientApiFixture.Demo, "SELECT status FROM appointments WHERE id = $1", appointment));
        Assert.Equal(1, await Count("SELECT count(*) FROM outbox_messages WHERE type = 'VisitClosed' AND payload->>'visitId' = $1", visitId.ToString()));
        Assert.Equal(HttpStatusCode.Conflict, (await doctor.PostAsync($"/v1/visits/{visitId}/close", null)).StatusCode);
    }

    [IntegrationFact]
    public async Task A_visit_cannot_be_attached_to_someone_elses_appointment_or_closed_by_another_doctor()
    {
        using var staff = f.Client(Staff);
        var (doctor, _) = await DoctorAsync();
        var (other, otherId) = await DoctorAsync(["clinical:read@own", "clinical:write@own"]);
        using var d1 = doctor;
        using var d2 = other;
        var patient = await RegisterPatientAsync(staff, f.BranchA);
        var start = DateTimeOffset.UtcNow.AddMinutes(30);
        var booked = await staff.PostAsJsonAsync("/v1/appointments", new { branchId = f.BranchA, patientId = patient, providerId = otherId, start, end = start.AddMinutes(30) });
        var appointment = (await JsonAsync(booked)).GetProperty("id").GetGuid();

        var mismatch = await doctor.PostAsJsonAsync("/v1/visits", new { patientId = patient, appointmentId = appointment });
        var visitId = await StartVisitAsync(doctor, patient);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        Assert.Equal("visit.appointment_mismatch", (await JsonAsync(mismatch)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/v1/visits/{visitId}/close", null)).StatusCode);
    }

    [IntegrationFact]
    public async Task Six_simultaneous_visit_starts_create_exactly_one_open_visit()
    {
        using var staff = f.Client(Staff);
        var (doctor, doctorId) = await DoctorAsync();
        using var _ = doctor;
        var patient = await RegisterPatientAsync(staff, f.BranchA);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => doctor.PostAsJsonAsync("/v1/visits", new { patientId = patient })));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await Count("SELECT count(*) FROM visits WHERE patient_id = $1 AND provider_id = $2 AND status = 'open'", patient, doctorId));
    }

    // ---------------- Odontogram ----------------
    [IntegrationFact]
    public async Task Odontogram_keeps_history_supersedes_per_surface_and_whole_tooth_replaces_all()
    {
        using var staff = f.Client(Staff);
        var (doctor, _) = await DoctorAsync();
        using var _ = doctor;
        var patient = await RegisterPatientAsync(staff, f.BranchA);

        async Task Record(object body) => Assert.Equal(HttpStatusCode.Created, (await doctor.PostAsJsonAsync($"/v1/patients/{patient}/odontogram", body)).StatusCode);

        async Task<List<JsonElement>> Current(int fdi)
        {
            var json = await JsonAsync(await doctor.GetAsync($"/v1/patients/{patient}/odontogram"));
            return json.GetProperty("teeth").TryGetProperty(fdi.ToString(System.Globalization.CultureInfo.InvariantCulture), out var t) ? t.EnumerateArray().ToList() : [];
        }

        await Record(new { toothFdi = 16, surface = "O", condition = "caries" });
        await Record(new { toothFdi = 16, surface = "M", condition = "caries" });
        Assert.Equal(2, (await Current(16)).Count);

        await Record(new { toothFdi = 16, surface = "O", condition = "filling", material = "kompozit" });   // yalnız O əvəz olunur
        var afterFilling = await Current(16);
        Assert.Equal(2, afterFilling.Count);
        Assert.Equal("filling", afterFilling.Single(r => r.GetProperty("surface").GetString() == "O").GetProperty("condition").GetString());
        Assert.Equal("caries", afterFilling.Single(r => r.GetProperty("surface").GetString() == "M").GetProperty("condition").GetString());

        await Record(new { toothFdi = 16, condition = "extracted" });   // bütün diş: səthlərin hamısını əvəz edir
        var afterExtraction = await Current(16);
        Assert.Single(afterExtraction);
        Assert.Equal("extracted", afterExtraction[0].GetProperty("condition").GetString());

        var history = (await JsonAsync(await doctor.GetAsync($"/v1/patients/{patient}/odontogram/16/history"))).EnumerateArray().ToList();
        Assert.Equal(4, history.Count);                      // heç nə silinməyib
        Assert.Equal(3, history.Count(h => h.GetProperty("supersededAt").ValueKind != JsonValueKind.Null));
        Assert.Equal(1, await Count("SELECT count(*) FROM tooth_records WHERE patient_id = $1 AND tooth_fdi = 16 AND superseded_at IS NULL", patient));
    }

    [IntegrationFact]
    public async Task Odontogram_time_travel_returns_the_state_at_that_moment()
    {
        using var staff = f.Client(Staff);
        var (doctor, _) = await DoctorAsync();
        using var _ = doctor;
        var patient = await RegisterPatientAsync(staff, f.BranchA);
        await doctor.PostAsJsonAsync($"/v1/patients/{patient}/odontogram", new { toothFdi = 36, surface = "O", condition = "caries" });
        await Task.Delay(30);
        await doctor.PostAsJsonAsync($"/v1/patients/{patient}/odontogram", new { toothFdi = 36, surface = "O", condition = "filling" });
        var history = (await JsonAsync(await doctor.GetAsync($"/v1/patients/{patient}/odontogram/36/history"))).EnumerateArray().ToList();
        var firstAt = history.Single(h => h.GetProperty("condition").GetString() == "caries").GetProperty("recordedAt").GetDateTimeOffset();
        var secondAt = history.Single(h => h.GetProperty("condition").GetString() == "filling").GetProperty("recordedAt").GetDateTimeOffset();

        async Task<string> At(DateTimeOffset moment) =>
            (await JsonAsync(await doctor.GetAsync($"/v1/patients/{patient}/odontogram?at={Uri.EscapeDataString(moment.ToString("O"))}"))).GetProperty("teeth").GetProperty("36")[0].GetProperty("condition").GetString()!;

        Assert.Equal("caries", await At(firstAt));
        Assert.Equal("filling", await At(secondAt));
        Assert.Equal("no-store", (await doctor.GetAsync($"/v1/patients/{patient}/odontogram")).Headers.CacheControl!.ToString());
    }

    [IntegrationFact]
    public async Task Simultaneous_records_for_the_same_tooth_never_leave_two_current_records()
    {
        using var staff = f.Client(Staff);
        var (doctor, _) = await DoctorAsync();
        using var _ = doctor;
        var patient = await RegisterPatientAsync(staff, f.BranchA);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            doctor.PostAsJsonAsync($"/v1/patients/{patient}/odontogram", new { toothFdi = 26, surface = "B", condition = i % 2 == 0 ? "caries" : "filling" })));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));   // kilid sayəsində hamısı ardıcıl keçir
        Assert.Equal(1, await Count("SELECT count(*) FROM tooth_records WHERE patient_id = $1 AND tooth_fdi = 26 AND surface = 'B' AND superseded_at IS NULL", patient));
        Assert.Equal(6, await Count("SELECT count(*) FROM tooth_records WHERE patient_id = $1 AND tooth_fdi = 26", patient));
    }

    [IntegrationFact]
    public async Task Odontogram_input_is_validated_and_dentition_is_detected()
    {
        using var staff = f.Client(Staff);
        var (doctor, _) = await DoctorAsync();
        using var _ = doctor;
        var patient = await RegisterPatientAsync(staff, f.BranchA);
        Task<HttpResponseMessage> Post(object body) => doctor.PostAsJsonAsync($"/v1/patients/{patient}/odontogram", body);

        Assert.Equal("odontogram.invalid_tooth", (await JsonAsync(await Post(new { toothFdi = 19, condition = "caries" }))).GetProperty("code").GetString());
        Assert.Equal("odontogram.invalid_tooth", (await JsonAsync(await Post(new { toothFdi = 56, condition = "caries" }))).GetProperty("code").GetString());
        Assert.Equal("odontogram.invalid_surface", (await JsonAsync(await Post(new { toothFdi = 16, surface = "X", condition = "caries" }))).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(new { toothFdi = 16, condition = "levitating" })).StatusCode);

        await Post(new { toothFdi = 55, condition = "caries" });
        Assert.Equal("primary", (await JsonAsync(await doctor.GetAsync($"/v1/patients/{patient}/odontogram"))).GetProperty("dentition").GetString());
        await Post(new { toothFdi = 16, condition = "healthy" });
        Assert.Equal("mixed", (await JsonAsync(await doctor.GetAsync($"/v1/patients/{patient}/odontogram"))).GetProperty("dentition").GetString());

        var visitId = await StartVisitAsync(doctor, patient);
        await doctor.PostAsync($"/v1/visits/{visitId}/close", null);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(new { toothFdi = 16, condition = "caries", visitId })).StatusCode);   // bağlı vizitə yazmaq olmaz
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(new { toothFdi = 16, condition = "caries", visitId = Guid.NewGuid() })).StatusCode);
    }

    // ---------------- Treatment plans ----------------
    [IntegrationFact]
    public async Task Procedure_catalog_is_searchable()
    {
        using var staff = f.Client(Staff);

        var codes = (await JsonAsync(await staff.GetAsync("/v1/procedure-codes?q=kanal"))).EnumerateArray().Select(c => c.GetProperty("code").GetString()).ToList();
        var all = (await JsonAsync(await staff.GetAsync("/v1/procedure-codes"))).EnumerateArray().ToList();
        var wildcard = (await JsonAsync(await staff.GetAsync("/v1/procedure-codes?q=%25"))).EnumerateArray().ToList();

        Assert.Equal(["D3310", "D3320", "D3330"], codes);
        Assert.True(all.Count >= 18);
        Assert.Empty(wildcard);   // "%" literal sayılır
    }

    [IntegrationFact]
    public async Task Plan_totals_validation_and_full_lifecycle_with_billing_ready_events()
    {
        using var staff = f.Client(Staff);
        var (doctor, doctorId) = await DoctorAsync();
        using var _ = doctor;
        var patient = await RegisterPatientAsync(staff, f.BranchA);

        // Validasiya
        Task<HttpResponseMessage> Create(params object[] items) => doctor.PostAsJsonAsync($"/v1/patients/{patient}/treatment-plans", new { title = "T", items });
        Assert.Equal("plan.unknown_procedure", (await JsonAsync(await Create(Item("X9999", 16, 10)))).GetProperty("code").GetString());
        Assert.Equal("plan.tooth_required", (await JsonAsync(await Create(Item("D2391", null, 10)))).GetProperty("code").GetString());
        Assert.Equal("plan.invalid_price", (await JsonAsync(await Create(Item("D2391", 16, -5)))).GetProperty("code").GetString());
        Assert.Equal("plan.invalid_tooth", (await JsonAsync(await Create(Item("D2391", 99, 5)))).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Create()).StatusCode);

        var plan = await CreatePlanAsync(doctor, patient, Item("D2391", 16, 100m, 2, 10m), Item("D1110", null, 250m));
        var planId = plan.GetProperty("id").GetGuid();
        Assert.Equal(430.00m, plan.GetProperty("total").GetDecimal());   // 2×100×0.9 + 250
        Assert.Equal("draft", plan.GetProperty("status").GetString());
        var items = plan.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

        // Qəbul olunmamış planda prosedur icra olunmur
        var visitId = await StartVisitAsync(doctor, patient);
        Assert.Equal("plan.not_accepted", (await JsonAsync(await doctor.PostAsJsonAsync($"/v1/treatment-plans/{planId}/items/{items[0]}/perform", new { visitId }))).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await doctor.PostAsync($"/v1/treatment-plans/{planId}/accept", null)).StatusCode);   // əvvəl təklif olunmalıdır

        Assert.Equal("proposed", (await JsonAsync(await doctor.PostAsync($"/v1/treatment-plans/{planId}/propose", null))).GetProperty("status").GetString());
        Assert.Equal("accepted", (await JsonAsync(await doctor.PostAsync($"/v1/treatment-plans/{planId}/accept", null))).GetProperty("status").GetString());
        Assert.Equal(1, await Count("SELECT count(*) FROM outbox_messages WHERE type = 'TreatmentPlanAccepted' AND payload->>'planId' = $1", planId.ToString()));

        var first = await doctor.PostAsJsonAsync($"/v1/treatment-plans/{planId}/items/{items[0]}/perform", new { visitId });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var afterFirst = await JsonAsync(first);
        Assert.Equal("in_progress", afterFirst.GetProperty("status").GetString());

        // Billing üçün lazım olan hər şey hadisədədir: qiymət/endirim icra anında "donur"
        var payload = await f.Db.ScalarAsync<string>(PatientApiFixture.Demo, "SELECT payload::text FROM outbox_messages WHERE type = 'ProcedurePerformed' AND payload->>'planItemId' = $1", items[0].ToString());
        using var doc = JsonDocument.Parse(payload!);
        Assert.Equal("D2391", doc.RootElement.GetProperty("procedureCode").GetString());
        Assert.Equal(16, doc.RootElement.GetProperty("toothFdi").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("quantity").GetInt32());
        Assert.Equal(100m, doc.RootElement.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(10m, doc.RootElement.GetProperty("discountPercent").GetDecimal());
        Assert.Equal(visitId, doc.RootElement.GetProperty("visitId").GetGuid());
        Assert.Equal(doctorId, doc.RootElement.GetProperty("providerId").GetGuid());

        Assert.Equal("plan.item_not_pending", (await JsonAsync(await doctor.PostAsJsonAsync($"/v1/treatment-plans/{planId}/items/{items[0]}/perform", new { visitId }))).GetProperty("code").GetString());

        var last = await JsonAsync(await doctor.PostAsJsonAsync($"/v1/treatment-plans/{planId}/items/{items[1]}/perform", new { visitId }));
        Assert.Equal("completed", last.GetProperty("status").GetString());
        Assert.Equal(1, await Count("SELECT count(*) FROM outbox_messages WHERE type = 'TreatmentPlanCompleted' AND payload->>'planId' = $1", planId.ToString()));
        Assert.Equal(2, await Count("SELECT count(*) FROM outbox_messages WHERE type = 'ProcedurePerformed' AND payload->>'planId' = $1", planId.ToString()));

        var listed = (await JsonAsync(await doctor.GetAsync($"/v1/patients/{patient}/treatment-plans"))).EnumerateArray().ToList();
        Assert.Single(listed);
        Assert.Equal(HttpStatusCode.OK, (await doctor.GetAsync($"/v1/treatment-plans/{planId}")).StatusCode);
    }

    [IntegrationFact]
    public async Task Procedures_are_performed_only_within_the_doctors_own_open_visit()
    {
        using var staff = f.Client(Staff);
        var (doctor, _) = await DoctorAsync();
        var (colleague, _) = await DoctorAsync();
        using var d1 = doctor;
        using var d2 = colleague;
        var patient = await RegisterPatientAsync(staff, f.BranchA);
        var plan = await CreatePlanAsync(doctor, patient, Item("D2391", 16, 100m));
        var planId = plan.GetProperty("id").GetGuid();
        var itemId = plan.GetProperty("items")[0].GetProperty("id").GetGuid();
        await doctor.PostAsync($"/v1/treatment-plans/{planId}/propose", null);
        await doctor.PostAsync($"/v1/treatment-plans/{planId}/accept", null);
        var colleaguesVisit = await StartVisitAsync(colleague, patient);

        var foreign = await doctor.PostAsJsonAsync($"/v1/treatment-plans/{planId}/items/{itemId}/perform", new { visitId = colleaguesVisit });
        var ghostVisit = await doctor.PostAsJsonAsync($"/v1/treatment-plans/{planId}/items/{itemId}/perform", new { visitId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal("plan.not_your_visit", (await JsonAsync(foreign)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ghostVisit.StatusCode);
    }

    [IntegrationFact]
    public async Task Six_simultaneous_performs_of_the_same_item_produce_exactly_one_billable_event()
    {
        using var staff = f.Client(Staff);
        var (doctor, _) = await DoctorAsync();
        using var _ = doctor;
        var patient = await RegisterPatientAsync(staff, f.BranchA);
        var plan = await CreatePlanAsync(doctor, patient, Item("D2391", 16, 100m), Item("D2391", 17, 100m));
        var planId = plan.GetProperty("id").GetGuid();
        var itemId = plan.GetProperty("items")[0].GetProperty("id").GetGuid();
        await doctor.PostAsync($"/v1/treatment-plans/{planId}/propose", null);
        await doctor.PostAsync($"/v1/treatment-plans/{planId}/accept", null);
        var visitId = await StartVisitAsync(doctor, patient);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => doctor.PostAsJsonAsync($"/v1/treatment-plans/{planId}/items/{itemId}/perform", new { visitId })));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.PreconditionFailed }));
        Assert.Equal(1, await Count("SELECT count(*) FROM outbox_messages WHERE type = 'ProcedurePerformed' AND payload->>'planItemId' = $1", itemId.ToString()));   // ikiqat faktura yoxdur
    }

    [IntegrationFact]
    public async Task Cancelling_a_plan_keeps_performed_items_and_blocks_further_work()
    {
        using var staff = f.Client(Staff);
        var (doctor, _) = await DoctorAsync();
        using var _ = doctor;
        var patient = await RegisterPatientAsync(staff, f.BranchA);
        var plan = await CreatePlanAsync(doctor, patient, Item("D2391", 16, 100m), Item("D2391", 17, 100m));
        var planId = plan.GetProperty("id").GetGuid();
        var items = plan.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        await doctor.PostAsync($"/v1/treatment-plans/{planId}/propose", null);
        await doctor.PostAsync($"/v1/treatment-plans/{planId}/accept", null);
        var visitId = await StartVisitAsync(doctor, patient);
        await doctor.PostAsJsonAsync($"/v1/treatment-plans/{planId}/items/{items[0]}/perform", new { visitId });

        var cancelled = await JsonAsync(await doctor.PostAsync($"/v1/treatment-plans/{planId}/cancel", null));

        Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());
        var statuses = cancelled.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("status").GetString()).ToList();
        Assert.Equal(["done", "cancelled"], statuses);
        Assert.Equal(100m, cancelled.GetProperty("total").GetDecimal());   // ləğv olunan bənd cəmə daxil deyil
        Assert.Equal(HttpStatusCode.Conflict, (await doctor.PostAsJsonAsync($"/v1/treatment-plans/{planId}/items/{items[1]}/perform", new { visitId })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await doctor.PostAsync($"/v1/treatment-plans/{planId}/cancel", null)).StatusCode);
    }

    // ---------------- Prescriptions ----------------
    [IntegrationFact]
    public async Task Allergy_conflicts_block_prescriptions_unless_overridden_with_a_reason_and_are_audited()
    {
        using var staff = f.Client(Staff);
        var (doctor, doctorId) = await DoctorAsync();
        using var _ = doctor;
        var patient = await RegisterPatientAsync(staff, f.BranchA);
        await doctor.PostAsJsonAsync($"/v1/patients/{patient}/allergies", new { substance = "Penisillin", severity = "severe" });
        Task<HttpResponseMessage> Rx(object[] items, bool? @override = null, string? reason = null) =>
            doctor.PostAsJsonAsync("/v1/prescriptions", new { patientId = patient, items, overrideAllergyWarning = @override, overrideReason = reason });

        var blocked = await Rx([new { drug = "Amoksisillin 500 mq", dose = "1 tablet", frequency = "3x1", days = 7 }]);
        var shortReason = await Rx([new { drug = "Amoksisillin 500 mq", dose = "1 tablet" }], true, "lazımdır");
        var safe = await Rx([new { drug = "Paracetamol", dose = "500 mq", days = 3 }]);
        var overridden = await Rx([new { drug = "Amoksisillin 500 mq", dose = "1 tablet" }], true, "Digər antibiotiklərə davamlı infeksiya, pasiyent xəbərdar edildi");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        var problem = await JsonAsync(blocked);
        Assert.Equal("prescription.allergy_conflict", problem.GetProperty("code").GetString());
        var conflict = problem.GetProperty("conflicts")[0];
        Assert.Equal("Penisillin", conflict.GetProperty("allergen").GetString());
        Assert.Equal("severe", conflict.GetProperty("severity").GetString());
        Assert.Equal("prescription.override_reason_required", (await JsonAsync(shortReason)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, safe.StatusCode);
        Assert.True((await JsonAsync(safe)).GetProperty("allergyCheckPassed").GetBoolean());
        Assert.Equal(HttpStatusCode.Created, overridden.StatusCode);
        Assert.False((await JsonAsync(overridden)).GetProperty("allergyCheckPassed").GetBoolean());

        Assert.Equal(1, await Count("SELECT count(*) FROM audit_log WHERE action = 'prescription.allergy_override' AND user_id = $1", doctorId));
        Assert.Equal(1, await Count("SELECT count(*) FROM audit_log WHERE action = 'prescription.issue' AND user_id = $1", doctorId));
        var details = await f.Db.ScalarAsync<string>(PatientApiFixture.Demo, "SELECT coalesce(string_agg(after_data::text, ' '), '') FROM audit_log WHERE action LIKE 'prescription.%'");
        Assert.DoesNotContain("Amoksisillin", details!, StringComparison.Ordinal);   // dərman adları auditə düşmür
        Assert.Equal(2, (await JsonAsync(await doctor.GetAsync($"/v1/patients/{patient}/prescriptions"))).GetArrayLength());
        Assert.Equal("no-store", (await doctor.GetAsync($"/v1/patients/{patient}/prescriptions")).Headers.CacheControl!.ToString());
    }

    [IntegrationFact]
    public async Task Prescriptions_are_immutable_and_the_database_enforces_the_override_reason()
    {
        using var staff = f.Client(Staff);
        var (doctor, _) = await DoctorAsync();
        using var _ = doctor;
        var patient = await RegisterPatientAsync(staff, f.BranchA);
        await doctor.PostAsJsonAsync("/v1/prescriptions", new { patientId = patient, items = new[] { new { drug = "Paracetamol", dose = "500 mq" } } });

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => f.Db.ExecAsync(PatientApiFixture.Demo,
            "UPDATE prescriptions SET allergy_check_passed = false, override_reason = NULL WHERE patient_id = $1", patient));

        Assert.Contains("ck_rx_override", ex.Message, StringComparison.Ordinal);
    }

    [IntegrationFact]
    public async Task Receptionists_cannot_prescribe_and_invalid_prescriptions_are_rejected()
    {
        using var staff = f.Client(Staff);
        var (doctor, _) = await DoctorAsync();
        using var _ = doctor;
        var patient = await RegisterPatientAsync(staff, f.BranchA);

        var denied = await staff.PostAsJsonAsync("/v1/prescriptions", new { patientId = patient, items = new[] { new { drug = "Paracetamol", dose = "500 mq" } } });
        var empty = await doctor.PostAsJsonAsync("/v1/prescriptions", new { patientId = patient, items = Array.Empty<object>() });
        var noDose = await doctor.PostAsJsonAsync("/v1/prescriptions", new { patientId = patient, items = new[] { new { drug = "Paracetamol", dose = "" } } });

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal("prescription.invalid_item", (await JsonAsync(noDose)).GetProperty("code").GetString());
    }

    // ---------------- Clinical notes ----------------
    [IntegrationFact]
    public async Task Notes_are_drafts_until_signed_then_immutable_with_addenda()
    {
        using var staff = f.Client(Staff);
        var (doctor, doctorId) = await DoctorAsync();
        var (colleague, _) = await DoctorAsync();
        using var d1 = doctor;
        using var d2 = colleague;
        var patient = await RegisterPatientAsync(staff, f.BranchA);

        var created = await doctor.PostAsJsonAsync("/v1/clinical-notes", new { patientId = patient, subjective = "Ağrı soyuqdan", objective = "36 kariyes", assessment = "Dərin kariyes", plan = "Plomb" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var noteId = (await JsonAsync(created)).GetProperty("id").GetGuid();

        var edited = await doctor.PutAsJsonAsync($"/v1/clinical-notes/{noteId}", new { subjective = "Ağrı soyuqdan və şirindən", objective = "36 kariyes", assessment = "Dərin kariyes", plan = "Plomb" });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        // Qaralama yalnız müəllifə görünür və yalnız o dəyişə/imzalaya bilər
        Assert.Single((await JsonAsync(await doctor.GetAsync($"/v1/patients/{patient}/clinical-notes"))).EnumerateArray());
        Assert.Empty((await JsonAsync(await colleague.GetAsync($"/v1/patients/{patient}/clinical-notes"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await colleague.PutAsJsonAsync($"/v1/clinical-notes/{noteId}", new { subjective = "oğurluq" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await colleague.PostAsync($"/v1/clinical-notes/{noteId}/sign", null)).StatusCode);

        // İmzalanmamış qeydə addendum yazmaq olmaz
        Assert.Equal("note.original_not_signed", (await JsonAsync(await doctor.PostAsJsonAsync("/v1/clinical-notes", new { patientId = patient, subjective = "əlavə", addendumOf = noteId }))).GetProperty("code").GetString());

        var signed = await doctor.PostAsync($"/v1/clinical-notes/{noteId}/sign", null);
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        Assert.NotEqual(JsonValueKind.Null, (await JsonAsync(signed)).GetProperty("signedAt").ValueKind);
        Assert.Equal(1, await Count("SELECT count(*) FROM outbox_messages WHERE type = 'ClinicalNoteSigned' AND payload->>'noteId' = $1", noteId.ToString()));

        // İmzadan sonra: tətbiq də, DB də dəyişməyə icazə vermir
        var tooLate = await doctor.PutAsJsonAsync($"/v1/clinical-notes/{noteId}", new { subjective = "sonradan dəyişmə" });
        Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);
        Assert.Equal("clinical.note_signed", (await JsonAsync(tooLate)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await doctor.PostAsync($"/v1/clinical-notes/{noteId}/sign", null)).StatusCode);
        var dbEx = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => f.Db.ExecAsync(PatientApiFixture.Demo, "UPDATE clinical_notes SET subjective = 'hack' WHERE id = $1", noteId));
        Assert.Contains("immutable", dbEx.Message, StringComparison.Ordinal);

        // Düzəliş = addendum; imzalanmış qeyd isə həmkarlara görünür
        var addendum = await doctor.PostAsJsonAsync("/v1/clinical-notes", new { patientId = patient, subjective = "Düzəliş: 46 yox, 36", addendumOf = noteId });
        Assert.Equal(HttpStatusCode.Created, addendum.StatusCode);
        Assert.Equal(noteId, (await JsonAsync(addendum)).GetProperty("addendumOf").GetGuid());
        var seenByColleague = (await JsonAsync(await colleague.GetAsync($"/v1/patients/{patient}/clinical-notes"))).EnumerateArray().ToList();
        Assert.Single(seenByColleague);
        Assert.Equal(noteId, seenByColleague[0].GetProperty("id").GetGuid());
        Assert.Equal(1, await Count("SELECT count(*) FROM audit_log WHERE action = 'note.sign' AND user_id = $1", doctorId));
    }

    [IntegrationFact]
    public async Task Empty_or_oversized_notes_are_rejected()
    {
        using var staff = f.Client(Staff);
        var (doctor, _) = await DoctorAsync();
        using var _ = doctor;
        var patient = await RegisterPatientAsync(staff, f.BranchA);

        var empty = await doctor.PostAsJsonAsync("/v1/clinical-notes", new { patientId = patient, subjective = "  " });
        var huge = await doctor.PostAsJsonAsync("/v1/clinical-notes", new { patientId = patient, subjective = new string('x', 5001) });
        var badSource = await doctor.PostAsJsonAsync("/v1/clinical-notes", new { patientId = patient, subjective = "x", source = "telepathy" });

        Assert.Equal("note.empty", (await JsonAsync(empty)).GetProperty("code").GetString());
        Assert.Equal("note.too_long", (await JsonAsync(huge)).GetProperty("code").GetString());
        Assert.Equal("note.invalid_source", (await JsonAsync(badSource)).GetProperty("code").GetString());
    }

    // ---------------- Access / scope / audit ----------------
    [IntegrationFact]
    public async Task Clinical_data_follows_the_care_relationship_for_doctors_with_own_scope()
    {
        using var staff = f.Client(Staff);
        var (outsider, _) = await DoctorAsync(["clinical:read@own", "clinical:write@own", "prescription:write@own"]);
        var treatingId = await f.SeedProviderAsync();
        using var treating = f.Client(["clinical:read@own", "clinical:write@own", "prescription:write@own"], userId: treatingId);
        using var o = outsider;
        var patient = await RegisterPatientAsync(staff, f.BranchA);
        var start = DateTimeOffset.UtcNow.AddHours(5);
        await staff.PostAsJsonAsync("/v1/appointments", new { branchId = f.BranchA, patientId = patient, providerId = treatingId, start, end = start.AddMinutes(30) });

        // Əlaqəsi olmayan həkim üçün pasiyentin klinik məlumatı mövcud deyil
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/v1/patients/{patient}/odontogram")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync("/v1/visits", new { patientId = patient })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync("/v1/prescriptions", new { patientId = patient, items = new[] { new { drug = "Paracetamol", dose = "500 mq" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/v1/patients/{patient}/clinical-notes")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await treating.GetAsync($"/v1/patients/{patient}/odontogram")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await treating.PostAsJsonAsync("/v1/visits", new { patientId = patient })).StatusCode);
    }

    [IntegrationFact]
    public async Task Staff_without_clinical_permissions_and_other_tenants_are_locked_out()
    {
        using var reception = f.Client(["patient:read@tenant", "patient:write@tenant", "appointment:read@tenant", "appointment:write@tenant"]);
        var (doctor, _) = await DoctorAsync();
        using var d = doctor;
        var patient = await RegisterPatientAsync(reception, f.BranchA);
        using var otherTenantDoctor = f.Client(DoctorPerms, tenant: "other");

        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync($"/v1/patients/{patient}/odontogram")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PostAsJsonAsync("/v1/visits", new { patientId = patient })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync("/v1/procedure-codes")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherTenantDoctor.GetAsync($"/v1/patients/{patient}/odontogram")).StatusCode);
        using var anonymous = f.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/v1/patients/{patient}/odontogram")).StatusCode);
    }

    [IntegrationFact]
    public async Task Clinical_reads_and_writes_are_audited_and_the_chain_stays_intact()
    {
        using var staff = f.Client(Staff);
        var (doctor, doctorId) = await DoctorAsync();
        using var _ = doctor;
        var patient = await RegisterPatientAsync(staff, f.BranchA);
        var visitId = await StartVisitAsync(doctor, patient);
        await doctor.PostAsJsonAsync($"/v1/patients/{patient}/odontogram", new { toothFdi = 16, condition = "caries", notes = "Gizli klinik şərh" });
        await doctor.GetAsync($"/v1/patients/{patient}/odontogram");
        await doctor.PostAsync($"/v1/visits/{visitId}/close", null);

        foreach (var action in new[] { "visit.start", "odontogram.record", "odontogram.read", "visit.close" })
        {
            Assert.Equal(1, await Count("SELECT count(*) FROM audit_log WHERE action = $1 AND user_id = $2", action, doctorId));
        }

        var details = await f.Db.ScalarAsync<string>(PatientApiFixture.Demo, "SELECT coalesce(string_agg(after_data::text, ' '), '') FROM audit_log WHERE user_id = $1", doctorId);
        Assert.DoesNotContain("Gizli klinik şərh", details!, StringComparison.Ordinal);
        Assert.True((await f.VerifyAuditChainAsync(PatientApiFixture.Demo, "demo")).IsIntact);
    }
}
