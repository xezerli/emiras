using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DentaCore.TestSupport;

namespace DentaCore.Patient.IntegrationTests;

public sealed class PatientApiTests(PatientApiFixture f) : IClassFixture<PatientApiFixture>
{
    private static string Unique() => "Zq" + Guid.NewGuid().ToString("N")[..8];

    private static string NewPhone() => "+99450" + Random.Shared.Next(1_000_000, 9_999_999);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> RegisterAsync(HttpClient client, Guid branch, string? last = null, string? phone = null, string? nid = null, bool confirm = false, string first = "Aysel")
    {
        var response = await client.PostAsJsonAsync($"/v1/patients?confirmDuplicate={confirm.ToString().ToLowerInvariant()}", new
        {
            branchId = branch, firstName = first, lastName = last ?? Unique(), phone = phone ?? NewPhone(), nationalId = nid, birthDate = "1990-05-01", gender = "F",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonAsync(response);
    }

    private static HttpRequestMessage Patch(Guid id, string json, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/v1/patients/{id}") { Content = new StringContent(json, Encoding.UTF8, "application/merge-patch+json") };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    private Task<long> AuditCount(string action, Guid? userId = null) =>
        f.Db.ScalarAsync<long>(PatientApiFixture.Demo, "SELECT count(*) FROM audit_log WHERE action = $1 AND ($2::uuid IS NULL OR user_id = $2)", action, (object?)userId ?? DBNull.Value)!;

    // ---------------- Register ----------------
    [IntegrationFact]
    public async Task Register_returns_masked_201_and_stores_encrypted_phone_outbox_event_and_audit()
    {
        using var client = f.Client(Perms.All, [f.BranchA]);
        var phone = NewPhone();
        var last = Unique();

        var response = await client.PostAsJsonAsync("/v1/patients", new { branchId = f.BranchA, firstName = "Aysel", lastName = last, phone, email = "Aysel@Clinic.az", nationalId = "aze" + Random.Shared.Next(1000000, 9999999) });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await JsonAsync(response);
        var id = json.GetProperty("id").GetGuid();
        Assert.Equal($"/v1/patients/{id}", response.Headers.Location!.OriginalString);
        Assert.True(json.GetProperty("chartNo").GetInt64() > 0);
        Assert.Equal(1, json.GetProperty("rowVersion").GetInt32());
        Assert.Contains("*", json.GetProperty("phone").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(phone[4..], await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var enc = await f.Db.ScalarAsync<byte[]>(PatientApiFixture.Demo, "SELECT phone_enc FROM patients WHERE id = $1", id);
        Assert.DoesNotContain(phone[4..], Encoding.UTF8.GetString(enc!), StringComparison.Ordinal);
        Assert.Equal(32, (await f.Db.ScalarAsync<byte[]>(PatientApiFixture.Demo, "SELECT phone_hash FROM patients WHERE id = $1", id))!.Length);
        Assert.Equal(1, await f.Db.ScalarAsync<long>(PatientApiFixture.Demo, "SELECT count(*) FROM outbox_messages WHERE type = 'PatientRegistered' AND payload->>'patientId' = $1", id.ToString()));
        Assert.Equal(1, await f.Db.ScalarAsync<long>(PatientApiFixture.Demo, "SELECT count(*) FROM audit_log WHERE action = 'patient.create' AND entity_id = $1 AND user_id = $2", id, f.Staff));
    }

    [IntegrationFact]
    public async Task Same_phone_in_another_format_is_a_409_with_existing_id_unless_confirmed()
    {
        using var client = f.Client(Perms.All);
        var created = await RegisterAsync(client, f.BranchA, phone: "+994551234567");

        var local = await client.PostAsJsonAsync("/v1/patients", new { branchId = f.BranchA, firstName = "Elnur", lastName = Unique(), phone = "055 123 45 67" });
        var confirmed = await client.PostAsJsonAsync("/v1/patients?confirmDuplicate=true", new { branchId = f.BranchA, firstName = "Elnur", lastName = Unique(), phone = "055 123 45 67" });

        Assert.Equal(HttpStatusCode.Conflict, local.StatusCode);
        var problem = await JsonAsync(local);
        Assert.Equal("patient.duplicate", problem.GetProperty("code").GetString());
        Assert.Equal(created.GetProperty("id").GetGuid(), problem.GetProperty("existingPatientId").GetGuid());
        Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
    }

    [IntegrationFact]
    public async Task Invalid_input_returns_422()
    {
        using var client = f.Client(Perms.All);

        var noName = await client.PostAsJsonAsync("/v1/patients", new { branchId = f.BranchA, firstName = "", lastName = "X" });
        var badPhone = await client.PostAsJsonAsync("/v1/patients", new { branchId = f.BranchA, firstName = "A", lastName = "B", phone = "12" });
        var future = await client.PostAsJsonAsync("/v1/patients", new { branchId = f.BranchA, firstName = "A", lastName = "B", birthDate = "2999-01-01" });
        var badEmail = await client.PostAsJsonAsync("/v1/patients", new { branchId = f.BranchA, firstName = "A", lastName = "B", email = "nope" });

        foreach (var r in new[] { noName, badPhone, future, badEmail })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        }

        Assert.Equal("patient.invalid_phone", (await JsonAsync(badPhone)).GetProperty("code").GetString());
        Assert.Equal("patient.invalid_birth_date", (await JsonAsync(future)).GetProperty("code").GetString());
    }

    // ---------------- Authentication / authorization ----------------
    [IntegrationFact]
    public async Task Anonymous_is_401_and_missing_permission_is_403_and_audited()
    {
        using var anonymous = f.Anonymous();
        using var readOnly = f.Client(["patient:read@tenant"], userId: f.OtherStaff);
        var before = await AuditCount("permission.denied", f.OtherStaff);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/patients")).StatusCode);
        var denied = await readOnly.PostAsJsonAsync("/v1/patients", new { branchId = f.BranchA, firstName = "A", lastName = "B" });

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("permission.denied", (await JsonAsync(denied)).GetProperty("code").GetString());
        Assert.Equal(before + 1, await AuditCount("permission.denied", f.OtherStaff));
    }

    [IntegrationFact]
    public async Task Registering_into_a_branch_outside_scope_is_403()
    {
        using var branchA = f.Client(["patient:write@branch"], [f.BranchA]);

        var response = await branchA.PostAsJsonAsync("/v1/patients", new { branchId = f.BranchB, firstName = "A", lastName = "B" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [IntegrationFact]
    public async Task Branch_scope_hides_other_branches_everywhere()
    {
        using var admin = f.Client(Perms.All);
        using var branchA = f.Client(Perms.All.Select(p => p.Replace("@tenant", "@branch", StringComparison.Ordinal)).ToArray(), [f.BranchA]);
        var last = Unique();
        var inB = (await RegisterAsync(admin, f.BranchB, last)).GetProperty("id").GetGuid();
        var inA = (await RegisterAsync(admin, f.BranchA, last)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await branchA.GetAsync($"/v1/patients/{inB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await branchA.SendAsync(Patch(inB, """{"firstName":"X"}""", "1"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await branchA.DeleteAsync($"/v1/patients/{inB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await branchA.GetAsync($"/v1/patients/{inB}/medical-profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await branchA.GetAsync($"/v1/patients/{inA}")).StatusCode);

        var ids = (await JsonAsync(await branchA.GetAsync($"/v1/patients?q={last}"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([inA], ids);
    }

    [IntegrationFact]
    public async Task Own_scope_sees_only_patients_the_user_registered()
    {
        using var mine = f.Client(["patient:read@own", "patient:write@own"], userId: f.OtherStaff);
        using var admin = f.Client(Perms.All);
        var last = Unique();
        var own = (await RegisterAsync(mine, f.BranchA, last)).GetProperty("id").GetGuid();
        var foreign = (await RegisterAsync(admin, f.BranchA, last)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await mine.GetAsync($"/v1/patients/{own}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mine.GetAsync($"/v1/patients/{foreign}")).StatusCode);
        var ids = (await JsonAsync(await mine.GetAsync($"/v1/patients?q={last}"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([own], ids);
    }

    // ---------------- Read / reveal / audit ----------------
    [IntegrationFact]
    public async Task Get_masks_by_default_reveal_needs_permission_and_both_are_audited_separately()
    {
        using var admin = f.Client(Perms.All);
        using var plain = f.Client(["patient:read@tenant"], userId: f.OtherStaff);
        var phone = NewPhone();
        var id = (await RegisterAsync(admin, f.BranchA, phone: phone)).GetProperty("id").GetGuid();

        var masked = await plain.GetAsync($"/v1/patients/{id}");
        var denied = await plain.GetAsync($"/v1/patients/{id}?reveal=true");
        var beforeSensitive = await AuditCount("patient.read.sensitive", f.Staff);
        var revealed = await admin.GetAsync($"/v1/patients/{id}?reveal=true");

        Assert.Equal(HttpStatusCode.OK, masked.StatusCode);
        Assert.Equal("\"1\"", masked.Headers.ETag!.Tag);
        Assert.Equal("no-store", masked.Headers.CacheControl!.ToString());
        Assert.NotEqual(phone, (await JsonAsync(masked)).GetProperty("phone").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(phone, (await JsonAsync(revealed)).GetProperty("phone").GetString());
        Assert.Equal(beforeSensitive + 1, await AuditCount("patient.read.sensitive", f.Staff));
    }

    [IntegrationFact]
    public async Task Every_read_is_audited_exactly_once_and_search_text_never_reaches_the_audit_log()
    {
        using var admin = f.Client(Perms.All);
        var last = Unique();
        var id = (await RegisterAsync(admin, f.BranchA, last)).GetProperty("id").GetGuid();
        var reads = await AuditCount("patient.read", f.Staff);
        var searches = await AuditCount("patient.search", f.Staff);

        await admin.GetAsync($"/v1/patients/{id}");
        await admin.GetAsync($"/v1/patients/{id}");
        await admin.GetAsync($"/v1/patients?q={last}");

        Assert.Equal(reads + 2, await AuditCount("patient.read", f.Staff));
        Assert.Equal(searches + 1, await AuditCount("patient.search", f.Staff));
        var details = await f.Db.ScalarAsync<string>(PatientApiFixture.Demo, "SELECT after_data::text FROM audit_log WHERE action = 'patient.search' ORDER BY id DESC LIMIT 1");
        Assert.DoesNotContain(last, details!, StringComparison.Ordinal);
    }

    [IntegrationFact]
    public async Task Audit_chain_is_intact_and_tampering_is_detected()
    {
        using var admin = f.Client(Perms.All);
        var id = (await RegisterAsync(admin, f.BranchA)).GetProperty("id").GetGuid();
        await admin.GetAsync($"/v1/patients/{id}");

        var before = await f.VerifyAuditChainAsync(PatientApiFixture.Demo, "demo");
        Assert.True(before.IsIntact);
        Assert.True(before.EntriesChecked >= 2);

        // Superuser audit trigger-ini söndürüb bir qeydi dəyişir (təşkilat daxili hücumçunun ssenarisi)
        var victim = await f.Db.ScalarAsync<long>(PatientApiFixture.Demo, "SELECT id FROM audit_log WHERE action = 'patient.create' ORDER BY id DESC LIMIT 1");
        await f.Db.ExecAsync(PatientApiFixture.Demo, "ALTER TABLE audit_log DISABLE TRIGGER audit_no_update");
        try
        {
            await f.Db.ExecAsync(PatientApiFixture.Demo, "UPDATE audit_log SET action = 'patient.read' WHERE id = $1", victim);
            var after = await f.VerifyAuditChainAsync(PatientApiFixture.Demo, "demo");

            Assert.False(after.IsIntact);
            Assert.Equal(victim, after.FirstBrokenId);
        }
        finally
        {
            await f.Db.ExecAsync(PatientApiFixture.Demo, "UPDATE audit_log SET action = 'patient.create' WHERE id = $1", victim);
            await f.Db.ExecAsync(PatientApiFixture.Demo, "ALTER TABLE audit_log ENABLE TRIGGER audit_no_update");
        }

        Assert.True((await f.VerifyAuditChainAsync(PatientApiFixture.Demo, "demo")).IsIntact);
    }

    [IntegrationFact]
    public async Task Audit_log_rejects_updates_and_deletes_at_database_level()
    {
        using var admin = f.Client(Perms.All);
        await RegisterAsync(admin, f.BranchA);

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            f.Db.ExecAsync(PatientApiFixture.Demo, "DELETE FROM audit_log WHERE action = 'patient.create'"));

        Assert.Contains("append-only", ex.Message, StringComparison.Ordinal);
    }

    // ---------------- Search ----------------
    [IntegrationFact]
    public async Task Search_finds_by_name_in_any_order_phone_in_any_format_chart_number_and_national_id()
    {
        using var admin = f.Client(Perms.All);
        var last = Unique();
        var nid = "AZE" + Random.Shared.Next(1000000, 9999999);
        var created = await RegisterAsync(admin, f.BranchA, last, phone: "+994701234567", nid: nid, first: "Nərmin");
        var id = created.GetProperty("id").GetGuid();
        var chartNo = created.GetProperty("chartNo").GetInt64();

        async Task<List<Guid>> Find(string q) =>
            (await JsonAsync(await admin.GetAsync($"/v1/patients?q={Uri.EscapeDataString(q)}"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

        Assert.Contains(id, await Find(last));
        Assert.Contains(id, await Find(last.ToLowerInvariant()));          // böyük/kiçik hərf fərqi yoxdur
        Assert.Contains(id, await Find($"Nərmin {last}"));
        Assert.Contains(id, await Find($"{last} Nərmin"));                   // sıra fərqi yoxdur
        Assert.Contains(id, await Find(last[..5]));                          // hissə
        Assert.Contains(id, await Find("070 123 45 67"));                    // yerli format
        Assert.Contains(id, await Find("+994 70 123-45-67"));
        Assert.Contains(id, await Find(chartNo.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(6, '0')));
        Assert.Contains(id, await Find(nid.ToLowerInvariant()));
        Assert.DoesNotContain(id, await Find(last + "nomatch"));
    }

    [IntegrationFact]
    public async Task Like_wildcards_in_the_query_are_literals()
    {
        using var admin = f.Client(Perms.All);
        await RegisterAsync(admin, f.BranchA);

        foreach (var q in new[] { "%", "_", "%%", "\\", "a%b" })
        {
            var json = await JsonAsync(await admin.GetAsync($"/v1/patients?q={Uri.EscapeDataString(q)}"));
            Assert.Empty(json.GetProperty("items").EnumerateArray());
        }
    }

    [IntegrationFact]
    public async Task Search_pages_newest_first_without_gaps_or_duplicates()
    {
        using var admin = f.Client(Perms.All);
        var last = Unique();
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            created.Add((await RegisterAsync(admin, f.BranchA, last)).GetProperty("id").GetGuid());
        }

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"/v1/patients?q={last}&limit=2" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var json = await JsonAsync(await admin.GetAsync(url));
            seen.AddRange(json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            cursor = json.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(created.AsEnumerable().Reverse(), seen);   // ən yeni əvvəl
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.GetAsync("/v1/patients?cursor=garbage!!&limit=2")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.GetAsync("/v1/patients?limit=101")).StatusCode);
    }

    // ---------------- Update / concurrency ----------------
    [IntegrationFact]
    public async Task Patch_updates_with_if_match_bumps_version_and_audits_only_field_names()
    {
        using var admin = f.Client(Perms.All);
        var created = await RegisterAsync(admin, f.BranchA);
        var id = created.GetProperty("id").GetGuid();
        var newLast = Unique();

        var response = await admin.SendAsync(Patch(id, $$"""{"lastName":"{{newLast}}","fatherName":null,"phone":"0771234567"}""", "\"1\""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal(newLast, json.GetProperty("lastName").GetString());
        Assert.Equal(2, json.GetProperty("rowVersion").GetInt32());
        Assert.Equal("\"2\"", response.Headers.ETag!.Tag);
        var audit = await f.Db.ScalarAsync<string>(PatientApiFixture.Demo, "SELECT after_data::text FROM audit_log WHERE action = 'patient.update' AND entity_id = $1", id);
        Assert.Contains("lastName", audit!, StringComparison.Ordinal);
        Assert.DoesNotContain(newLast, audit!, StringComparison.Ordinal);   // dəyər yox, yalnız sahə adı
        Assert.DoesNotContain("0771234567", audit!, StringComparison.Ordinal);
        Assert.Equal(1, await f.Db.ScalarAsync<long>(PatientApiFixture.Demo, "SELECT count(*) FROM outbox_messages WHERE type = 'PatientUpdated' AND payload->>'patientId' = $1", id.ToString()));
    }

    [IntegrationFact]
    public async Task Patch_requires_if_match_rejects_stale_versions_and_bad_bodies()
    {
        using var admin = f.Client(Perms.All);
        var id = (await RegisterAsync(admin, f.BranchA)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.PreconditionRequired, (await admin.SendAsync(Patch(id, """{"firstName":"X"}""", null))).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await admin.SendAsync(Patch(id, """{"firstName":"X"}""", "\"7\""))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.SendAsync(Patch(id, """{"chartNo":1}""", "1"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.SendAsync(Patch(id, """{"birthDate":"3000-01-01"}""", "1"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(Patch(id, "not json", "1"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(Patch(id, "{}", "1"))).StatusCode);
        Assert.Equal(1, await f.Db.ScalarAsync<int>(PatientApiFixture.Demo, "SELECT row_version FROM patients WHERE id = $1", id));
    }

    [IntegrationFact]
    public async Task Two_simultaneous_patches_with_the_same_version_cannot_both_win()
    {
        using var admin = f.Client(Perms.All);
        var id = (await RegisterAsync(admin, f.BranchA)).GetProperty("id").GetGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => admin.SendAsync(Patch(id, $$"""{"notes":"writer {{i}}"}""", "1"))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.PreconditionFailed, r.StatusCode));
        Assert.Equal(2, await f.Db.ScalarAsync<int>(PatientApiFixture.Demo, "SELECT row_version FROM patients WHERE id = $1", id));
    }

    [IntegrationFact]
    public async Task Changing_phone_to_another_patients_number_conflicts()
    {
        using var admin = f.Client(Perms.All);
        var other = NewPhone();
        await RegisterAsync(admin, f.BranchA, phone: other);
        var id = (await RegisterAsync(admin, f.BranchA)).GetProperty("id").GetGuid();

        var response = await admin.SendAsync(Patch(id, $$"""{"phone":"{{other}}"}""", "1"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ---------------- Delete ----------------
    [IntegrationFact]
    public async Task Delete_is_soft_hides_the_patient_everywhere_and_keeps_the_row()
    {
        using var admin = f.Client(Perms.All);
        var last = Unique();
        var id = (await RegisterAsync(admin, f.BranchA, last)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1/patients/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/v1/patients/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/v1/patients/{id}")).StatusCode);
        Assert.Empty((await JsonAsync(await admin.GetAsync($"/v1/patients?q={last}"))).GetProperty("items").EnumerateArray());
        Assert.NotNull(await f.Db.ScalarAsync<DateTime?>(PatientApiFixture.Demo, "SELECT deleted_at FROM patients WHERE id = $1", id));
        Assert.Equal(1, await f.Db.ScalarAsync<long>(PatientApiFixture.Demo, "SELECT count(*) FROM outbox_messages WHERE type = 'PatientDeleted' AND payload->>'patientId' = $1", id.ToString()));
    }

    // ---------------- Medical profile ----------------
    [IntegrationFact]
    public async Task Allergies_flow_into_medical_profile_and_the_severe_flag()
    {
        using var admin = f.Client(Perms.All);
        using var receptionist = f.Client(["patient:read@tenant"], userId: f.OtherStaff);
        var id = (await RegisterAsync(admin, f.BranchA)).GetProperty("id").GetGuid();
        Assert.False((await JsonAsync(await admin.GetAsync($"/v1/patients/{id}"))).GetProperty("hasSevereAllergy").GetBoolean());

        var denied = await receptionist.PostAsJsonAsync($"/v1/patients/{id}/allergies", new { substance = "Penisillin", severity = "severe" });
        var mild = await admin.PostAsJsonAsync($"/v1/patients/{id}/allergies", new { substance = "Lateks", reaction = "səpgi", severity = "mild" });
        var severe = await admin.PostAsJsonAsync($"/v1/patients/{id}/allergies", new { substance = "Penisillin", reaction = "anafilaksiya", severity = "severe" });
        var invalid = await admin.PostAsJsonAsync($"/v1/patients/{id}/allergies", new { substance = "X", severity = "deadly" });

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Created, mild.StatusCode);
        Assert.Equal(HttpStatusCode.Created, severe.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);

        var profile = await JsonAsync(await admin.GetAsync($"/v1/patients/{id}/medical-profile"));
        Assert.Equal(["Lateks", "Penisillin"], profile.GetProperty("allergies").EnumerateArray().Select(a => a.GetProperty("substance").GetString()!).Order().ToArray());
        Assert.True((await JsonAsync(await admin.GetAsync($"/v1/patients/{id}"))).GetProperty("hasSevereAllergy").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await receptionist.GetAsync($"/v1/patients/{id}/medical-profile")).StatusCode);

        var profileReads = await AuditCount("patient.medical_profile.read", f.Staff);
        await admin.GetAsync($"/v1/patients/{id}/medical-profile");
        Assert.Equal(profileReads + 1, await AuditCount("patient.medical_profile.read", f.Staff));
    }

    // ---------------- Tenant isolation ----------------
    [IntegrationFact]
    public async Task Tenants_are_isolated_and_tokens_do_not_cross_over()
    {
        using var demo = f.Client(Perms.All);
        using var other = f.Client(Perms.All, tenant: "other");
        var last = Unique();
        var id = (await RegisterAsync(demo, f.BranchA, last)).GetProperty("id").GetGuid();

        // Başqa klinikanın öz tokeni: pasiyenti görmür
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1/patients/{id}")).StatusCode);
        Assert.Empty((await JsonAsync(await other.GetAsync($"/v1/patients?q={last}"))).GetProperty("items").EnumerateArray());

        // Demo klinikasının tokeni başqa klinikaya qarşı işləmir
        using var crossed = f.Factory!.CreateClientFor("other");
        crossed.DefaultRequestHeaders.Authorization = demo.DefaultRequestHeaders.Authorization;
        var response = await crossed.GetAsync("/v1/patients");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenant.mismatch", (await JsonAsync(response)).GetProperty("code").GetString());

        Assert.Equal(0, await f.Db.ScalarAsync<long>(PatientApiFixture.Other, "SELECT count(*) FROM patients WHERE last_name = $1", last));
    }
}
