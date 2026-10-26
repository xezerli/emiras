using DentaCore.Audit.Infrastructure;
using DentaCore.BuildingBlocks.Infrastructure.Observability;
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
builder.Services.AddDentaCoreObservability(builder.Configuration, builder.Logging, "identity-api");

builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddIdentityAuthentication();
builder.Services.AddAuditModule(builder.Configuration);

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

app.MapDentaCoreHealth();

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

// İstifadəçi və rol idarəetməsi (user:manage). İcazəni mediator pipeline-ı yoxlayır; RequireAuthorization yalnız autentifikasiyanı tələb edir.
var users = app.MapGroup("/v1/users").RequireAuthorization();

users.MapGet("/", async (string? status, string? cursor, int? limit, ISender sender, CancellationToken ct) =>
    (await sender.Send(new ListUsersQuery(status, cursor, limit ?? 50), ct)).ToHttpResult(Results.Ok));

users.MapPost("/", async (CreateUserRequest body, ISender sender, CancellationToken ct) =>
    (await sender.Send(new CreateUserCommand(body.Email, body.FullName, body.Password, body.Roles ?? []), ct))
        .ToHttpResult(u => Results.Created($"/v1/users/{u.Id}", u)));

users.MapGet("/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
    (await sender.Send(new GetUserQuery(id), ct)).ToHttpResult(Results.Ok));

users.MapPatch("/{id:guid}", async (Guid id, RenameUserRequest body, ISender sender, CancellationToken ct) =>
    (await sender.Send(new RenameUserCommand(id, body.FullName), ct)).ToHttpResult(Results.Ok));

users.MapPut("/{id:guid}/roles", async (Guid id, SetUserRolesRequest body, ISender sender, CancellationToken ct) =>
    (await sender.Send(new SetUserRolesCommand(id, body.Roles ?? []), ct)).ToHttpResult(Results.Ok));

users.MapPost("/{id:guid}/disable", async (Guid id, ISender sender, CancellationToken ct) =>
    (await sender.Send(new SetUserStatusCommand(id, Enabled: false), ct)).ToHttpResult(Results.Ok));

users.MapPost("/{id:guid}/enable", async (Guid id, ISender sender, CancellationToken ct) =>
    (await sender.Send(new SetUserStatusCommand(id, Enabled: true), ct)).ToHttpResult(Results.Ok));

users.MapPost("/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest body, ISender sender, CancellationToken ct) =>
    (await sender.Send(new ResetPasswordCommand(id, body.NewPassword), ct)).ToHttpResult(_ => Results.NoContent()));

app.MapGet("/v1/branches", async (ISender sender, CancellationToken ct) =>
    (await sender.Send(new ListBranchesQuery(), ct)).ToHttpResult(Results.Ok)).RequireAuthorization();

var roles = app.MapGroup("/v1/roles").RequireAuthorization();

roles.MapGet("/", async (ISender sender, CancellationToken ct) =>
    (await sender.Send(new ListRolesQuery(), ct)).ToHttpResult(Results.Ok));

roles.MapPut("/{code}/permissions", async (string code, SetRolePermissionsRequest body, ISender sender, CancellationToken ct) =>
    (await sender.Send(new SetRolePermissionsCommand(code, body.Permissions ?? []), ct)).ToHttpResult(Results.Ok));

await app.RunAsync();

static IPAddress? ClientIp(HttpContext http) => http.Connection.RemoteIpAddress;

internal sealed record LoginRequest(string Email, string Password, Guid? DeviceId = null, string? DeviceName = null);

internal sealed record RefreshRequest(string RefreshToken);

internal sealed record CreateUserRequest(string Email, string FullName, string Password, IReadOnlyList<RoleAssignment>? Roles);

internal sealed record RenameUserRequest(string FullName);

internal sealed record SetUserRolesRequest(IReadOnlyList<RoleAssignment>? Roles);

internal sealed record ResetPasswordRequest(string NewPassword);

internal sealed record SetRolePermissionsRequest(IReadOnlyList<RoleGrant>? Permissions);

internal sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresIn)
{
    public string TokenType { get; init; } = "Bearer";
}

internal sealed record TwoFactorChallengeResponse(string ChallengeId, string[] Methods);

#pragma warning disable CA1050
/// <summary>WebApplicationFactory testləri üçün.</summary>
public partial class Program;
#pragma warning restore CA1050
