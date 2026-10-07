using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DentaCore.TestSupport;
using Microsoft.IdentityModel.JsonWebTokens;

namespace DentaCore.Identity.IntegrationTests;

public sealed class AuthFlowTests : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly PostgresFixture _db;
    private readonly ApiFactory<Program> _factory;
    private readonly TestKeys _keys = new();
    private readonly HttpClient _client;

    public AuthFlowTests(PostgresFixture db)
    {
        _db = db;
        // Skip olunan testlərdə fixture boşdur, host yaradılmır
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

    private async Task<(string Email, Guid UserId)> SeedAsync(string schema = "t_demo")
    {
        var email = NewEmail();
        return (email, await _db.SeedUserAsync(schema, email, "Nərmin Əliyeva"));
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<(string Access, string Refresh)> LoginAsync(string email, string password = PostgresFixture.Password)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        return (json.GetProperty("accessToken").GetString()!, json.GetProperty("refreshToken").GetString()!);
    }

    [IntegrationFact]
    public async Task Login_returns_rs256_jwt_with_tenant_roles_and_permissions()
    {
        var (email, userId) = await SeedAsync();

        var response = await _client.PostAsJsonAsync("/v1/auth/login", new { email = email.ToUpperInvariant(), password = PostgresFixture.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal("Bearer", json.GetProperty("tokenType").GetString());
        Assert.Equal(600, json.GetProperty("expiresIn").GetInt32());

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(json.GetProperty("accessToken").GetString());
        Assert.Equal("RS256", jwt.Alg);
        Assert.Equal(userId.ToString(), jwt.Subject);
        Assert.Equal(_db.DemoTenantId.ToString(), jwt.GetClaim("tid").Value);
        Assert.Contains("reception", jwt.Claims.Where(c => c.Type == "role").Select(c => c.Value));
        Assert.Contains("patient:read@branch", jwt.Claims.Where(c => c.Type == "perm").Select(c => c.Value));
        Assert.True(jwt.ValidTo - jwt.ValidFrom <= TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
    }

    [IntegrationFact]
    public async Task Login_stores_only_hash_of_refresh_token_and_writes_outbox_event_atomically()
    {
        var (email, userId) = await SeedAsync();

        var (_, refresh) = await LoginAsync(email);

        var hash = await _db.ScalarAsync<byte[]>("t_demo", "SELECT token_hash FROM refresh_tokens WHERE user_id = $1", userId);
        Assert.NotNull(hash);
        Assert.Equal(32, hash.Length);
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(refresh), hash);

        var outbox = await _db.ScalarAsync<long>("t_demo",
            "SELECT count(*) FROM outbox_messages WHERE type = 'UserLoggedIn' AND payload->>'userId' = $1", userId.ToString());
        Assert.Equal(1, outbox);
        Assert.NotNull(await _db.ScalarAsync<DateTime?>("t_demo", "SELECT last_login_at FROM users WHERE id = $1", userId));
    }

    [IntegrationFact]
    public async Task Email_is_not_stored_in_plaintext()
    {
        var (email, userId) = await SeedAsync();

        var enc = await _db.ScalarAsync<byte[]>("t_demo", "SELECT email_enc FROM users WHERE id = $1", userId);

        Assert.DoesNotContain(email, System.Text.Encoding.UTF8.GetString(enc!), StringComparison.Ordinal);
    }

    [IntegrationFact]
    public async Task Wrong_password_and_unknown_email_return_identical_401_and_count_failures()
    {
        var (email, userId) = await SeedAsync();

        var wrong = await _client.PostAsJsonAsync("/v1/auth/login", new { email, password = "nope-nope" });
        var unknown = await _client.PostAsJsonAsync("/v1/auth/login", new { email = NewEmail(), password = "nope-nope" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal("application/problem+json", wrong.Content.Headers.ContentType!.MediaType);
        Assert.Equal((await JsonAsync(wrong)).GetProperty("code").GetString(), (await JsonAsync(unknown)).GetProperty("code").GetString());
        Assert.Equal(1, await _db.ScalarAsync<int>("t_demo", "SELECT failed_logins FROM users WHERE id = $1", userId));
    }

    [IntegrationFact]
    public async Task Five_failures_lock_the_account_with_423_even_for_the_correct_password()
    {
        var (email, userId) = await SeedAsync();

        for (var i = 0; i < 5; i++)
        {
            await _client.PostAsJsonAsync("/v1/auth/login", new { email, password = "bad-password" });
        }

        var locked = await _client.PostAsJsonAsync("/v1/auth/login", new { email, password = PostgresFixture.Password });

        Assert.Equal(HttpStatusCode.Locked, locked.StatusCode);
        Assert.Equal("auth.account_locked", (await JsonAsync(locked)).GetProperty("code").GetString());
        Assert.NotNull(await _db.ScalarAsync<DateTime?>("t_demo", "SELECT locked_until FROM users WHERE id = $1", userId));
        Assert.Equal(1, await _db.ScalarAsync<long>("t_demo", "SELECT count(*) FROM outbox_messages WHERE type = 'UserLockedOut' AND payload->>'userId' = $1", userId.ToString()));
    }

    [IntegrationFact]
    public async Task Invalid_input_returns_422_validation_problem()
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/login", new { email = "not-an-email", password = "" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [IntegrationFact]
    public async Task Me_requires_a_valid_token_and_returns_effective_permissions()
    {
        var (email, userId) = await SeedAsync();
        var (access, _) = await LoginAsync(email);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/v1/auth/me")).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/me");
        request.Headers.Authorization = new("Bearer", access);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal(userId, json.GetProperty("id").GetGuid());
        Assert.Contains(json.GetProperty("permissions").EnumerateArray(), p => p.GetProperty("code").GetString() == "appointment:write");
    }

    [IntegrationFact]
    public async Task Refresh_rotates_token_and_replaying_the_old_one_revokes_the_entire_family()
    {
        var (email, userId) = await SeedAsync();
        var (_, refresh1) = await LoginAsync(email);

        var rotated = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refresh1 });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var refresh2 = (await JsonAsync(rotated)).GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(refresh1, refresh2);

        // Köhnə token təkrar təqdim olunur (oğurlanma ehtimalı)
        var replay = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refresh1 });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // Yeni token da ləğv olunub: oğru da, qanuni istifadəçi də yenidən daxil olmalıdır
        var afterRevoke = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refresh2 });
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);

        Assert.Equal(0, await _db.ScalarAsync<long>("t_demo", "SELECT count(*) FROM refresh_tokens WHERE user_id = $1 AND revoked_at IS NULL", userId));
        Assert.Equal(1, await _db.ScalarAsync<long>("t_demo", "SELECT count(*) FROM outbox_messages WHERE type = 'RefreshTokenReuseDetected' AND payload->>'userId' = $1", userId.ToString()));
    }

    [IntegrationFact]
    public async Task Concurrent_refresh_with_the_same_token_lets_at_most_one_win()
    {
        var (email, userId) = await SeedAsync();
        var (_, refresh) = await LoginAsync(email);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refresh })));

        Assert.True(responses.Count(r => r.StatusCode == HttpStatusCode.OK) <= 1);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));

        // Paralel təkrar aşkarlandığı üçün family sonda tam ləğv olunmalıdır
        Assert.Equal(0, await _db.ScalarAsync<long>("t_demo", "SELECT count(*) FROM refresh_tokens WHERE user_id = $1 AND revoked_at IS NULL", userId));
    }

    [IntegrationFact]
    public async Task Logout_revokes_the_session()
    {
        var (email, _) = await SeedAsync();
        var (access, refresh) = await LoginAsync(email);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/logout") { Content = JsonContent.Create(new { refreshToken = refresh }) };
        logout.Headers.Authorization = new("Bearer", access);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(logout)).StatusCode);

        var refreshed = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
    }

    [IntegrationFact]
    public async Task Unknown_tenant_gets_404_and_other_tenants_users_cannot_log_in_here()
    {
        var (email, _) = await SeedAsync("t_other");

        using var ghost = _factory.CreateClientFor("ghost");
        var missing = await ghost.PostAsJsonAsync("/v1/auth/login", new { email, password = PostgresFixture.Password });
        var crossTenant = await _client.PostAsJsonAsync("/v1/auth/login", new { email, password = PostgresFixture.Password });

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("tenant.not_found", (await JsonAsync(missing)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, crossTenant.StatusCode);   // demo sxemində belə istifadəçi yoxdur
    }

    [IntegrationFact]
    public async Task Token_issued_for_one_tenant_is_rejected_by_another_tenant()
    {
        var (email, _) = await SeedAsync();
        var (access, _) = await LoginAsync(email);

        using var other = _factory.CreateClientFor("other");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/me");
        request.Headers.Authorization = new("Bearer", access);
        var response = await other.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenant.mismatch", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [IntegrationFact]
    public async Task Forged_and_unsigned_tokens_are_rejected()
    {
        var (email, _) = await SeedAsync();
        var (access, _) = await LoginAsync(email);
        var parts = access.Split('.');

        // 1) payload dəyişdirilib, imza köhnədir
        var tamperedPayload = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(
            System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(parts[1])).Replace("reception", "director", StringComparison.Ordinal)));
        // 2) alg=none hücumu
        var noneHeader = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}");

        foreach (var forged in new[] { $"{parts[0]}.{tamperedPayload}.{parts[2]}", $"{noneHeader}.{parts[1]}." })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/me");
            request.Headers.Authorization = new("Bearer", forged);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request)).StatusCode);
        }
    }

    [IntegrationFact]
    public async Task Ip_restricted_user_is_forbidden_from_unlisted_address()
    {
        var (email, userId) = await SeedAsync();
        await _db.ExecAsync("t_demo", "UPDATE users SET allowed_ips = ARRAY['192.168.0.0/16']::cidr[] WHERE id = $1", userId);

        var response = await _client.PostAsJsonAsync("/v1/auth/login", new { email, password = PostgresFixture.Password });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.ip_not_allowed", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [IntegrationFact]
    public async Task Two_factor_user_receives_202_challenge_and_no_tokens()
    {
        var (email, userId) = await SeedAsync();
        await _db.ExecAsync("t_demo", "UPDATE users SET two_factor_enabled = true WHERE id = $1", userId);

        var response = await _client.PostAsJsonAsync("/v1/auth/login", new { email, password = PostgresFixture.Password });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.False(json.TryGetProperty("accessToken", out _));
        Assert.Equal(0, await _db.ScalarAsync<long>("t_demo", "SELECT count(*) FROM refresh_tokens WHERE user_id = $1", userId));
    }

    [IntegrationFact]
    public async Task Health_endpoint_works_without_tenant()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
