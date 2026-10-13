using System.Net;
using System.Text.Json;
using DentaCore.TestSupport;
using Npgsql;
using static DentaCore.Sync.IntegrationTests.SyncFixture;

namespace DentaCore.Sync.IntegrationTests;

[Collection(SyncDefinition.Name)]
public sealed class PullTests(SyncFixture f)
{
    private static readonly string[] Tenant = ["patient:read@tenant", "appointment:read@tenant", "clinical:read@tenant", "invoice:read@tenant"];

    private static Guid IdOf(JsonElement change) => change.GetProperty("entityId").GetGuid();

    private static List<JsonElement> Of(IEnumerable<JsonElement> changes, string entity) => changes.Where(c => c.GetProperty("entity").GetString() == entity).ToList();

    // ---------------- Content ----------------
    [IntegrationFact]
    public async Task Insert_update_and_delete_arrive_as_changes_with_current_data_and_row_versions()
    {
        using var client = f.Client(Tenant);
        var branch = await f.BranchAsync();
        var start = await NowAsync(client);

        var patient = await f.PatientAsync(branch, "Birinci");
        var (page, changes) = await PullAsync(client, start);
        var created = Assert.Single(Of(changes, "patients"), c => IdOf(c) == patient);
        Assert.Equal("I", created.GetProperty("op").GetString());
        Assert.Equal(1, created.GetProperty("rowVersion").GetInt32());
        Assert.Equal("Birinci", created.GetProperty("data").GetProperty("lastName").GetString());
        Assert.Equal(branch, created.GetProperty("data").GetProperty("branchId").GetGuid());
        Assert.True(page.GetProperty("cursor").GetInt64() > start);

        var afterInsert = page.GetProperty("cursor").GetInt64();
        await f.Db.ExecAsync(SyncFixture.Demo, "UPDATE patients SET last_name = 'İkinci' WHERE id = $1", patient);
        var (page2, changes2) = await PullAsync(client, afterInsert);
        var updated = Assert.Single(Of(changes2, "patients"), c => IdOf(c) == patient);
        Assert.Equal("U", updated.GetProperty("op").GetString());
        Assert.Equal(2, updated.GetProperty("rowVersion").GetInt32());
        Assert.Equal("İkinci", updated.GetProperty("data").GetProperty("lastName").GetString());

        await f.Db.ExecAsync(SyncFixture.Demo, "UPDATE patients SET deleted_at = now() WHERE id = $1", patient);
        var (_, changes3) = await PullAsync(client, page2.GetProperty("cursor").GetInt64());
        var deleted = Assert.Single(Of(changes3, "patients"), c => IdOf(c) == patient);
        Assert.Equal("D", deleted.GetProperty("op").GetString());   // soft-delete nişana (tombstone) çevrilir
        Assert.Equal(JsonValueKind.Null, deleted.GetProperty("data").ValueKind);
    }

    [IntegrationFact]
    public async Task Hard_deleted_rows_become_tombstones()
    {
        using var client = f.Client(Tenant);
        var branch = await f.BranchAsync();
        var room = Guid.NewGuid();
        await f.Db.ExecAsync(SyncFixture.Demo, "INSERT INTO rooms(id, branch_id, name) VALUES ($1, $2, 'Otaq-Z')", room, branch);
        var start = await NowAsync(client);

        await f.Db.ExecAsync(SyncFixture.Demo, "DELETE FROM rooms WHERE id = $1", room);

        var (_, changes) = await PullAsync(client, start);
        Assert.Equal("D", Assert.Single(Of(changes, "rooms"), c => IdOf(c) == room).GetProperty("op").GetString());
    }

