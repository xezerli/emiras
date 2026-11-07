using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DentaCore.Gateway.IntegrationTests;

public sealed record Seen(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>Aşağıdakı servisin əvəzi: real Kestrel-də, ephemeral portda. Gələn sorğunu yazır, tələb olunan status ilə cavab verir.</summary>
public sealed class StubService : IAsyncDisposable
{
    private readonly WebApplication _app;

    private StubService(WebApplication app, string name)
    {
        _app = app;
        Name = name;
    }

    public string Name { get; }

    public ConcurrentQueue<Seen> Requests { get; } = new();

    public string Address => _app.Urls.First().TrimEnd('/') + "/";

    public static async Task<StubService> StartAsync(string name, int status = 200)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var stub = new StubService(app, name);
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.Map("/{**rest}", async (HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var headers = http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            stub.Requests.Enqueue(new Seen(http.Request.Method, http.Request.Path, http.Request.QueryString.Value ?? string.Empty, headers, await reader.ReadToEndAsync()));
            http.Response.Headers["X-Stub"] = name;
            http.Response.StatusCode = status;
            await http.Response.WriteAsJsonAsync(new { service = name });
        });
        await app.StartAsync();
        return stub;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

/// <summary>Gateway-i real kimi qaldırır; klaster ünvanları və limitlər mühit dəyişənləri ilə verilir (minimal hosting konfiqurasiyanı dərhal oxuyur).</summary>
public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _previous = [];
    private readonly IPAddress? _remoteIp;

    public GatewayFactory(IReadOnlyDictionary<string, string> clusters, IPAddress? remoteIp = null, params (string Key, string Value)[] extra)
    {
        _remoteIp = remoteIp;
        foreach (var (cluster, address) in clusters)
        {
            Set($"ReverseProxy__Clusters__{cluster}__Destinations__d1__Address", address);
        }

        foreach (var (key, value) in extra)
        {
            Set(key, value);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (_remoteIp is not null)
        {
            builder.ConfigureServices(services => services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter>(_ => new FakeRemoteIpFilter(_remoteIp)));
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (var (key, value) in _previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private void Set(string key, string? value)
    {
        _previous[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    private sealed class FakeRemoteIpFilter(IPAddress ip) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = ip;   // TestServer-də RemoteIpAddress null olur: ingress-i təqlid edir
                return nextMiddleware();
            });
            next(app);
        };
    }
}
