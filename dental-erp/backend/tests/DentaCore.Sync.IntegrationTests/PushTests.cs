using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DentaCore.TestSupport;
using static DentaCore.Sync.IntegrationTests.SyncFixture;

namespace DentaCore.Sync.IntegrationTests;

[Collection(SyncDefinition.Name)]
public sealed class PushTests(SyncFixture f)
{
    private static readonly string[] Clinician = ["patient:read@tenant", "clinical:read@tenant", "clinical:write@tenant"];

    private static object Create(Guid noteId, Guid patient, string subjective, Guid? change = null, string? source = null) => new
    {
        entity = "clinical_notes", entityId = noteId, op = "I", clientChangeId = change ?? Guid.NewGuid(),
        data = new { patientId = patient, subjective, objective = "O", assessment = "A", plan = "P", source },
    };

    private static object Update(Guid noteId, int baseVersion, string subjective, Guid? change = null) => new
    {
        entity = "clinical_notes", entityId = noteId, op = "U", baseVersion, clientChangeId = change ?? Guid.NewGuid(),
        data = new { subjective, objective = "O", assessment = "A", plan = "P" },
    };

    private async Task<(HttpClient Client, Guid User, Guid Patient, Guid Device)> SetupAsync()
    {
        var doctor = await f.DoctorAsync();
        var branch = await f.BranchAsync();
        return (f.Client(Clinician, doctor, [branch]), doctor, await f.PatientAsync(branch), Guid.NewGuid());
    }