    [IntegrationFact]
    public async Task Sensitive_patient_fields_never_leave_the_server()
    {
        using var client = f.Client(Tenant);
        var branch = await f.BranchAsync();
        var start = await NowAsync(client);
        var patient = await f.PatientAsync(branch);

        var (_, changes) = await PullAsync(client, start);

        var data = Assert.Single(Of(changes, "patients"), c => IdOf(c) == patient).GetProperty("data");
        var keys = data.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal("birthDate,branchId,chartNo,fatherName,firstName,gender,lastName,noShowCount,preferredChannel,status", string.Join(",", keys));
        var raw = data.GetRawText();
        Assert.DoesNotContain("gizli qeyd", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Bakı", raw, StringComparison.Ordinal);
    }

    [IntegrationFact]
    public async Task Several_changes_to_one_row_in_a_window_collapse_to_the_latest_state()
    {
        using var client = f.Client(Tenant);
        var branch = await f.BranchAsync();
        var start = await NowAsync(client);
        var patient = await f.PatientAsync(branch, "A");
        await f.Db.ExecAsync(SyncFixture.Demo, "UPDATE patients SET last_name = 'B' WHERE id = $1", patient);
        await f.Db.ExecAsync(SyncFixture.Demo, "UPDATE patients SET last_name = 'C' WHERE id = $1", patient);

        var (_, changes) = await PullAsync(client, start);

        var only = Assert.Single(Of(changes, "patients"), c => IdOf(c) == patient);
        Assert.Equal("C", only.GetProperty("data").GetProperty("lastName").GetString());
        Assert.Equal(3, only.GetProperty("rowVersion").GetInt32());
    }

    [IntegrationFact]
    public async Task Pulling_again_from_the_returned_cursor_yields_nothing_new()
    {
        using var client = f.Client(Tenant);
        var branch = await f.BranchAsync();
        await f.PatientAsync(branch);
        var cursor = await NowAsync(client);

        var (page, changes) = await PullAsync(client, cursor);

        Assert.Empty(changes);
        // Horizont başqa (əlaqəsiz) tranzaksiyalarla da irəliləyir, ona görə bərabərlik yox, geri çəkilməmə yoxlanılır
        Assert.True(page.GetProperty("cursor").GetInt64() >= cursor);
        Assert.False(page.GetProperty("hasMore").GetBoolean());
    }

    // ---------------- Paging ----------------
    [IntegrationFact]
    public async Task Paging_returns_every_change_exactly_once_with_a_strictly_increasing_cursor()
    {
        using var client = f.Client(Tenant);
        var branch = await f.BranchAsync();
        var start = await NowAsync(client);
        var created = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            created.Add(await f.PatientAsync(branch, "Sehife" + i));
        }

        var seen = new List<Guid>();
        var cursor = start;
        var pages = 0;
        bool more;
        do
        {
            var (page, changes) = await PullAsync(client, cursor, limit: 3);
            seen.AddRange(Of(changes, "patients").Select(IdOf));
            var next = page.GetProperty("cursor").GetInt64();
            more = page.GetProperty("hasMore").GetBoolean();
            Assert.True(next >= cursor);
            if (more)
            {
                Assert.True(next > cursor, "a page with more data must advance the cursor");
            }

            cursor = next;
            pages++;
        }
        while (more && pages < 20);

        Assert.Equal(created.Order(), seen.Where(created.Contains).Order());
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.True(pages >= 3);
    }

