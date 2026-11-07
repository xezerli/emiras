using System.Net;
using System.Text.Json;
using static DentaCore.Search.IntegrationTests.SearchFixture;

namespace DentaCore.Search.IntegrationTests;

[Collection(SearchDefinition.Name)]
public sealed class PatientSearchTests(SearchFixture f)
{
    private static readonly string[] Tenant = ["patient:read@tenant"];

    /// <summary>Hər test öz filialında və unikal soyad kökü ilə işləyir ki, paylaşılan indeksdə bir-birinə qarışmasın.</summary>
    private static string Unique() => "Zz" + Guid.NewGuid().ToString("N")[..6];

    // ---------------- Matching ----------------
    [ElasticFact]
    public async Task Prefix_search_folds_azerbaijani_letters_both_ways()
    {
        var branch = await f.BranchAsync();
        var marker = Unique();
        var eliyev = await f.PatientAsync(branch, "Rəşad", marker + "Əliyev");
        var huseynov = await f.PatientAsync(branch, "Elçin", marker + "Hüseynov");
        var ismayilov = await f.PatientAsync(branch, "Ceyhun", marker + "İsmayılov");
        await f.IndexAsync();
        using var client = f.Client(Tenant);

        Assert.Contains(eliyev, await SearchIdsAsync(client, marker + "eli", branch));          // ə → e
        Assert.Contains(eliyev, await SearchIdsAsync(client, marker + "Əliyev", branch));
        Assert.Contains(huseynov, await SearchIdsAsync(client, marker + "hus", branch));        // ü → u (yazdıqca)
        Assert.Contains(huseynov, await SearchIdsAsync(client, marker + "HÜSEYNOV", branch));   // böyük hərf
        Assert.Contains(ismayilov, await SearchIdsAsync(client, marker + "ismayilov", branch)); // ı, İ → i
        Assert.Contains(ismayilov, await SearchIdsAsync(client, marker + "İSMAYILOV", branch));

        // Qısa prefikslər fuzzy ilə xilas ola bilməz (bir neçə hərf fərqi var): yalnız düzgün hərf xəritələməsi tapır
        Assert.Contains(eliyev, await SearchIdsAsync(client, marker + "Əli", branch));
        Assert.Contains(eliyev, await SearchIdsAsync(client, marker + "eli", branch));
        Assert.Contains(huseynov, await SearchIdsAsync(client, marker + "Hüs", branch));
        Assert.Contains(ismayilov, await SearchIdsAsync(client, marker + "İsm", branch));
        Assert.Contains(ismayilov, await SearchIdsAsync(client, marker + "ISM", branch));
    }

    [ElasticFact]
    public async Task All_special_letters_fold_to_their_latin_base_in_prefix_search()
    {
        var branch = await f.BranchAsync();
        var marker = Unique();
        var patient = await f.PatientAsync(branch, "Şəfəq", marker + "ÇöğÜrşı");
        await f.IndexAsync();
        using var client = f.Client(Tenant);

        foreach (var query in new[] { "cogurs", "ÇÖĞÜRŞ", "çöğürş", "COGURSI" })
        {
            Assert.Contains(patient, await SearchIdsAsync(client, marker + query, branch));
        }

        Assert.Contains(patient, await SearchIdsAsync(client, "sefeq " + marker + "cogur", branch));   // ş→s, ə→e
    }

    [ElasticFact]
    public async Task First_name_last_name_and_father_name_are_searchable_in_any_order()
    {
        var branch = await f.BranchAsync();
        var marker = Unique();
        var patient = await f.PatientAsync(branch, "Nigar" + marker, "Quliyeva", "Fərhad");
        var other = await f.PatientAsync(branch, "Nigar" + marker, "Məmmədova");
        await f.IndexAsync();
        using var client = f.Client(Tenant);

        Assert.Equal([patient], await SearchIdsAsync(client, $"quliyeva nigar{marker}", branch));
        Assert.Equal([patient], await SearchIdsAsync(client, $"nigar{marker} quliyeva", branch));
        Assert.Equal([patient], await SearchIdsAsync(client, $"farhad quliyeva nigar{marker}", branch));
        Assert.Equal(2, (await SearchIdsAsync(client, "nigar" + marker, branch)).Count);
        Assert.Contains(other, await SearchIdsAsync(client, "nigar" + marker, branch));
    }