    private static async Task<JsonElement> PushOkAsync(HttpClient client, Guid device, params object[] changes)
    {
        var response = await PushAsync(client, device, changes);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private Task<long> NoteCount(Guid id) => f.Db.ScalarAsync<long>(SyncFixture.Demo, "SELECT count(*) FROM clinical_notes WHERE id = $1", id)!;

    // ---------------- Create / update ----------------
    [IntegrationFact]
    public async Task Offline_created_note_is_applied_with_the_clients_id_and_the_real_row_version()
    {
        var (client, user, patient, device) = await SetupAsync();
        var note = Guid.NewGuid();

        var result = await PushOkAsync(client, device, Create(note, patient, "Soyuğa həssaslıq"));

        var applied = Assert.Single(result.GetProperty("applied").EnumerateArray());
        Assert.Equal(note, applied.GetProperty("entityId").GetGuid());
        Assert.Equal(1, applied.GetProperty("rowVersion").GetInt32());   // DB-nin təyin etdiyi, 0 yox
        Assert.Equal(user, await f.Db.ScalarAsync<Guid>(SyncFixture.Demo, "SELECT author_id FROM clinical_notes WHERE id = $1", note));
        Assert.Equal("Soyuğa həssaslıq", await f.Db.ScalarAsync<string>(SyncFixture.Demo, "SELECT subjective FROM clinical_notes WHERE id = $1", note));
    }

    [IntegrationFact]
    public async Task Replaying_the_same_push_after_a_lost_response_changes_nothing_and_returns_the_same_result()
    {
        var (client, _, patient, device) = await SetupAsync();
        var note = Guid.NewGuid();
        var change = Guid.NewGuid();
        var first = await PushOkAsync(client, device, Create(note, patient, "bir", change));

        var replay = await PushOkAsync(client, device, Create(note, patient, "bir", change));

        Assert.Equal(first.GetProperty("applied")[0].GetProperty("rowVersion").GetInt32(), replay.GetProperty("applied")[0].GetProperty("rowVersion").GetInt32());
        Assert.Equal(1, await NoteCount(note));
        Assert.Equal(1, await f.Db.ScalarAsync<long>(SyncFixture.Demo, "SELECT count(*) FROM sync_client_changes WHERE client_change_id = $1", change));
    }

    [IntegrationFact]
    public async Task Creating_the_same_note_id_again_under_a_new_change_id_is_still_idempotent()
    {
        // Klient əvvəlki push-un nəticəsini almayıb və yeni clientChangeId ilə təkrar göndərir (məs. tətbiq yenidən quraşdırılıb)
        var (client, _, patient, device) = await SetupAsync();
        var note = Guid.NewGuid();
        await PushOkAsync(client, device, Create(note, patient, "bir"));

        var again = await PushOkAsync(client, device, Create(note, patient, "bir"));

        Assert.Single(again.GetProperty("applied").EnumerateArray());
        Assert.Equal(1, await NoteCount(note));
    }

    [IntegrationFact]
    public async Task Update_with_the_current_base_version_is_applied_and_returns_the_new_version()
    {
        var (client, _, patient, device) = await SetupAsync();
        var note = Guid.NewGuid();
        await PushOkAsync(client, device, Create(note, patient, "bir"));

        var result = await PushOkAsync(client, device, Update(note, 1, "iki"));

        Assert.Equal(2, result.GetProperty("applied")[0].GetProperty("rowVersion").GetInt32());
        Assert.Equal("iki", await f.Db.ScalarAsync<string>(SyncFixture.Demo, "SELECT subjective FROM clinical_notes WHERE id = $1", note));
    }

    [IntegrationFact]
    public async Task Update_based_on_a_stale_version_is_a_conflict_that_carries_the_servers_state()
    {
        var (client, _, patient, device) = await SetupAsync();
        var note = Guid.NewGuid();
        await PushOkAsync(client, device, Create(note, patient, "bir"));
        await PushOkAsync(client, device, Update(note, 1, "başqa cihazdan"));   // server artıq 2-dir

        var result = await PushOkAsync(client, Guid.NewGuid(), Update(note, 1, "mənim dəyişikliyim"));

        Assert.Empty(result.GetProperty("applied").EnumerateArray());
        var conflict = Assert.Single(result.GetProperty("conflicts").EnumerateArray());
        Assert.Equal(2, conflict.GetProperty("serverVersion").GetInt32());
        Assert.Equal("needs_manual", conflict.GetProperty("resolution").GetString());
        Assert.Equal("başqa cihazdan", conflict.GetProperty("serverData").GetProperty("subjective").GetString());
        Assert.Equal("başqa cihazdan", await f.Db.ScalarAsync<string>(SyncFixture.Demo, "SELECT subjective FROM clinical_notes WHERE id = $1", note));   // heç nə əzilmədi
    }

    [IntegrationFact]
    public async Task Stale_update_whose_text_already_matches_the_server_converges_instead_of_conflicting()
    {
        var (client, _, patient, device) = await SetupAsync();
        var note = Guid.NewGuid();
        await PushOkAsync(client, device, Create(note, patient, "bir"));
        await PushOkAsync(client, device, Update(note, 1, "iki"));   // cavab itdi, klient bunu bilmir

        var result = await PushOkAsync(client, device, Update(note, 1, "iki"));   // yeni clientChangeId, köhnə baseVersion, eyni mətn

        Assert.Empty(result.GetProperty("conflicts").EnumerateArray());
        Assert.Equal(2, result.GetProperty("applied")[0].GetProperty("rowVersion").GetInt32());
    }

    [IntegrationFact]
    public async Task A_conflict_is_remembered_so_a_replay_returns_it_again()
    {
        var (client, _, patient, device) = await SetupAsync();
        var note = Guid.NewGuid();
        await PushOkAsync(client, device, Create(note, patient, "bir"));
        await PushOkAsync(client, device, Update(note, 1, "iki"));
        var change = Guid.NewGuid();
        await PushOkAsync(client, device, Update(note, 1, "üç", change));

        var replay = await PushOkAsync(client, device, Update(note, 1, "üç", change));

        Assert.Single(replay.GetProperty("conflicts").EnumerateArray());
        Assert.Equal("iki", await f.Db.ScalarAsync<string>(SyncFixture.Demo, "SELECT subjective FROM clinical_notes WHERE id = $1", note));
    }

    // ---------------- Rejections ----------------
    [IntegrationFact]
    public async Task Domain_rules_still_apply_signed_notes_are_frozen_other_authors_are_refused_and_deletes_are_forbidden()
    {
        var (client, _, patient, device) = await SetupAsync();
        var note = Guid.NewGuid();
        await PushOkAsync(client, device, Create(note, patient, "bir"));
        await f.Db.ExecAsync(SyncFixture.Demo, "UPDATE clinical_notes SET signed_at = now() WHERE id = $1", note);
        var colleague = f.Client(Clinician, await f.DoctorAsync());
        var draft = Guid.NewGuid();
        await PushOkAsync(client, device, Create(draft, patient, "mənim qaralamam"));

        var signed = await PushOkAsync(client, device, Update(note, 2, "imzadan sonra"));
        var foreign = await PushOkAsync(colleague, Guid.NewGuid(), Update(draft, 1, "başqasının qeydi"));
        var delete = await PushOkAsync(client, device, new { entity = "clinical_notes", entityId = draft, op = "D", clientChangeId = Guid.NewGuid() });

        Assert.Equal("clinical.note_signed", Assert.Single(signed.GetProperty("rejected").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal("note.not_author", Assert.Single(foreign.GetProperty("rejected").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal("note.not_deletable", Assert.Single(delete.GetProperty("rejected").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal("bir", await f.Db.ScalarAsync<string>(SyncFixture.Demo, "SELECT subjective FROM clinical_notes WHERE id = $1", note));
        Assert.Equal("mənim qaralamam", await f.Db.ScalarAsync<string>(SyncFixture.Demo, "SELECT subjective FROM clinical_notes WHERE id = $1", draft));
    }

    [IntegrationFact]
    public async Task Permissions_scope_and_validation_apply_to_pushed_changes_like_to_the_api()
    {
        var (client, _, patient, device) = await SetupAsync();
        using var readOnly = f.Client(["clinical:read@tenant", "patient:read@tenant"], await f.DoctorAsync());
        var otherBranch = await f.BranchAsync();
        var foreignPatient = await f.PatientAsync(otherBranch);
        using var branchBound = f.Client(["patient:read@branch", "clinical:read@branch", "clinical:write@branch"], await f.DoctorAsync(), [await f.BranchAsync()]);

        var noWrite = await PushOkAsync(readOnly, device, Create(Guid.NewGuid(), patient, "x"));
        var outOfScope = await PushOkAsync(branchBound, device, Create(Guid.NewGuid(), foreignPatient, "x"));
        var ghostPatient = await PushOkAsync(client, device, Create(Guid.NewGuid(), Guid.NewGuid(), "x"));
        var noBase = await PushOkAsync(client, device, new { entity = "clinical_notes", entityId = Guid.NewGuid(), op = "U", clientChangeId = Guid.NewGuid(), data = new { subjective = "x" } });
        var badSource = await PushOkAsync(client, device, Create(Guid.NewGuid(), patient, "x", source: "telepathy"));

        Assert.Single(noWrite.GetProperty("rejected").EnumerateArray());
        Assert.Single(outOfScope.GetProperty("rejected").EnumerateArray());
        Assert.Equal("patient.not_found", ghostPatient.GetProperty("rejected")[0].GetProperty("code").GetString());
        Assert.Equal("sync.base_version_required", noBase.GetProperty("rejected")[0].GetProperty("code").GetString());
        Assert.Equal("note.invalid_source", badSource.GetProperty("rejected")[0].GetProperty("code").GetString());
    }

    [IntegrationFact]
    public async Task Unsupported_entities_are_rejected_and_never_written()
    {
        var (client, _, patient, device) = await SetupAsync();

        var result = await PushOkAsync(client, device,
            new { entity = "patients", entityId = patient, op = "U", baseVersion = 1, clientChangeId = Guid.NewGuid(), data = new { lastName = "Hack" } },
            new { entity = "invoices", entityId = Guid.NewGuid(), op = "I", clientChangeId = Guid.NewGuid(), data = new { total = 1 } });

        Assert.Equal(2, result.GetProperty("rejected").GetArrayLength());
        Assert.All(result.GetProperty("rejected").EnumerateArray(), r => Assert.Equal("sync.entity_not_writable", r.GetProperty("code").GetString()));
        Assert.NotEqual("Hack", await f.Db.ScalarAsync<string>(SyncFixture.Demo, "SELECT last_name FROM patients WHERE id = $1", patient));
    }

    [IntegrationFact]
    public async Task Another_users_client_change_id_cannot_be_replayed_or_hijacked()
    {
        var (client, _, patient, device) = await SetupAsync();
        var change = Guid.NewGuid();
        await PushOkAsync(client, device, Create(Guid.NewGuid(), patient, "mənim", change));
        using var intruder = f.Client(Clinician, await f.DoctorAsync());

        var result = await PushOkAsync(intruder, device, Create(Guid.NewGuid(), patient, "başqa", change));

        Assert.Equal("sync.client_change_id_taken", Assert.Single(result.GetProperty("rejected").EnumerateArray()).GetProperty("code").GetString());
        Assert.Empty(result.GetProperty("applied").EnumerateArray());
    }

    [IntegrationFact]
    public async Task A_mixed_batch_reports_each_change_separately_and_preserves_order()
    {
        var (client, _, patient, device) = await SetupAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var result = await PushOkAsync(client, device,
            Create(first, patient, "bir"),
            Create(Guid.NewGuid(), Guid.NewGuid(), "pis pasiyent"),
            Update(first, 1, "bir-redaktə"),   // eyni dəstədə yaradılan qeydi dərhal redaktə edir
            Create(second, patient, "iki"));

        Assert.Equal(3, result.GetProperty("applied").GetArrayLength());
        Assert.Equal(1, result.GetProperty("rejected").GetArrayLength());
        Assert.Equal("bir-redaktə", await f.Db.ScalarAsync<string>(SyncFixture.Demo, "SELECT subjective FROM clinical_notes WHERE id = $1", first));
        Assert.Equal(1, await NoteCount(second));
    }

    // ---------------- Request validation, audit, round trip ----------------
    [IntegrationFact]
    public async Task Malformed_requests_are_refused_before_anything_is_applied()
    {
        var (client, _, patient, device) = await SetupAsync();
        var duplicateId = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PushAsync(client, device)).StatusCode);   // boş dəst
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PushAsync(client, Guid.Empty, Create(Guid.NewGuid(), patient, "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PushAsync(client, device, Create(Guid.NewGuid(), patient, "x", duplicateId), Create(Guid.NewGuid(), patient, "y", duplicateId))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PushAsync(client, device, new { entity = "clinical_notes", entityId = Guid.NewGuid(), op = "Q", clientChangeId = Guid.NewGuid() })).StatusCode);
        var tooMany = Enumerable.Range(0, 201).Select(_ => Create(Guid.NewGuid(), patient, "x")).ToArray();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PushAsync(client, device, tooMany)).StatusCode);
        using var anonymous = f.Factory!.CreateClientFor("demo");
        Assert.Equal(HttpStatusCode.Unauthorized, (await PushAsync(anonymous, device, Create(Guid.NewGuid(), patient, "x"))).StatusCode);
    }

    [IntegrationFact]
    public async Task Push_is_audited_with_outcome_counts_and_no_clinical_content()
    {
        var (client, _, patient, device) = await SetupAsync();

        await PushOkAsync(client, device, Create(Guid.NewGuid(), patient, "çox məxfi mətn"), Create(Guid.NewGuid(), Guid.NewGuid(), "x"));

        var details = await f.Db.ScalarAsync<string>(SyncFixture.Demo, "SELECT after_data::text FROM audit_log WHERE action = 'sync.push' AND entity_id = $1 ORDER BY id DESC LIMIT 1", device);
        Assert.NotNull(details);
        Assert.Contains("\"applied\": 1", details, StringComparison.Ordinal);
        Assert.Contains("\"rejected\": 1", details, StringComparison.Ordinal);
        Assert.DoesNotContain("məxfi", details, StringComparison.Ordinal);
    }

    [IntegrationFact]
    public async Task Pushed_note_comes_back_through_pull_for_its_author_only_until_it_is_signed()
    {
        var (client, _, patient, device) = await SetupAsync();
        using var colleague = f.Client(Clinician, await f.DoctorAsync());
        var startAuthor = await NowAsync(client);
        var startColleague = await NowAsync(colleague);
        var note = Guid.NewGuid();

        await PushOkAsync(client, device, Create(note, patient, "yeni qeyd"));

        var mine = Of((await PullAsync(client, startAuthor)).Changes, note);
        Assert.Equal("yeni qeyd", Assert.Single(mine).GetProperty("data").GetProperty("subjective").GetString());
        Assert.Empty(Of((await PullAsync(colleague, startColleague)).Changes, note));   // qaralama başqasına görünmür

        await f.Db.ExecAsync(SyncFixture.Demo, "UPDATE clinical_notes SET signed_at = now() WHERE id = $1", note);
        Assert.Single(Of((await PullAsync(colleague, startColleague)).Changes, note));   // imzalandıqdan sonra görünür
    }

    private static List<JsonElement> Of(IEnumerable<JsonElement> changes, Guid id) =>
        changes.Where(c => c.GetProperty("entity").GetString() == "clinical_notes" && c.GetProperty("entityId").GetGuid() == id).ToList();
}
