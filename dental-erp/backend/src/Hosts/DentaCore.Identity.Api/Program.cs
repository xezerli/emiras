using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Http;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.Identity.Application;
using DentaCore.Identity.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddIdentityAuthentication();

// Brute-force qorunması (Mərhələ 3 §2: /auth/* üçün IP başına dəqiqədə 10). Gateway-də də ayrıca tətbiq olunacaq.
var authPerMinute = builder.Configuration.GetValue("RateLimiting:AuthPerMinute", 10);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

app.UseMiddleware<TenantResolutionMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<TenantClaimGuardMiddleware>();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var auth = app.MapGroup("/v1/auth").RequireRateLimiting("auth");

auth.MapPost("/login", async (LoginRequest body, HttpContext http, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(new LoginCommand(body.Email, body.Password, ClientIp(http), body.DeviceId), ct);
    return result.ToHttpResult(r => r.Tokens is { } t
        ? Results.Ok(new TokenResponse(t.AccessToken, t.RefreshToken, t.ExpiresIn))
        : Results.Json(new TwoFactorChallengeResponse(r.TwoFactorChallengeId!, ["totp"]), statusCode: StatusCodes.Status202Accepted));
});

auth.MapPost("/refresh", async (RefreshRequest body, HttpContext http, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(new RefreshTokenCommand(body.RefreshToken, ClientIp(http)), ct);
    return result.ToHttpResult(t => Results.Ok(new TokenResponse(t.AccessToken, t.RefreshToken, t.ExpiresIn)));
});

auth.MapPost("/logout", async (RefreshRequest body, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(new LogoutCommand(body.RefreshToken), ct);
    return result.ToHttpResult(_ => Results.NoContent());
}).RequireAuthorization();

auth.MapGet("/me", async (ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
{
    if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
    {
        return Results.Unauthorized();
    }

    var result = await sender.Send(new GetCurrentUserQuery(userId), ct);
    return result.ToHttpResult(Results.Ok);
}).RequireAuthorization();

await app.RunAsync();

static IPAddress? ClientIp(HttpContext http) => http.Connection.RemoteIpAddress;

internal sealed record LoginRequest(string Email, string Password, Guid? DeviceId = null, string? DeviceName = null);

internal sealed record RefreshRequest(string RefreshToken);

internal sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresIn)
{
    public string TokenType { get; init; } = "Bearer";
}

internal sealed record TwoFactorChallengeResponse(string ChallengeId, string[] Methods);

#pragma warning disable CA1050
/// <summary>WebApplicationFactory testləri üçün.</summary>
public partial class Program;
#pragma warning restore CA1050