    [IntegrationFact]
    public async Task A_single_transaction_larger_than_the_limit_is_delivered_whole()
    {
        using var client = f.Client(Tenant);
        var branch = await f.BranchAsync();
        var start = await NowAsync(client);
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        await using (var connection = await f.Db.OpenTenantAsync(SyncFixture.Demo))
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            foreach (var id in ids)
            {
                await using var command = new NpgsqlCommand("INSERT INTO patients(id, branch_id, first_name, last_name) VALUES ($1, $2, 'T', 'Bulk')", connection, transaction);
                command.Parameters.AddWithValue(id);
                command.Parameters.AddWithValue(branch);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        var (page, changes) = await PullAsync(client, start, limit: 2);

        Assert.Equal(ids.Order(), Of(changes, "patients").Select(IdOf).Where(ids.Contains).Order());   // xid dəqiqliyində kursor: tranzaksiya bölünmür
        var (_, rest) = await PullAsync(client, page.GetProperty("cursor").GetInt64(), limit: 2);
        Assert.DoesNotContain(Of(rest, "patients"), c => ids.Contains(IdOf(c)));
    }

    // ---------------- The reason the cursor is not a sequence number ----------------
    [IntegrationFact]
    public async Task A_change_committed_late_by_a_slower_transaction_is_never_lost()
    {
        using var client = f.Client(Tenant);
        var branch = await f.BranchAsync();
        var start = await NowAsync(client);
        var slow = Guid.NewGuid();
        var fast = Guid.NewGuid();

        await using var connection = await f.Db.OpenTenantAsync(SyncFixture.Demo);
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("INSERT INTO patients(id, branch_id, first_name, last_name) VALUES ($1, $2, 'T', 'Slow')", connection, transaction))
        {
            command.Parameters.AddWithValue(slow);
            command.Parameters.AddWithValue(branch);
            await command.ExecuteNonQueryAsync();   // seq alındı, hələ commit yoxdur
        }

        await f.Db.ExecAsync(SyncFixture.Demo, "INSERT INTO patients(id, branch_id, first_name, last_name) VALUES ($1, $2, 'T', 'Fast')", fast, branch);   // sonrakı seq, ARTIQ commit olub

        var (firstPage, firstChanges) = await PullAsync(client, start, waitForHorizon: false);   // yavaş tranzaksiya AÇIQDIR: gözləmək mənasızdır
        // Yavaş tranzaksiya açıq olduğundan ondan sonrakı heç nə verilmir: yoxsa kursor onu ötüb əbədi itirərdi
        Assert.DoesNotContain(Of(firstChanges, "patients"), c => IdOf(c) == fast);
        Assert.DoesNotContain(Of(firstChanges, "patients"), c => IdOf(c) == slow);

        await transaction.CommitAsync();

        var (_, secondChanges) = await PullAsync(client, firstPage.GetProperty("cursor").GetInt64());
        var ids = Of(secondChanges, "patients").Select(IdOf).ToList();
        Assert.Contains(slow, ids);
        Assert.Contains(fast, ids);
    }

    // ---------------- Scope ----------------
    [IntegrationFact]
    public async Task Branch_scope_gets_only_its_branches_and_tenant_scope_gets_all()
    {
        var a = await f.BranchAsync();
        var b = await f.BranchAsync();
        using var branchUser = f.Client(["patient:read@branch"], branches: [a]);
        using var tenantUser = f.Client(["patient:read@tenant"]);
        var startBranch = await NowAsync(branchUser);
        var startTenant = await NowAsync(tenantUser);

        var inA = await f.PatientAsync(a);
        var inB = await f.PatientAsync(b);

        var branchIds = Of((await PullAsync(branchUser, startBranch)).Changes, "patients").Select(IdOf).ToList();
        var tenantIds = Of((await PullAsync(tenantUser, startTenant)).Changes, "patients").Select(IdOf).ToList();
        Assert.Contains(inA, branchIds);
        Assert.DoesNotContain(inB, branchIds);
        Assert.Contains(inB, tenantIds);
        Assert.Contains(inA, tenantIds);
    }

