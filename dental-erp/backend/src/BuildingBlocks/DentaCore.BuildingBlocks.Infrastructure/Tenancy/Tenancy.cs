using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using DentaCore.BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DentaCore.BuildingBlocks.Infrastructure.Tenancy;

public sealed record TenantInfo(Guid Id, string Slug, string Schema);

public sealed class TenancyOptions
{
    public const string Section = "Tenancy";

    /// <summary>Məs. "dentacore.app": klinika.dentacore.app → slug "klinika".</summary>
    public string BaseDomain { get; set; } = "dentacore.app";

    /// <summary>X-Tenant header-i yalnız dev/test üçün. İstehsalda tenant yalnız domendən təyin olunur.</summary>
    public bool AllowTenantHeader { get; set; }
}

public sealed class TenantContext : ITenantContext
{
    private TenantInfo? _tenant;

    public bool IsResolved => _tenant is not null;

    public Guid TenantId => Required.Id;

    public string Slug => Required.Slug;

    public string Schema => Required.Schema;

    private TenantInfo Required => _tenant ?? throw new InvalidOperationException("Tenant is not resolved for this request.");

    internal void Set(TenantInfo tenant) => _tenant = tenant;
}

public interface ITenantDirectory
{
    Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken cancellationToken);
}

/// <summary>platform.tenants cədvəlindən tenant tapır (60 san keş). Yalnız aktiv/trial tenant-lar qaytarılır.</summary>
public sealed class NpgsqlTenantDirectory(NpgsqlDataSource platformDataSource, IMemoryCache cache) : ITenantDirectory
{
    public async Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        if (!TenantNaming.IsValidSlug(slug))
        {
            return null;
        }

        if (cache.TryGetValue(slug, out TenantInfo? cached))
        {
            return cached;
        }

        await using var command = platformDataSource.CreateCommand(
            "SELECT id, slug, schema_name FROM platform.tenants WHERE slug = $1 AND status IN ('trial','active') AND deleted_at IS NULL");
        command.Parameters.AddWithValue(slug);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        TenantInfo? info = null;
        if (await reader.ReadAsync(cancellationToken))
        {
            info = new TenantInfo(reader.GetGuid(0), reader.GetString(1), reader.GetString(2));
        }

        // Tapılmayanlar da qısa müddət keşlənir (skan/brute-force zamanı DB-ni qorumaq üçün)
        cache.Set(slug, info, info is null ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(60));
        return info;
    }
}

public static partial class TenantNaming
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,40}$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex SlugRegex();

    [GeneratedRegex("^t_[a-z0-9_]{1,40}$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex SchemaRegex();

    public static bool IsValidSlug(string value) => SlugRegex().IsMatch(value);

    public static bool IsValidSchema(string value) => SchemaRegex().IsMatch(value);
}

/// <summary>Hər tenant sxemi üçün ayrıca NpgsqlDataSource (search_path = t_xxx, public). Connection pool sxem üzrə ayrılır.</summary>
public interface ITenantDataSources
{
    NpgsqlDataSource Get(string schema);
}

public sealed class TenantDataSources(IConfiguration configuration) : ITenantDataSources, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _sources = new();

    public NpgsqlDataSource Get(string schema)
    {
        if (!TenantNaming.IsValidSchema(schema))
        {
            throw new ArgumentException("Invalid tenant schema name.", nameof(schema));
        }

        return _sources.GetOrAdd(schema, s =>
        {
            var builder = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Default"))
            {
                SearchPath = $"{s},public",
            };
            return NpgsqlDataSource.Create(builder.ConnectionString);
        });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var source in _sources.Values)
        {
            await source.DisposeAsync();
        }
    }
}

/// <summary>Sorğunun tenant-ını təyin edir. Tapılmasa 404 problem+json qaytarır, sorğu davam etmir.</summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next, IOptions<TenancyOptions> options)
{
    private static readonly string[] BypassPaths = ["/health"];

    public async Task InvokeAsync(HttpContext context, ITenantDirectory directory, TenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (BypassPaths.Any(p => context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        var slug = ExtractSlug(context, options.Value);
        var tenant = slug is null ? null : await directory.FindBySlugAsync(slug, context.RequestAborted);
        if (tenant is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(
                new { type = "https://errors.dentacore.app/tenant.not_found", title = "Tenant not found", status = 404, code = "tenant.not_found" },
                options: null,
                contentType: "application/problem+json",
                cancellationToken: context.RequestAborted);
            return;
        }

        tenantContext.Set(tenant);
        await next(context);
    }

    private static string? ExtractSlug(HttpContext context, TenancyOptions options)
    {
        if (options.AllowTenantHeader && context.Request.Headers.TryGetValue("X-Tenant", out var header) && header.Count == 1)
        {
            return header[0];
        }

        var host = context.Request.Host.Host;
        var suffix = "." + options.BaseDomain;
        return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? host[..^suffix.Length] : null;
    }
}

public static class TenancyServiceCollectionExtensions
{
    public static IServiceCollection AddTenancy(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TenancyOptions>(configuration.GetSection(TenancyOptions.Section));
        services.AddMemoryCache();
        services.AddSingleton(_ => NpgsqlDataSource.Create(configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is required.")));
        services.AddSingleton<ITenantDirectory, NpgsqlTenantDirectory>();
        services.AddSingleton<ITenantDataSources, TenantDataSources>();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        return services;
    }
}

/// <summary>
/// Autentifikasiya olunmuş istifadəçinin JWT "tid" claim-i sorğunun tenant-ı ilə uyğun olmalıdır.
/// Başqa klinikanın tokeni ilə bu klinikaya müraciət (cross-tenant) burada kəsilir.
/// </summary>
public sealed class TenantClaimGuardMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.User.Identity?.IsAuthenticated == true && tenantContext.IsResolved)
        {
            var claim = context.User.FindFirst("tid")?.Value;
            if (!Guid.TryParse(claim, out var tid) || tid != tenantContext.TenantId)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(
                    new { type = "https://errors.dentacore.app/tenant.mismatch", title = "Token does not belong to this tenant", status = 403, code = "tenant.mismatch" },
                    options: null,
                    contentType: "application/problem+json",
                    cancellationToken: context.RequestAborted);
                return;
            }
        }

        await next(context);
    }
}
