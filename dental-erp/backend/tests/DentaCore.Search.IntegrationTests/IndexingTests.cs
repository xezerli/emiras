using System.Net;
using System.Text.Json;
using DentaCore.Search.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static DentaCore.Search.IntegrationTests.SearchFixture;

namespace DentaCore.Search.IntegrationTests;

[Collection(SearchDefinition.Name)]
public sealed class IndexingTests(SearchFixture f)
{
    private static readonly string[] Tenant = ["patient:read@tenant"];

    private Task<long?> CursorAsync() => f.Db.ScalarAsync<long?>(SearchFixture.Demo, "SELECT cursor FROM indexer_cursors WHERE name = 'patients-es'")!;

    private static string Unique() => "Ix" + Guid.NewGuid().ToString("N")[..6];

    [ElasticFact]
    public async Task Edits_soft_deletes_and_new_patients_reach_the_index_through_the_change_log()
    {
        var branch = await f.BranchAsync();
        var marker = Unique();
        var patient = await f.PatientAsync(branch, "Ad", marker + "Köhnə");
        await f.IndexAsync();
        using var client = f.Client(Tenant);
        Assert.Equal([patient], await SearchIdsAsync(client, marker + "kohne", branch));

        await f.Db.ExecAsync(SearchFixture.Demo, "UPDATE patients SET last_name = $2 WHERE id = $1", patient, marker + "Yeni");
        var added = await f.PatientAsync(branch, "Təzə", marker + "Əlavə");
        await f.IndexAsync();
        Assert.Empty(await SearchIdsAsync(client, marker + "kohne", branch));
        Assert.Equal([patient], await SearchIdsAsync(client, marker + "yeni", branch));
        Assert.Equal([added], await SearchIdsAsync(client, marker + "elave", branch));

        await f.Db.ExecAsync(SearchFixture.Demo, "UPDATE patients SET deleted_at = now() WHERE id = $1", patient);
        await f.IndexAsync();
        Assert.Empty(await SearchIdsAsync(client, marker + "yeni", branch));   // silinmiş pasiyent axtarışda yoxdur
    }

    [ElasticFact]
    public async Task First_run_backfills_existing_patients_then_follows_changes_and_idles_when_nothing_changed()
    {
        var before = await CursorAsync();
        var branch = await f.BranchAsync();
        var marker = Unique();
        var early = await f.PatientAsync(branch, "Əvvəl", marker);   // indexer işləməmişdən əvvəl
        await f.IndexAsync();
        using var client = f.Client(Tenant);

        Assert.Equal([early], await SearchIdsAsync(client, marker, branch));
        var cursor = await CursorAsync();
        Assert.NotNull(cursor);
        if (before is not null)
        {
            Assert.True(cursor >= before);
        }

        await f.IndexAsync();   // dəyişiklik yoxdur
        Assert.True(await CursorAsync() >= cursor);
    }

    [ElasticFact]
    public async Task A_stale_write_cannot_overwrite_a_newer_document_external_versioning()
    {
        var branch = await f.BranchAsync();
        var marker = Unique();
        var patient = await f.PatientAsync(branch, "Ad", marker + "Birinci");
        await f.IndexAsync();
        var index = f.PatientsIndex(f.Db.DemoTenantId);
        var id = patient.ToString("D");
        // Başqa nüsxə daha yeni versiya (999) yazıb
        var newer = $$"""{"tenantId":"{{f.Db.DemoTenantId:N}}","patientId":"{{id}}","branchId":"{{branch:N}}","chartNo":1,"chartNoText":"1","status":"active","firstName":"Ad","lastName":"{{marker}}Yenidən","fatherName":null,"fullName":"{{marker}}Yenidən Ad"}""";
        using (var put = new StringContent(newer, System.Text.Encoding.UTF8, "application/json"))
        {
            Assert.True((await f.Es.PutAsync($"{index}/_doc/{id}?version=999&version_type=external", put)).IsSuccessStatusCode);
        }

        await f.Db.ExecAsync(SearchFixture.Demo, "UPDATE patients SET last_name = $2 WHERE id = $1", patient, marker + "İkinci");   // DB versiyası 2
        await f.IndexAsync();

        var doc = await f.EsDocAsync(index, id);
        Assert.Equal(999, doc.GetProperty("_version").GetInt32());
        Assert.Equal(marker + "Yenidən", doc.GetProperty("_source").GetProperty("lastName").GetString());   // köhnə yazı əzmədi
    }