    [IntegrationFact]
    public async Task Own_scope_doctor_gets_patients_he_created_or_treats_and_only_visible_notes()
    {
        var branch = await f.BranchAsync();
        var doctor = await f.DoctorAsync();
        var colleague = await f.DoctorAsync();
        using var mine = f.Client(["patient:read@own", "clinical:read@own"], doctor, [branch]);
        var start = await NowAsync(mine);
        var treated = await f.PatientAsync(branch);
        await f.AppointmentAsync(branch, treated, doctor);
        var created = await f.PatientAsync(branch, createdBy: doctor);
        var stranger = await f.PatientAsync(branch);
        var cancelledOnly = await f.PatientAsync(branch);
        await f.AppointmentAsync(branch, cancelledOnly, doctor, "cancelled");

        var notes = new Dictionary<string, Guid>();
        foreach (var (name, author, signed) in new[] { ("ownDraft", doctor, false), ("colleagueDraft", colleague, false), ("colleagueSigned", colleague, true) })
        {
            var id = Guid.NewGuid();
            notes[name] = id;
            await f.Db.ExecAsync(
                SyncFixture.Demo, "INSERT INTO clinical_notes(id, patient_id, author_id, subjective, signed_at) VALUES ($1, $2, $3, $4, CASE WHEN $5 THEN now() END)",
                id, treated, author, name, signed);
        }

        var (_, changes) = await PullAsync(mine, start);

        var patients = Of(changes, "patients").Select(IdOf).ToList();
        Assert.Contains(treated, patients);
        Assert.Contains(created, patients);
        Assert.DoesNotContain(stranger, patients);
        Assert.DoesNotContain(cancelledOnly, patients);   // ləğv olunmuş qəbul "baxan həkim" əlaqəsi yaratmır
        var noteIds = Of(changes, "clinical_notes").Select(IdOf).ToList();
        Assert.Contains(notes["ownDraft"], noteIds);
        Assert.DoesNotContain(notes["colleagueDraft"], noteIds);   // başqasının qaralaması görünmür
        Assert.Contains(notes["colleagueSigned"], noteIds);        // imzalanmış qeyd pasiyentə baxan həkimə görünür
    }

    [IntegrationFact]
    public async Task Entities_without_the_read_permission_are_absent_and_no_permission_means_nothing()
    {
        var branch = await f.BranchAsync();
        using var receptionOnly = f.Client(["patient:read@tenant"]);
        using var nobody = f.Client(["report:read@tenant"]);
        var start = await NowAsync(receptionOnly);
        var startNobody = await NowAsync(nobody);
        var patient = await f.PatientAsync(branch);
        var service = Guid.NewGuid();
        await f.Db.ExecAsync(SyncFixture.Demo, "INSERT INTO services(id, code, name, price) VALUES ($1, $2, 'X', 1)", service, "S" + service.ToString("N")[..8]);
        var noteAuthor = await f.DoctorAsync();
        await f.Db.ExecAsync(SyncFixture.Demo, "INSERT INTO clinical_notes(id, patient_id, author_id, subjective, signed_at) VALUES ($1, $2, $3, 'x', now())", Guid.NewGuid(), patient, noteAuthor);

        var (_, changes) = await PullAsync(receptionOnly, start);
        var (_, none) = await PullAsync(nobody, startNobody);

        Assert.Contains(Of(changes, "patients"), c => IdOf(c) == patient);
        Assert.Empty(Of(changes, "services"));
        Assert.Empty(Of(changes, "clinical_notes"));
        Assert.Empty(none);
    }

    [IntegrationFact]
    public async Task A_row_outside_the_users_scope_is_skipped_without_a_tombstone_but_a_real_delete_is_reported()
    {
        var a = await f.BranchAsync();
        var b = await f.BranchAsync();
        using var branchUser = f.Client(["patient:read@branch"], branches: [a]);
        var start = await NowAsync(branchUser);
        var foreign = await f.PatientAsync(b);
        var local = await f.PatientAsync(a);
        await f.Db.ExecAsync(SyncFixture.Demo, "UPDATE patients SET deleted_at = now() WHERE id = $1", local);

        var (_, changes) = await PullAsync(branchUser, start);

        var patients = Of(changes, "patients");
        Assert.DoesNotContain(patients, c => IdOf(c) == foreign);   // başqa filialın pasiyentinin id-si də sızmır
        Assert.Equal("D", Assert.Single(patients, c => IdOf(c) == local).GetProperty("op").GetString());
    }

