using System.Net;
using System.Net.Http;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);
var gateway = builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = gateway.MaxRequestBodyBytes);
builder.Services.AddSingleton(gateway);
builder.Services.AddHttpClient("downstream-health", c => c.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(context =>
    {
        // Müştərinin göndərdiyi X-Forwarded-* etibarsızdır: üzərinə yazılır (For/Proto/Host), əlavə olunmur. Dəyərlər yalnız
        // güvənilən proxy-lərdən (Gateway:TrustedProxies) qəbul edilib RemoteIpAddress/Host-a çevrilir, gateway onları yenidən yazır.
        context.AddXForwarded(ForwardedTransformActions.Set);
        // Tenant subdomain-dən (klinika.dentacore.app) həll olunur, ona görə orijinal Host aşağıya ötürülür
        context.AddOriginalHost(true);
        context.AddRequestTransform(transform =>
        {
            if (!gateway.ForwardTenantHeader)
            {
                // X-Tenant yalnız lokal/test üçündür; istehsalda klientdən gələn tenant seçimi aşağıya çatmamalıdır
                transform.ProxyRequest.Headers.Remove("X-Tenant");
            }

            return ValueTask.CompletedTask;
        });
    });

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.ForwardLimit = 1;
    foreach (var entry in gateway.TrustedProxies)
    {
        var parts = entry.Split('/');
        if (IPAddress.TryParse(parts[0], out var address))
        {
            if (parts.Length == 2 && int.TryParse(parts[1], out var prefix))
            {
                o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(address, prefix));
            }
            else
            {
                o.KnownProxies.Add(address);
            }
        }
    }
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Hər IP üçün iki ayrı pəncərə: giriş/refresh (parol sınağı) daha sərt limitlənir və ümumi trafikdən təsirlənmir
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var isAuth = http.Request.Path.StartsWithSegments("/v1/auth", StringComparison.OrdinalIgnoreCase);
        return RateLimitPartition.GetFixedWindowLimiter(
            isAuth ? $"auth:{ip}" : $"all:{ip}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = isAuth ? gateway.AuthRequestsPerMinute : gateway.RequestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
    o.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
});

var app = builder.Build();

app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    // Korrelyasiya: düzgün gələn id saxlanır, əks halda yenisi yaradılır (log injection üçün yalnız təhlükəsiz simvollar)
    var incoming = context.Request.Headers["X-Request-Id"].ToString();
    var id = IsSafeRequestId(incoming) ? incoming : Guid.NewGuid().ToString("N");
    context.Request.Headers["X-Request-Id"] = id;
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Request-Id"] = id;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        return Task.CompletedTask;
    });
    await next();
});
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/downstream", async (Yarp.ReverseProxy.Configuration.IProxyConfigProvider provider, IHttpClientFactory clients, CancellationToken ct) =>
{
    // Hər klasterin birinci ünvanından /health oxuyur. Yalnız ad və vəziyyət qaytarır (ünvanlar daxili infrastrukturdur).
    var http = clients.CreateClient("downstream-health");
    var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (var cluster in provider.GetConfig().Clusters)
    {
        var address = cluster.Destinations?.Values.FirstOrDefault()?.Address;
        try
        {
            using var response = await http.GetAsync(new Uri(new Uri(address!), "health"), ct);
            result[cluster.ClusterId] = response.IsSuccessStatusCode ? "ok" : "unhealthy";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or ArgumentNullException)
        {
            result[cluster.ClusterId] = "down";
        }
    }

    var healthy = result.Values.All(v => v == "ok");
    return Results.Json(new { status = healthy ? "ok" : "degraded", services = result }, statusCode: healthy ? 200 : 503);
});
app.MapReverseProxy();

await app.RunAsync();

static bool IsSafeRequestId(string value) =>
    value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

internal sealed class GatewayOptions
{
    public int RequestsPerMinute { get; set; } = 600;

    public int AuthRequestsPerMinute { get; set; } = 30;

    public long MaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024;

    public bool ForwardTenantHeader { get; set; }

    /// <summary>Ingress/load balancer IP-ləri və ya CIDR (10.0.0.0/8). Boşdursa X-Forwarded-* heç kimə güvənilmir və qoşulan IP istifadə olunur.</summary>
    public string[] TrustedProxies { get; set; } = [];
}

public partial class Program;