    [ElasticFact]
    public async Task When_elasticsearch_is_down_the_cursor_stays_put_and_nothing_is_lost()
    {
        var branch = await f.BranchAsync();
        var marker = Unique();
        await f.IndexAsync();   // kursor mövcuddur
        var cursorBefore = await CursorAsync();
        var patient = await f.PatientAsync(branch, "Ad", marker);

        await using (var broken = f.BuildIndexing("http://127.0.0.1:1"))
        {
            await SearchFixture.IndexAsyncUnrefreshed(broken);   // xəta udulur (loglanır), istisna çıxmır
        }

        Assert.Equal(cursorBefore, await CursorAsync());   // kursor irəli getmədi

        await f.IndexAsync();   // Elasticsearch qayıdıb: dəyişiklik itməyib
        using var client = f.Client(Tenant);
        Assert.Equal([patient], await SearchIdsAsync(client, marker, branch));
    }

    [ElasticFact]
    public async Task Search_api_answers_503_instead_of_500_when_elasticsearch_is_unreachable()
    {
        using var factory = new DentaCore.TestSupport.ApiFactory<Program>(
            f.Db, ("Jwt__PublicKeyPem", f.PublicPem), ("Elasticsearch__Uri", "http://127.0.0.1:1"), ("Elasticsearch__IndexPrefix", f.Prefix), ("Elasticsearch__RequestTimeoutSeconds", "2"));
        using var client = f.Client(Tenant, factory: factory);

        var response = await client.GetAsync("/v1/search/patients?q=abc");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("search.unavailable", (await JsonAsync(response)).GetProperty("code").GetString());
        using var audit = f.Client(["audit:read@tenant"], factory: factory);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await audit.GetAsync("/v1/admin/audit")).StatusCode);
    }

    [ElasticFact]
    public async Task Search_before_anything_was_indexed_returns_empty_not_an_error()
    {
        // İndeksi olmayan tenant: Elasticsearch 404 əvəzinə boş nəticə
        var emptyPrefix = "e" + Guid.NewGuid().ToString("N")[..8];
        using var factory = new DentaCore.TestSupport.ApiFactory<Program>(f.Db, ("Jwt__PublicKeyPem", f.PublicPem), ("Elasticsearch__Uri", f.EsUri), ("Elasticsearch__IndexPrefix", emptyPrefix));
        using var client = f.Client(Tenant, factory: factory);

        Assert.Empty(await SearchIdsAsync(client, "hechnese"));
        using var audit = f.Client(["audit:read@tenant"], factory: factory);
        var response = await audit.GetAsync("/v1/admin/audit");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await JsonAsync(response)).GetProperty("items").EnumerateArray());
    }

    [ElasticFact]
    public async Task Index_mapping_is_strict_so_unexpected_fields_are_rejected_not_silently_indexed()
    {
        var branch = await f.BranchAsync();
        await f.PatientAsync(branch, "Ad", Unique());
        await f.IndexAsync();
        var index = f.PatientsIndex(f.Db.DemoTenantId);

        using var body = new StringContent("""{"tenantId":"x","patientId":"x","phone":"+994501234567"}""", System.Text.Encoding.UTF8, "application/json");
        var response = await f.Es.PostAsync($"{index}/_doc", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);   // dynamic: strict, telefon sahəsi mapping-də yoxdur
        var mapping = JsonDocument.Parse(await f.Es.GetStringAsync($"{index}/_mapping")).RootElement.GetProperty(index).GetProperty("mappings");
        Assert.Equal("strict", mapping.GetProperty("dynamic").GetString());
    }
}