    [IntegrationFact]
    public async Task Another_tenants_user_sees_nothing_of_this_tenant()
    {
        using var other = f.Client(["patient:read@tenant"], tenant: "other");
        var start = await NowAsync(other);
        var branch = await f.BranchAsync();
        await f.PatientAsync(branch);

        var (_, changes) = await PullAsync(other, start);

        Assert.Empty(changes);
    }

    // ---------------- Cursor validity ----------------
    [IntegrationFact]
    public async Task Cursor_from_the_future_or_before_the_purged_horizon_requires_a_full_resync()
    {
        using var client = f.Client(Tenant);
        var now = await NowAsync(client);

        var ahead = await client.GetAsync($"/v1/sync/pull?cursor={now + 1_000_000}");
        Assert.Equal(HttpStatusCode.Gone, ahead.StatusCode);
        Assert.Equal("sync.resync_required", (await JsonAsync(ahead)).GetProperty("code").GetString());

        await f.Db.ExecAsync(SyncFixture.Demo, "INSERT INTO sync_state(key, value) VALUES ('change_log_purged_through', $1) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value", now);
        try
        {
            Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync("/v1/sync/pull?cursor=0")).StatusCode);
            Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync($"/v1/sync/pull?cursor={now - 1}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v1/sync/pull?cursor={now}")).StatusCode);
        }
        finally
        {
            await f.Db.ExecAsync(SyncFixture.Demo, "DELETE FROM sync_state WHERE key = 'change_log_purged_through'");
        }
    }

    [IntegrationFact]
    public async Task Bad_parameters_anonymous_callers_and_caching()
    {
        using var client = f.Client(Tenant);
        using var anonymous = f.Factory!.CreateClientFor("demo");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/sync/pull")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/v1/sync/pull?limit=5000")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/v1/sync/pull?cursor=-3")).StatusCode);
        var ok = await client.GetAsync("/v1/sync/pull");
        Assert.Contains("no-store", ok.Headers.CacheControl?.ToString());
    }

    // ---------------- Snapshot ----------------
    [IntegrationFact]
    public async Task Snapshot_pages_through_everything_in_scope_and_hands_back_a_cursor_to_continue_with_pull()
    {
        var branch = await f.BranchAsync();
        using var client = f.Client(["patient:read@branch"], branches: [branch]);
        var created = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            created.Add(await f.PatientAsync(branch, "Snap" + i));
        }

        await f.PatientAsync(await f.BranchAsync(), "Foreign");

        var seen = new List<Guid>();
        Guid? after = null;
        long? firstCursor = null;
        var pages = 0;
        do
        {
            var response = await client.GetAsync($"/v1/sync/snapshot?entity=patients&limit=3" + (after is null ? string.Empty : $"&after={after}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await JsonAsync(response);
            firstCursor ??= page.GetProperty("cursor").GetInt64();
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("entityId").GetGuid()));
            after = page.GetProperty("nextAfter").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextAfter").GetGuid();
            pages++;
        }
        while (after is not null && pages < 10);

        Assert.Equal(created.Order(), seen.Order());   // yalnız bu filial, tam bir dəfə
        Assert.Equal(3, pages);

        // Snapshot oxunarkən (və sonra) olan dəyişiklik ilk kursordan pull ilə gəlir
        await f.Db.ExecAsync(SyncFixture.Demo, "UPDATE patients SET last_name = 'Sonradan' WHERE id = $1", created[0]);
        var (_, changes) = await PullAsync(client, firstCursor!.Value);
        Assert.Equal("Sonradan", Assert.Single(Of(changes, "patients"), c => IdOf(c) == created[0]).GetProperty("data").GetProperty("lastName").GetString());
    }

    [IntegrationFact]
    public async Task Snapshot_refuses_unknown_and_forbidden_entities()
    {
        using var client = f.Client(["patient:read@tenant"]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/v1/sync/snapshot?entity=users")).StatusCode);       // sinxron olunmur
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/v1/sync/snapshot?entity=payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1/sync/snapshot?entity=services")).StatusCode);              // invoice:read yoxdur
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/sync/snapshot?entity=patients")).StatusCode);
    }
}