    [ElasticFact]
    public async Task Typos_still_find_the_patient_and_exact_matches_rank_first()
    {
        var branch = await f.BranchAsync();
        var marker = Unique().ToLowerInvariant();
        var exact = await f.PatientAsync(branch, "Vüqar", marker + "mammadov");
        await f.PatientAsync(branch, "Vüqar", marker + "mammadli");
        await f.IndexAsync();
        using var client = f.Client(Tenant);

        var typo = await SearchIdsAsync(client, marker + "mamadov", branch);     // bir hərf buraxılıb
        Assert.Contains(exact, typo);

        var ranked = await SearchIdsAsync(client, marker + "mammadov", branch);
        Assert.Equal(exact, ranked[0]);
    }

    [ElasticFact]
    public async Task Chart_number_matches_exactly()
    {
        var branch = await f.BranchAsync();
        var patient = await f.PatientAsync(branch, "Kart", Unique());
        var chartNo = await f.ChartNoAsync(patient);
        await f.IndexAsync();
        using var client = f.Client(Tenant);

        Assert.Equal(patient, (await SearchIdsAsync(client, chartNo.ToString(System.Globalization.CultureInfo.InvariantCulture), branch))[0]);
    }

    [ElasticFact]
    public async Task Search_text_is_data_not_query_syntax_and_nothing_matches_gracefully()
    {
        var branch = await f.BranchAsync();
        await f.PatientAsync(branch, "Adi", Unique());
        await f.IndexAsync();
        using var client = f.Client(Tenant);

        Assert.Empty(await SearchIdsAsync(client, "*:*", branch));                              // Lucene sintaksisi icra olunmur
        Assert.Empty(await SearchIdsAsync(client, "\" OR 1=1 --", branch));
        Assert.Empty(await SearchIdsAsync(client, "{\"match_all\":{}}", branch));
        Assert.Empty(await SearchIdsAsync(client, "qqqqqqqqqqqq", branch));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/v1/search/patients?q=a")).StatusCode);    // çox qısa
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"/v1/search/patients?q={new string('x', 101)}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/v1/search/patients?q=ab&limit=51")).StatusCode);
    }

    [ElasticFact]
    public async Task Limit_is_respected()
    {
        var branch = await f.BranchAsync();
        var marker = Unique();
        for (var i = 0; i < 6; i++)
        {
            await f.PatientAsync(branch, "Çox", marker + i);
        }

        await f.IndexAsync();
        using var client = f.Client(Tenant);

        Assert.Equal(3, (await SearchIdsAsync(client, marker, branch, limit: 3)).Count);
    }

    // ---------------- Scope and isolation ----------------
    [ElasticFact]
    public async Task Branch_scope_searches_only_its_branches_and_the_filter_is_part_of_the_query()
    {
        var a = await f.BranchAsync();
        var b = await f.BranchAsync();
        var marker = Unique();
        var inA = await f.PatientAsync(a, "Filial", marker + "A");
        var inB = await f.PatientAsync(b, "Filial", marker + "B");
        await f.IndexAsync();
        using var branchUser = f.Client(["patient:read@branch"], branches: [a]);
        using var tenantUser = f.Client(Tenant);

        var own = await SearchIdsAsync(branchUser, marker);
        Assert.Equal([inA], own);                                           // filial göstərilməyib: yalnız öz filialı
        Assert.Equal(HttpStatusCode.NotFound, (await branchUser.GetAsync($"/v1/search/patients?q={marker}&branchId={b}")).StatusCode);
        Assert.Equal(2, (await SearchIdsAsync(tenantUser, marker)).Count);
        Assert.Equal([inB], await SearchIdsAsync(tenantUser, marker, b));

        using var both = f.Client(["patient:read@branch"], branches: [a, b]);
        Assert.Equal(2, (await SearchIdsAsync(both, marker)).Count);
    }

    [ElasticFact]
    public async Task Own_scope_and_missing_permission_are_refused()
    {
        using var own = f.Client(["patient:read@own"]);
        using var none = f.Client(["invoice:read@tenant"]);
        using var anonymous = f.Factory!.CreateClientFor("demo");

        Assert.Equal(HttpStatusCode.Forbidden, (await own.GetAsync("/v1/search/patients?q=ab")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync("/v1/search/patients?q=ab")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/search/patients?q=ab")).StatusCode);
    }

    [ElasticFact]
    public async Task Tenants_have_separate_indexes_and_never_see_each_others_patients()
    {
        var marker = Unique();
        var demoBranch = await f.BranchAsync();
        var otherBranch = await f.BranchAsync(SearchFixture.Other);
        var demoPatient = await f.PatientAsync(demoBranch, "Tenant", marker + "D");
        var otherPatient = await f.PatientAsync(otherBranch, "Tenant", marker + "O", schema: SearchFixture.Other);
        await f.IndexAsync();
        using var demo = f.Client(Tenant);
        using var other = f.Client(Tenant, tenant: "other");

        Assert.Equal([demoPatient], await SearchIdsAsync(demo, marker));
        Assert.Equal([otherPatient], await SearchIdsAsync(other, marker));
        Assert.Equal(HttpStatusCode.OK, (await f.Es.GetAsync(f.PatientsIndex(f.Db.DemoTenantId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Es.GetAsync(f.PatientsIndex(f.Db.OtherTenantId))).StatusCode);
        // sənədin özündə də tenant var: yanlış indeksə düşmə halında belə filtrdə tutulur
        Assert.Equal(f.Db.DemoTenantId.ToString("N"), (await f.EsDocAsync(f.PatientsIndex(f.Db.DemoTenantId), demoPatient.ToString("D"))).GetProperty("_source").GetProperty("tenantId").GetString());
    }

    [ElasticFact]
    public async Task Index_holds_only_name_fields_never_contact_details_or_notes()
    {
        var branch = await f.BranchAsync();
        var patient = await f.PatientAsync(branch, "Məxfi", Unique());
        await f.Db.ExecAsync(SearchFixture.Demo, "UPDATE patients SET phone_enc = '\\xdeadbeef'::bytea, email_enc = '\\xcafe'::bytea, notes = 'çox gizli', address = '{\"city\":\"Bakı\"}'::jsonb WHERE id = $1", patient);
        await f.IndexAsync();

        var source = (await f.EsDocAsync(f.PatientsIndex(f.Db.DemoTenantId), patient.ToString("D"))).GetProperty("_source");

        Assert.Equal(
            "branchId,chartNo,chartNoText,fatherName,firstName,fullName,lastName,patientId,status,tenantId",
            string.Join(",", source.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
    }

    [ElasticFact]
    public async Task Patient_search_is_audited_with_a_count_and_never_the_query_text()
    {
        var branch = await f.BranchAsync();
        var marker = Unique();
        await f.PatientAsync(branch, "Audit", marker);
        await f.IndexAsync();
        var me = Guid.NewGuid();
        using var client = f.Client(Tenant, me);

        await SearchIdsAsync(client, marker + "xyz");

        var details = await f.Db.ScalarAsync<string>(SearchFixture.Demo, "SELECT after_data::text FROM audit_log WHERE action = 'patient.search' AND user_id = $1 ORDER BY id DESC LIMIT 1", me);
        Assert.Contains("\"results\"", details, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, details!, StringComparison.Ordinal);
        var cache = (await client.GetAsync($"/v1/search/patients?q={marker}")).Headers.CacheControl?.ToString();
        Assert.Contains("no-store", cache);
    }
}
