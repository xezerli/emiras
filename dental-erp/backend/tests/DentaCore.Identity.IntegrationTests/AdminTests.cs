using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DentaCore.TestSupport;
using Microsoft.IdentityModel.JsonWebTokens;

namespace DentaCore.Identity.IntegrationTests;

/// <summary>İstifadəçi və rol idarəetməsi: HTTP → handler → DB, real PostgreSQL.</summary>
// ApiFactory konfiqurasiyanı proses-geniş mühit dəyişənləri ilə verir: eyni assembly-dəki iki host paralel qalxsa bir-birinin açarlarını/DB-sini görər
[Collection("Identity host (env-var configured)")]
public sealed class AdminTests : IClassFixture<PostgresFixture>, IDisposable
{
    private const string StrongPassword = "Str0ng-Passw0rd";
    private readonly PostgresFixture _db;
    private readonly ApiFactory<Program> _factory;
    private readonly TestKeys _keys = new();
    private readonly HttpClient _client;

    public AdminTests(PostgresFixture db)
    {
        _db = db;
        if (!string.IsNullOrEmpty(db.ConnectionString))
        {
            _factory = new ApiFactory<Program>(db, ("Jwt__SigningKeyPem", _keys.PrivatePem));
            _client = _factory.CreateClientFor("demo");
        }
        else
        {
            _factory = null!;
            _client = null!;
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        _keys.Dispose();
    }

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@clinic.az";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<(string Access, string Refresh)> LoginAsync(string email, string password = PostgresFixture.Password)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        return (json.GetProperty("accessToken").GetString()!, json.GetProperty("refreshToken").GetString()!);
    }

    private async Task<(string Token, Guid UserId)> AdminAsync(string role = "clinic_admin")
    {
        var email = NewEmail();
        var id = await _db.SeedUserAsync("t_demo", email, "Admin", role);
        return ((await LoginAsync(email)).Access, id);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _client.SendAsync(request);
    }

