using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DentaCore.BuildingBlocks.Infrastructure.Observability;

public static class ObservabilityExtensions
{
    /// <summary>
    /// Telemetriya: OTLP ixracı YALNIZ <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> təyin olunanda qoşulur (yerli işlədəndə sıfır xərc).
    /// Heç bir host-da scrape (/metrics) endpoint-i açılmır: metrikalar OTel Collector-a itələnir, şəbəkə səthi artmır.
    /// <c>Logging:Format=json</c> olduqda log-lar bir sətirlik JSON-dur (log toplayıcı üçün). Sorğu sətri (PHI ola bilər) log-lanmır:
    /// <c>Microsoft.AspNetCore</c> kateqoriyası Warning səviyyəsində saxlanır (appsettings; test yoxlayır).
    /// </summary>
    public static IServiceCollection AddDentaCoreObservability(this IServiceCollection services, IConfiguration configuration, ILoggingBuilder logging, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logging);

        if (string.Equals(configuration["Logging:Format"], "json", StringComparison.OrdinalIgnoreCase))
        {
            logging.ClearProviders();
            logging.AddJsonConsole(o =>
            {
                o.UseUtcTimestamp = true;
                o.IncludeScopes = false;
                o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            });
        }

        if (string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithMetrics(m => m
                .AddMeter(DentaCoreMetrics.MeterName, "Npgsql")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter())
            .WithTracing(t => t
                .AddSource("Npgsql")
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal))
                .AddHttpClientInstrumentation()
                .AddOtlpExporter());
        return services;
    }

    /// <summary>
    /// <c>/health</c>: canlılıq (heç bir asılılığı yoxlamır; DB düşəndə pod-u yenidən başlatmaq lazım deyil).
    /// <c>/health/ready</c>: platforma bazası cavab verir (yoxdursa 503, trafik göndərilmir). Cavabda təfərrüat yoxdur.
    /// </summary>
    public static IEndpointRouteBuilder MapDentaCoreHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        endpoints.MapGet("/health/ready", async (NpgsqlDataSource platform, CancellationToken cancellationToken) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await using var command = platform.CreateCommand("SELECT 1");
                await command.ExecuteScalarAsync(timeout.Token);
                return Results.Ok(new { status = "ready" });
            }
            catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or InvalidOperationException)
            {
                return Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        return endpoints;
    }
}
