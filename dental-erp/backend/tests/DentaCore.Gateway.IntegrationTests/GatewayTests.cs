using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DentaCore.Gateway.IntegrationTests;

public sealed class GatewayTests : IAsyncLifetime
{
    private StubService _identity = null!;
    private StubService _clinic = null!;
    private StubService _billing = null!;
    private StubService _notify = null!;

    public async Task InitializeAsync()
    {
        _identity = await StubService.StartAsync("identity");
        _clinic = await StubService.StartAsync("clinic-core");
        _billing = await StubService.StartAsync("billing");
        _notify = await StubService.StartAsync("notify");
    }

    public async Task DisposeAsync()
    {
        foreach (var stub in new[] { _identity, _clinic, _billing, _notify })
        {
            await stub.DisposeAsync();
        }
    }

    private Dictionary<string, string> Clusters => new()
    {
        ["identity"] = _identity.Address,
        ["clinic-core"] = _clinic.Address,
        ["billing"] = _billing.Address,
        ["notify"] = _notify.Address,
    };

    private GatewayFactory Gateway(IPAddress? remoteIp = null, params (string Key, string Value)[] extra) => new(Clusters, remoteIp, extra);

    private static async Task<string> ServiceOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("service").GetString()!;

    // ---------------- Routing ----------------
    [Theory]
    [InlineData("/v1/auth/login", "identity")]
    [InlineData("/v1/auth/refresh", "identity")]
    [InlineData("/v1/users", "identity")]
    [InlineData("/v1/users/6f1c/roles", "identity")]
    [InlineData("/v1/roles", "identity")]
    [InlineData("/v1/branches", "identity")]
    [InlineData("/v1/roles/cashier/permissions", "identity")]
    [InlineData("/v1/patients", "clinic-core")]
    [InlineData("/v1/patients/6f1c/odontogram/16/history", "clinic-core")]
    [InlineData("/v1/appointments/availability", "clinic-core")]
    [InlineData("/v1/queue", "clinic-core")]
    [InlineData("/v1/waitlist", "clinic-core")]
    [InlineData("/v1/visits/1/close", "clinic-core")]
    [InlineData("/v1/treatment-plans/1", "clinic-core")]
    [InlineData("/v1/clinical-notes", "clinic-core")]
    [InlineData("/v1/prescriptions", "clinic-core")]
    [InlineData("/v1/procedure-codes", "clinic-core")]
    [InlineData("/v1/dashboard/summary", "clinic-core")]
    [InlineData("/v1/sync/pull", "clinic-core")]
    [InlineData("/v1/search/patients", "clinic-core")]
    [InlineData("/v1/admin/audit", "clinic-core")]
    [InlineData("/v1/services", "billing")]
    [InlineData("/v1/invoices/1/payments", "billing")]
    [InlineData("/v1/payments/1/refund", "billing")]
    [InlineData("/v1/cash-shifts/current", "billing")]
    [InlineData("/hubs/notifications/negotiate", "notify")]
    public async Task Every_public_path_reaches_the_service_that_owns_it(string path, string expected)
    {
        using var factory = Gateway();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await ServiceOf(response));
    }

    [Theory]
    [InlineData("/v1/unknown")]
    [InlineData("/v1")]
    [InlineData("/v1/patientsX")]          // prefiks uyğunluğu yox, tam resurs adı
    [InlineData("/internal/anything")]
    [InlineData("/v1/admin/users")]          // admin/users başqa servisə aiddir, gateway onu clinic-core-a yönləndirmir
    [InlineData("/health/../v1/patients/..")]
    public async Task Unknown_paths_never_reach_a_service(string path)
    {
        using var factory = Gateway();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.All(new[] { _identity, _clinic, _billing, _notify }, s => Assert.Empty(s.Requests));
    }

    [Fact]
    public async Task Method_body_query_authorization_and_downstream_status_pass_through_unchanged()
    {
        await using var created = await StubService.StartAsync("billing", status: 201);
        var clusters = Clusters;
        clusters["billing"] = created.Address;
        using var factory = new GatewayFactory(clusters);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/invoices?x=1&y=a%20b") { Content = JsonContent.Create(new { hello = "dünya" }) };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer abc.def.ghi");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "k-1");
        request.Headers.TryAddWithoutValidation("If-Match", "\"3\"");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("billing", response.Headers.GetValues("X-Stub").Single());
        var seen = Assert.Single(created.Requests);
        Assert.Equal("POST", seen.Method);
        Assert.Equal("/v1/invoices", seen.Path);
        Assert.Equal("?x=1&y=a%20b", seen.Query);
        Assert.Equal("Bearer abc.def.ghi", seen.Headers["Authorization"]);
        Assert.Equal("k-1", seen.Headers["Idempotency-Key"]);
        Assert.Equal("\"3\"", seen.Headers["If-Match"]);
        Assert.Equal("dünya", JsonDocument.Parse(seen.Body).RootElement.GetProperty("hello").GetString());
    }

    // ---------------- Tenant and forwarding headers ----------------
    [Fact]
    public async Task Original_host_is_preserved_so_the_tenant_subdomain_still_resolves_downstream()
    {
        using var factory = Gateway();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/patients");
        request.Headers.Host = "klinika1.dentacore.app";

        await client.SendAsync(request);

        Assert.Equal("klinika1.dentacore.app", Assert.Single(_clinic.Requests).Headers["Host"]);
    }

    [Fact]
    public async Task Client_chosen_tenant_header_is_stripped_unless_explicitly_allowed()
    {
        using (var factory = Gateway())
        using (var client = factory.CreateClient())
        {
            client.DefaultRequestHeaders.Add("X-Tenant", "someone-elses-clinic");
            await client.GetAsync("/v1/patients");
        }

        Assert.False(Assert.Single(_clinic.Requests).Headers.ContainsKey("X-Tenant"));

        _clinic.Requests.Clear();
        using var dev = Gateway(extra: ("Gateway__ForwardTenantHeader", "true"));
        using var devClient = dev.CreateClient();
        devClient.DefaultRequestHeaders.Add("X-Tenant", "demo");
        await devClient.GetAsync("/v1/patients");

        Assert.Equal("demo", Assert.Single(_clinic.Requests).Headers["X-Tenant"]);
    }

    [Fact]
    public async Task Forwarded_headers_from_an_untrusted_peer_are_ignored_and_rewritten_from_what_the_gateway_saw()
    {
        using var factory = Gateway(IPAddress.Parse("198.51.100.9"));   // etibarsız qoşulan tərəf (ingress siyahıda yoxdur)
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "6.6.6.6");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "evil.example");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        await client.GetAsync("/v1/patients");

        var headers = Assert.Single(_clinic.Requests).Headers;
        Assert.Equal("198.51.100.9", headers["X-Forwarded-For"]);          // saxta 6.6.6.6 yox, real peer
        Assert.NotEqual("evil.example", headers["X-Forwarded-Host"]);
        Assert.Equal("http", headers["X-Forwarded-Proto"]);                 // saxta "https" əvəzinə gördüyü sxem
        Assert.NotEqual("evil.example", headers["Host"]);                   // tenant həlli üçün Host saxtalaşdırıla bilmir
    }

    [Fact]
    public async Task Forwarded_headers_from_a_trusted_ingress_are_honoured_for_client_ip_and_scheme()
    {
        using var factory = Gateway(IPAddress.Parse("10.1.2.3"), ("Gateway__TrustedProxies__0", "10.0.0.0/8"));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        await client.GetAsync("/v1/patients");

        var headers = Assert.Single(_clinic.Requests).Headers;
        Assert.Equal("203.0.113.7", headers["X-Forwarded-For"]);
        Assert.Equal("https", headers["X-Forwarded-Proto"]);
    }

    // ---------------- Correlation and response hardening ----------------
    [Fact]
    public async Task Request_id_is_generated_forwarded_and_echoed_and_a_valid_incoming_one_is_kept()
    {
        using var factory = Gateway();
        using var client = factory.CreateClient();

        var generated = await client.GetAsync("/v1/patients");
        var id = generated.Headers.GetValues("X-Request-Id").Single();
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal(id, _clinic.Requests.Last().Headers["X-Request-Id"]);

        using var withId = new HttpRequestMessage(HttpMethod.Get, "/v1/patients");
        withId.Headers.Add("X-Request-Id", "web-7f3a_01");
        var kept = await client.SendAsync(withId);
        Assert.Equal("web-7f3a_01", kept.Headers.GetValues("X-Request-Id").Single());
        Assert.Equal("web-7f3a_01", _clinic.Requests.Last().Headers["X-Request-Id"]);

        using var unsafeId = new HttpRequestMessage(HttpMethod.Get, "/v1/patients");
        unsafeId.Headers.TryAddWithoutValidation("X-Request-Id", "bad id with spaces");
        var replaced = await client.SendAsync(unsafeId);
        Assert.Matches("^[0-9a-f]{32}$", replaced.Headers.GetValues("X-Request-Id").Single());
    }

    [Fact]
    public async Task Security_headers_are_added_to_proxied_and_to_rejected_responses()
    {
        using var factory = Gateway();
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/v1/patients", "/v1/unknown", "/health" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        }
    }

    // ---------------- Limits ----------------
    [Fact]
    public async Task Auth_endpoints_have_their_own_stricter_per_ip_limit()
    {
        using var factory = Gateway(extra: [("Gateway__AuthRequestsPerMinute", "3"), ("Gateway__RequestsPerMinute", "1000")]);
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await client.PostAsync("/v1/auth/login", null)).StatusCode);
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.Equal(3, _identity.Requests.Count);   // limitə düşən sorğu aşağıya çatmır
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/patients")).StatusCode);   // ümumi trafik təsirlənmir
        var limited = await client.PostAsync("/v1/auth/login", null);
        Assert.Equal("60", limited.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task General_limit_applies_per_client_ip_taken_from_trusted_forwarded_headers_only()
    {
        // İngress 10.1.2.3-dür və güvənilir: real müştəri IP-si X-Forwarded-For-dan götürülür, hər IP-nin öz pəncərəsi var
        using var trusted = Gateway(IPAddress.Parse("10.1.2.3"), ("Gateway__RequestsPerMinute", "2"), ("Gateway__TrustedProxies__0", "10.0.0.0/8"));
        using var client = trusted.CreateClient();

        async Task<HttpStatusCode> From(string ip)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/patients");
            request.Headers.Add("X-Forwarded-For", ip);
            return (await client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await From("203.0.113.5"));
        Assert.Equal(HttpStatusCode.OK, await From("203.0.113.5"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await From("203.0.113.5"));
        Assert.Equal(HttpStatusCode.OK, await From("203.0.113.6"));   // başqa müştəri təsirlənmir
    }

    [Fact]
    public async Task Forwarded_headers_from_an_untrusted_peer_cannot_dodge_the_limit()
    {
        // Güvənilən proxy yoxdur: hamı bir IP sayılır, X-Forwarded-For ilə pəncərə dəyişdirmək olmur
        using var factory = Gateway(IPAddress.Parse("198.51.100.9"), ("Gateway__RequestsPerMinute", "2"));
        using var client = factory.CreateClient();

        async Task<HttpStatusCode> From(string ip)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/patients");
            request.Headers.Add("X-Forwarded-For", ip);
            return (await client.SendAsync(request)).StatusCode;
        }

        await From("1.1.1.1");
        await From("2.2.2.2");
        Assert.Equal(HttpStatusCode.TooManyRequests, await From("3.3.3.3"));
    }

    // ---------------- Resilience and health ----------------
    [Fact]
    public async Task Unreachable_service_gives_a_gateway_error_and_does_not_break_the_others()
    {
        var clusters = Clusters;
        clusters["billing"] = "http://127.0.0.1:1/";   // heç kim dinləmir
        using var factory = new GatewayFactory(clusters);
        using var client = factory.CreateClient();

        var broken = await client.GetAsync("/v1/invoices");
        var healthy = await client.GetAsync("/v1/patients");

        Assert.Equal(HttpStatusCode.BadGateway, broken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
    }

    [Fact]
    public async Task Downstream_health_reports_each_service_without_leaking_addresses()
    {
        var clusters = Clusters;
        clusters["notify"] = "http://127.0.0.1:1/";
        using var factory = new GatewayFactory(clusters);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/downstream");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal("degraded", body.GetProperty("status").GetString());
        var services = body.GetProperty("services");
        Assert.Equal("ok", services.GetProperty("identity").GetString());
        Assert.Equal("ok", services.GetProperty("clinic-core").GetString());
        Assert.Equal("ok", services.GetProperty("billing").GetString());
        Assert.Equal("down", services.GetProperty("notify").GetString());
        Assert.DoesNotContain("127.0.0.1", text, StringComparison.Ordinal);

        using var all = Gateway();
        using var allClient = all.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await allClient.GetAsync("/health/downstream")).StatusCode);
    }

    [Fact]
    public async Task Gateway_own_health_does_not_depend_on_the_services()
    {
        var clusters = Clusters;
        foreach (var key in clusters.Keys.ToList())
        {
            clusters[key] = "http://127.0.0.1:1/";
        }

        using var factory = new GatewayFactory(clusters);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }
}
