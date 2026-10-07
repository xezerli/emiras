using System.Security.Claims;
using System.Security.Cryptography;
using DentaCore.BuildingBlocks.Application;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DentaCore.BuildingBlocks.Infrastructure.Auth;

/// <summary>JWT claim-lərindən (sub, perm, branch) ICurrentUser qurur. Sorğu başına bir dəfə.</summary>
internal sealed class HttpCurrentUser : ICurrentUser
{
    private readonly CurrentUser _inner;

    public HttpCurrentUser(IHttpContextAccessor accessor)
    {
        var http = accessor.HttpContext;
        var principal = http?.User;
        _inner = principal?.Identity?.IsAuthenticated == true && Guid.TryParse(principal.FindFirstValue("sub"), out var id)
            ? new CurrentUser(
                id,
                principal.FindAll("perm").Select(c => c.Value),
                principal.FindAll("branch").Select(c => c.Value),
                http!.Connection.RemoteIpAddress)
            : CurrentUser.Anonymous;
    }

    public bool IsAuthenticated => _inner.IsAuthenticated;

    public Guid UserId => _inner.UserId;

    public System.Net.IPAddress? Ip => _inner.Ip;

    public PermissionScope? ScopeOf(string permission) => _inner.ScopeOf(permission);

    public bool CanAccess(string permission, Guid branchId, Guid? ownerUserId = null) => _inner.CanAccess(permission, branchId, ownerUserId);

    public IReadOnlyCollection<Guid>? BranchFilterFor(string permission) => _inner.BranchFilterFor(permission);
}

/// <summary>Public açarla JWT yoxlama (Identity-dən başqa host-lar üçün). Açar JWKS ilə paylaşılana qədər PEM ilə verilir.</summary>
public sealed class JwtValidationOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "https://auth.dentacore.app";

    public string Audience { get; set; } = "dentacore-api";

    public string? PublicKeyPem { get; set; }
}

internal sealed class ConfigurePublicKeyJwtBearer(IOptions<JwtValidationOptions> options) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(string? name, JwtBearerOptions o)
    {
        var pem = options.Value.PublicKeyPem;
        if (string.IsNullOrWhiteSpace(pem))
        {
            throw new InvalidOperationException("Jwt:PublicKeyPem must be configured.");
        }

        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        if (rsa.KeySize < 2048)
        {
            throw new InvalidOperationException("Jwt public key must be at least RSA-2048.");
        }

        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = options.Value.Issuer,
            ValidAudience = options.Value.Audience,
            IssuerSigningKey = new RsaSecurityKey(rsa) { KeyId = "dc-1" },
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = "role",
        };
    }

    public void Configure(JwtBearerOptions o) => Configure(Options.DefaultName, o);
}

/// <summary>
/// Gözlənilməz DB toqquşmalarını 500 əvəzinə düzgün cavaba çevirir (RFC 9457): EF optimistic concurrency → 412,
/// handler-in tutmadığı constraint pozuntusu → 409. Gözlənilən hallar handler-lərdə konkret xəta koduyla həll olunur.
/// </summary>
public sealed class ConcurrencyExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await next(context);
        }
        catch (DentaCore.BuildingBlocks.Application.ConstraintViolationException) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(
                new { type = "https://errors.dentacore.app/conflict.constraint_violation", title = "The change conflicts with existing data", status = 409, code = "conflict.constraint_violation" },
                options: null,
                contentType: "application/problem+json",
                cancellationToken: context.RequestAborted);
        }
        catch (DbUpdateConcurrencyException) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
            await context.Response.WriteAsJsonAsync(
                new { type = "https://errors.dentacore.app/concurrency.stale", title = "The resource was modified by someone else", status = 412, code = "concurrency.stale" },
                options: null,
                contentType: "application/problem+json",
                cancellationToken: context.RequestAborted);
        }
    }
}

public static class RequestContextExtensions
{
    /// <summary>ICurrentUser (JWT claim-lərindən). Hər modulun DI-ı çağırır, təkrar çağırış zərərsizdir.</summary>
    public static IServiceCollection AddRequestContext(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpContextAccessor();
        services.TryAddScoped<ICurrentUser, HttpCurrentUser>();
        return services;
    }

    /// <summary>Identity-dən başqa host-lar: JWT yalnız public açarla yoxlanılır.</summary>
    public static IServiceCollection AddJwtValidation(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<JwtValidationOptions>(configuration.GetSection(JwtValidationOptions.Section));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<JwtBearerOptions>, ConfigurePublicKeyJwtBearer>());
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddAuthorization();
        return services;
    }
}