    private async Task<JsonElement> CreateUserAsync(string token, string email, string role = "reception", string password = StrongPassword)
    {
        var response = await SendAsync(HttpMethod.Post, "/v1/users", token, new { email, fullName = "Yeni İşçi", password, roles = new[] { new { role } } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonAsync(response);
    }

    private static string Code(JsonElement problem) => problem.GetProperty("code").GetString()!;

    [IntegrationFact]
    public async Task Admin_creates_a_user_who_can_sign_in_with_the_role_permissions_and_email_is_masked_and_encrypted()
    {
        var (admin, _) = await AdminAsync();
        var email = NewEmail();

        var created = await CreateUserAsync(admin, email);

        Assert.Equal("active", created.GetProperty("status").GetString());
        Assert.DoesNotContain(email, created.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("***@clinic.az", created.GetProperty("email").GetString(), StringComparison.Ordinal);
        Assert.Equal("reception", created.GetProperty("roles")[0].GetProperty("role").GetString());

        var (access, _) = await LoginAsync(email, StrongPassword);
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(access);
        Assert.Contains("patient:read@branch", jwt.Claims.Where(c => c.Type == "perm").Select(c => c.Value));

        var id = Guid.Parse(created.GetProperty("id").GetString()!);
        var enc = await _db.ScalarAsync<byte[]>("t_demo", "SELECT email_enc FROM users WHERE id = $1", id);
        Assert.DoesNotContain(email, System.Text.Encoding.UTF8.GetString(enc!), StringComparison.Ordinal);
        Assert.Equal(1, await _db.ScalarAsync<long>("t_demo", "SELECT count(*) FROM audit_log WHERE action = 'user.create' AND entity_id = $1", id));
    }

    [IntegrationFact]
    public async Task Only_user_manage_may_use_the_admin_endpoints()
    {
        var (reception, _) = await AdminAsync("reception");

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/v1/users", null)).StatusCode);
        foreach (var (method, url, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, "/v1/users", null),
            (HttpMethod.Post, "/v1/users", new { email = NewEmail(), fullName = "X Y", password = StrongPassword, roles = new[] { new { role = "reception" } } }),
            (HttpMethod.Get, "/v1/roles", null),
            (HttpMethod.Get, "/v1/branches", null),
            (HttpMethod.Put, "/v1/roles/hygienist/permissions", new { permissions = Array.Empty<object>() }),
            (HttpMethod.Post, $"/v1/users/{Guid.NewGuid()}/disable", null),
        })
        {
            var response = await SendAsync(method, url, reception, body);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("permission.denied", Code(await JsonAsync(response)));
        }
    }

    [IntegrationFact]
    public async Task Create_rejects_duplicate_email_weak_password_unknown_role_and_unknown_branch()
    {
        var (admin, _) = await AdminAsync();
        var email = NewEmail();
        await CreateUserAsync(admin, email.ToUpperInvariant());

        var duplicate = await SendAsync(HttpMethod.Post, "/v1/users", admin, new { email, fullName = "A B", password = StrongPassword, roles = new[] { new { role = "reception" } } });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("user.email_exists", Code(await JsonAsync(duplicate)));

        foreach (var weak in new[] { "short1", "onlyletters-here", "1234567890123" })
        {
            var response = await SendAsync(HttpMethod.Post, "/v1/users", admin, new { email = NewEmail(), fullName = "A B", password = weak, roles = new[] { new { role = "reception" } } });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        var unknownRole = await SendAsync(HttpMethod.Post, "/v1/users", admin, new { email = NewEmail(), fullName = "A B", password = StrongPassword, roles = new[] { new { role = "wizard" } } });
        Assert.Equal("user.unknown_role", Code(await JsonAsync(unknownRole)));

        var unknownBranch = await SendAsync(HttpMethod.Post, "/v1/users", admin, new { email = NewEmail(), fullName = "A B", password = StrongPassword, roles = new[] { new { role = "reception", branchId = Guid.NewGuid() } } });
        Assert.Equal("user.unknown_branch", Code(await JsonAsync(unknownBranch)));

        var noRoles = await SendAsync(HttpMethod.Post, "/v1/users", admin, new { email = NewEmail(), fullName = "A B", password = StrongPassword, roles = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noRoles.StatusCode);
    }

    [IntegrationFact]
    public async Task Disabling_blocks_sign_in_and_revokes_sessions_and_enabling_restores_access()
    {
        var (admin, _) = await AdminAsync();
        var email = NewEmail();
        var id = Guid.Parse((await CreateUserAsync(admin, email)).GetProperty("id").GetString()!);
        var (_, refresh) = await LoginAsync(email, StrongPassword);

        var disabled = await SendAsync(HttpMethod.Post, $"/v1/users/{id}/disable", admin);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Equal("disabled", (await JsonAsync(disabled)).GetProperty("status").GetString());

        var login = await _client.PostAsJsonAsync("/v1/auth/login", new { email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("auth.account_disabled", Code(await JsonAsync(login)));
        var refreshed = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);

        var enabled = await SendAsync(HttpMethod.Post, $"/v1/users/{id}/enable", admin);
        Assert.Equal("active", (await JsonAsync(enabled)).GetProperty("status").GetString());
        await LoginAsync(email, StrongPassword);
        Assert.Equal(1, await _db.ScalarAsync<long>("t_demo", "SELECT count(*) FROM audit_log WHERE action = 'user.disable' AND entity_id = $1", id));
    }

    [IntegrationFact]
    public async Task Enable_clears_a_temporary_lockout()
    {
        var (admin, _) = await AdminAsync();
        var email = NewEmail();
        var id = Guid.Parse((await CreateUserAsync(admin, email)).GetProperty("id").GetString()!);
        for (var i = 0; i < 5; i++)
        {
            await _client.PostAsJsonAsync("/v1/auth/login", new { email, password = "bad-password-1" });
        }

        var listed = await SendAsync(HttpMethod.Get, "/v1/users?status=locked", admin);
        Assert.Contains(id.ToString(), await listed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await SendAsync(HttpMethod.Post, $"/v1/users/{id}/enable", admin);

        await LoginAsync(email, StrongPassword);
    }

    [IntegrationFact]
    public async Task Administrators_cannot_disable_themselves_or_change_their_own_roles()
    {
        var (admin, adminId) = await AdminAsync();

        var disable = await SendAsync(HttpMethod.Post, $"/v1/users/{adminId}/disable", admin);
        var roles = await SendAsync(HttpMethod.Put, $"/v1/users/{adminId}/roles", admin, new { roles = new[] { new { role = "reception" } } });

        Assert.Equal(HttpStatusCode.Forbidden, disable.StatusCode);
        Assert.Equal("user.self_change", Code(await JsonAsync(disable)));
        Assert.Equal(HttpStatusCode.Forbidden, roles.StatusCode);
        Assert.Equal("clinic_admin", await _db.ScalarAsync<string>("t_demo", "SELECT r.code FROM user_roles ur JOIN roles r ON r.id = ur.role_id WHERE ur.user_id = $1", adminId));
    }

    [IntegrationFact]
    public async Task Changing_roles_replaces_them_and_forces_a_new_sign_in()
    {
        var (admin, _) = await AdminAsync();
        var email = NewEmail();
        var id = Guid.Parse((await CreateUserAsync(admin, email)).GetProperty("id").GetString()!);
        var (_, refresh) = await LoginAsync(email, StrongPassword);

        var changed = await SendAsync(HttpMethod.Put, $"/v1/users/{id}/roles", admin, new { roles = new[] { new { role = "doctor" }, new { role = "cashier" } } });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var assigned = (await JsonAsync(changed)).GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("role").GetString()).Order().ToList();
        Assert.Equal(["cashier", "doctor"], assigned);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refresh })).StatusCode);
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken((await LoginAsync(email, StrongPassword)).Access);
        Assert.Contains("clinical:write@own", jwt.Claims.Where(c => c.Type == "perm").Select(c => c.Value));
        Assert.DoesNotContain("reception", jwt.Claims.Where(c => c.Type == "role").Select(c => c.Value));
    }

    [IntegrationFact]
    public async Task Reset_password_replaces_the_password_and_revokes_sessions()
    {
        var (admin, _) = await AdminAsync();
        var email = NewEmail();
        var id = Guid.Parse((await CreateUserAsync(admin, email)).GetProperty("id").GetString()!);
        var (_, refresh) = await LoginAsync(email, StrongPassword);

        var weak = await SendAsync(HttpMethod.Post, $"/v1/users/{id}/reset-password", admin, new { newPassword = "weak" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, weak.StatusCode);

        var reset = await SendAsync(HttpMethod.Post, $"/v1/users/{id}/reset-password", admin, new { newPassword = "Another-Passw0rd" });

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/v1/auth/login", new { email, password = StrongPassword })).StatusCode);
        await LoginAsync(email, "Another-Passw0rd");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refresh })).StatusCode);
        var details = await _db.ScalarAsync<string>("t_demo", "SELECT coalesce(string_agg(after_data::text, ' '), '') FROM audit_log WHERE action = 'user.password.reset'");
        Assert.DoesNotContain("Another-Passw0rd", details, StringComparison.Ordinal);
    }

    [IntegrationFact]
    public async Task Listing_pages_with_a_cursor_and_rejects_a_bad_cursor()
    {
        var (admin, _) = await AdminAsync();
        for (var i = 0; i < 3; i++)
        {
            await CreateUserAsync(admin, NewEmail());
        }

        var seen = new HashSet<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var response = await SendAsync(HttpMethod.Get, $"/v1/users?limit=2{(cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor))}", admin);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await JsonAsync(response);
            foreach (var item in page.GetProperty("items").EnumerateArray())
            {
                Assert.True(seen.Add(item.GetProperty("id").GetString()!), "same user on two pages");
            }

            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 50);

        Assert.True(pages >= 2);
        Assert.True(seen.Count >= 4);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Get, "/v1/users?cursor=not-base64", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Get, "/v1/users?limit=1000", admin)).StatusCode);
    }

    [IntegrationFact]
    public async Task Role_permissions_can_be_edited_and_flow_into_the_next_token()
    {
        var (admin, _) = await AdminAsync();
        var email = NewEmail();
        await CreateUserAsync(admin, email, role: "hygienist");
        Assert.DoesNotContain("patient:read@branch", new JsonWebTokenHandler().ReadJsonWebToken((await LoginAsync(email, StrongPassword)).Access).Claims.Where(c => c.Type == "perm").Select(c => c.Value));

        var listed = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/roles", admin));
        var roles = listed.GetProperty("roles").EnumerateArray().ToDictionary(r => r.GetProperty("code").GetString()!);
        Assert.False(roles["director"].GetProperty("editable").GetBoolean());
        Assert.True(roles["hygienist"].GetProperty("editable").GetBoolean());
        Assert.Contains(listed.GetProperty("permissions").EnumerateArray(), p => p.GetProperty("code").GetString() == "patient:read");

        var updated = await SendAsync(HttpMethod.Put, "/v1/roles/hygienist/permissions", admin, new
        {
            permissions = new object[]
            {
                new { permission = "patient:read", scope = "branch" },
                new { permission = "invoice:refund", scope = "branch", maxAmount = 50.5 },
            },
        });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken((await LoginAsync(email, StrongPassword)).Access);
        Assert.Contains("patient:read@branch", jwt.Claims.Where(c => c.Type == "perm").Select(c => c.Value));
        Assert.Equal(50.5m, await _db.ScalarAsync<decimal>("t_demo", "SELECT max_amount FROM role_permissions rp JOIN roles r ON r.id = rp.role_id WHERE r.code = 'hygienist' AND rp.permission_code = 'invoice:refund'"));
        Assert.Equal(1, await _db.ScalarAsync<long>("t_demo", "SELECT count(*) FROM audit_log WHERE action = 'role.permissions.set'"));

        var cleared = await SendAsync(HttpMethod.Put, "/v1/roles/hygienist/permissions", admin, new { permissions = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(0, await _db.ScalarAsync<long>("t_demo", "SELECT count(*) FROM role_permissions rp JOIN roles r ON r.id = rp.role_id WHERE r.code = 'hygienist'"));
    }

    [IntegrationFact]
    public async Task Role_edits_refuse_protected_roles_unknown_permissions_bad_scopes_and_privilege_escalation()
    {
        var (admin, _) = await AdminAsync();
        var one = new[] { new { permission = "patient:read", scope = "branch" } };

        var protectedRole = await SendAsync(HttpMethod.Put, "/v1/roles/clinic_admin/permissions", admin, new { permissions = one });
        Assert.Equal(HttpStatusCode.Conflict, protectedRole.StatusCode);
        Assert.Equal("role.protected", Code(await JsonAsync(protectedRole)));

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, "/v1/roles/ghost/permissions", admin, new { permissions = one })).StatusCode);

        var unknown = await SendAsync(HttpMethod.Put, "/v1/roles/hygienist/permissions", admin, new { permissions = new[] { new { permission = "nuke:all", scope = "branch" } } });
        Assert.Equal("role.unknown_permission", Code(await JsonAsync(unknown)));

        var escalate = await SendAsync(HttpMethod.Put, "/v1/roles/hygienist/permissions", admin, new { permissions = new[] { new { permission = "user:manage", scope = "tenant" } } });
        Assert.Equal("role.permission_not_assignable", Code(await JsonAsync(escalate)));

        var badScope = await SendAsync(HttpMethod.Put, "/v1/roles/hygienist/permissions", admin, new { permissions = new[] { new { permission = "patient:read", scope = "galaxy" } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badScope.StatusCode);

        var duplicate = await SendAsync(HttpMethod.Put, "/v1/roles/hygienist/permissions", admin, new { permissions = new[] { one[0], one[0] } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);

        var negative = await SendAsync(HttpMethod.Put, "/v1/roles/hygienist/permissions", admin, new { permissions = new[] { new { permission = "invoice:refund", scope = "branch", maxAmount = -1 } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, negative.StatusCode);
    }

    [IntegrationFact]
    public async Task Rename_changes_only_the_display_name()
    {
        var (admin, _) = await AdminAsync();
        var email = NewEmail();
        var id = Guid.Parse((await CreateUserAsync(admin, email)).GetProperty("id").GetString()!);

        var renamed = await SendAsync(HttpMethod.Patch, $"/v1/users/{id}", admin, new { fullName = "  Leyla Məmmədova " });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Leyla Məmmədova", (await JsonAsync(renamed)).GetProperty("fullName").GetString());
        await LoginAsync(email, StrongPassword);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Patch, $"/v1/users/{Guid.NewGuid()}", admin, new { fullName = "X Y" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/v1/users/{Guid.NewGuid()}", admin)).StatusCode);
    }

    [IntegrationFact]
    public async Task Branches_are_listed_and_a_user_can_be_limited_to_one_branch()
    {
        var (admin, _) = await AdminAsync();
        var branch = await _db.SeedBranchAsync("t_demo", "Filial Nərimanov");
        var email = NewEmail();

        var listed = await SendAsync(HttpMethod.Get, "/v1/branches", admin);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Contains((await JsonAsync(listed)).EnumerateArray(), b => b.GetProperty("id").GetGuid() == branch && b.GetProperty("name").GetString() == "Filial Nərimanov");

        var created = await SendAsync(HttpMethod.Post, "/v1/users", admin, new { email, fullName = "Filial İşçisi", password = StrongPassword, roles = new[] { new { role = "reception", branchId = branch } } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(branch, (await JsonAsync(created)).GetProperty("roles")[0].GetProperty("branchId").GetGuid());

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken((await LoginAsync(email, StrongPassword)).Access);
        Assert.Equal([branch.ToString()], jwt.Claims.Where(c => c.Type == "branch").Select(c => c.Value));
    }
}
