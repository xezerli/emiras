using System.Net;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Identity.Domain;
using FluentValidation;

namespace DentaCore.Identity.Application;

public sealed record TokenPair(string AccessToken, string RefreshToken, int ExpiresIn);

public sealed record LoginResponse(TokenPair? Tokens, string? TwoFactorChallengeId);

public static class AuthPolicy
{
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);
}

// -------- Login --------
public sealed record LoginCommand(string Email, string Password, IPAddress? ClientIp, Guid? DeviceId = null)
    : ICommand<LoginResponse>;

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254).EmailAddress();
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256);
    }
}

internal sealed class LoginCommandHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IPasswordHasher passwordHasher,
    IPiiProtector pii,
    IAccessTokenIssuer accessTokens,
    IRefreshTokenFactory refreshFactory,
    ITenantContext tenant,
    IIdentityUnitOfWork unitOfWork,
    IClock clock) : IRequestHandler<LoginCommand, LoginResponse>
{
    // Eyni cavab: istifadəçi yoxdur / parol səhvdir. Enumerasiyanın qarşısı.
    private static readonly Error InvalidCredentials = Error.Unauthorized("auth.invalid_credentials", "Email or password is incorrect.");

    public async Task<Result<LoginResponse>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var user = await users.FindByEmailHashAsync(pii.BlindIndex(request.Email), cancellationToken);
        if (user is null)
        {
            passwordHasher.BurnTime(request.Password);
            return InvalidCredentials;
        }

        var canSignIn = user.CanAttemptSignIn(now);
        if (canSignIn.IsFailure)
        {
            return canSignIn.Error!;
        }

        if (!passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            user.RegisterFailedLogin(now, LockoutPolicy.Default);
            await unitOfWork.SaveChangesAsync(cancellationToken);   // uğursuz nəticənin yan təsiri: sayğac və bloklama saxlanmalıdır
            return InvalidCredentials;
        }

        var allowedIps = await users.GetAllowedIpsAsync(user.Id, cancellationToken);
        if (allowedIps.Count > 0 && !IsIpAllowed(request.ClientIp, allowedIps))
        {
            return Error.Forbidden("auth.ip_not_allowed", "Sign-in from this IP address is not permitted.");
        }

        if (user.TwoFactorEnabled)
        {
            // TOTP/WebAuthn təsdiqi növbəti dilimdədir. Tokenlər 2FA keçilmədən VERİLMİR.
            return new LoginResponse(null, Guid.NewGuid().ToString("N"));
        }

        user.RegisterSuccessfulLogin(now, request.ClientIp);

        var access = await users.GetAccessAsync(user.Id, user.FullName, cancellationToken);
        var accessToken = accessTokens.Issue(access, tenant.TenantId);
        var (raw, hash) = refreshFactory.Create();
        refreshTokens.Add(RefreshToken.Issue(Guid.NewGuid(), user.Id, Guid.NewGuid(), hash, now, AuthPolicy.RefreshTokenLifetime, request.ClientIp, request.DeviceId));

        return new LoginResponse(new TokenPair(accessToken.Token, raw, accessToken.ExpiresInSeconds), null);
    }

    private static bool IsIpAllowed(IPAddress? ip, IReadOnlyList<string> cidrs) =>
        ip is not null && cidrs.Any(c => IPNetwork.TryParse(c, out var network) && network.Contains(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip));
}

// -------- Refresh --------
public sealed record RefreshTokenCommand(string RefreshToken, IPAddress? ClientIp) : ICommand<TokenPair>;

public sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenCommandValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(256);
}

internal sealed class RefreshTokenCommandHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IAccessTokenIssuer accessTokens,
    IRefreshTokenFactory refreshFactory,
    ITenantContext tenant,
    IIdentityUnitOfWork unitOfWork,
    IClock clock) : IRequestHandler<RefreshTokenCommand, TokenPair>
{
    private static readonly Error Invalid = Error.Unauthorized("auth.invalid_refresh_token", "Refresh token is invalid or expired.");

    public async Task<Result<TokenPair>> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var current = await refreshTokens.FindByHashAsync(refreshFactory.Hash(request.RefreshToken), cancellationToken);
        if (current is null || current.IsExpired(now))
        {
            return Invalid;
        }

        if (current.IsConsumedOrRevoked)
        {
            return await HandleReuse(current, request.ClientIp, now, cancellationToken);
        }

        var user = await users.GetAsync(current.UserId, cancellationToken);
        if (user is null || user.Status == UserStatus.Disabled || user.IsLockedAt(now))
        {
            return Invalid;
        }

        var (raw, hash) = refreshFactory.Create();
        var next = RefreshToken.Issue(Guid.NewGuid(), current.UserId, current.FamilyId, hash, now, AuthPolicy.RefreshTokenLifetime, request.ClientIp, current.DeviceId);

        // Köhnəni "istifadə olunmuş" işarələmək və yenisini yazmaq eyni tranzaksiyada.
        // Tranzaksiya yalnız bu blokda yaşayır: uduzan tərəf reuse-u tranzaksiyadan KƏNARDA işləyir,
        // əks halda onun family ləğvi dispose zamanı rollback olardı.
        bool consumed;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            consumed = await refreshTokens.TryConsumeAsync(current.Id, next.Id, now, cancellationToken);
            if (consumed)
            {
                refreshTokens.Add(next);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }

        if (!consumed)
        {
            // Paralel sorğu bu tokeni artıq istifadə edib: eyni token iki dəfə təqdim olunub
            return await HandleReuse(current, request.ClientIp, now, cancellationToken);
        }

        var access = await users.GetAccessAsync(user.Id, user.FullName, cancellationToken);
        var accessToken = accessTokens.Issue(access, tenant.TenantId);
        return new TokenPair(accessToken.Token, raw, accessToken.ExpiresInSeconds);
    }

    private async Task<Result<TokenPair>> HandleReuse(RefreshToken token, IPAddress? ip, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var revoked = await refreshTokens.RevokeFamilyAsync(token.FamilyId, now, cancellationToken);
        if (revoked > 0)
        {
            // Hadisə yalnız ilk aşkarlanmada yazılır: artıq ləğv olunmuş family-nin təkrar cəhdləri səs-küy yaratmır
            token.RaiseReuseDetected(now, ip);
            await unitOfWork.SaveChangesAsync(cancellationToken);   // outbox-a düşməlidir (audit/bildiriş)
        }

        return Invalid;
    }
}

// -------- Logout --------
public sealed record LogoutCommand(string RefreshToken) : ICommand<Unit>;

public sealed class LogoutCommandValidator : AbstractValidator<LogoutCommand>
{
    public LogoutCommandValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(256);
}

internal sealed class LogoutCommandHandler(
    IRefreshTokenRepository refreshTokens,
    IRefreshTokenFactory refreshFactory,
    IClock clock) : IRequestHandler<LogoutCommand, Unit>
{
    public async Task<Result<Unit>> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        // İdempotent: token tanınmırsa da uğurlu qaytarır (sızıntı yoxdur)
        var token = await refreshTokens.FindByHashAsync(refreshFactory.Hash(request.RefreshToken), cancellationToken);
        if (token is not null)
        {
            await refreshTokens.RevokeFamilyAsync(token.FamilyId, clock.UtcNow, cancellationToken);
        }

        return new Unit();
    }
}

// -------- Current user --------
public sealed record GetCurrentUserQuery(Guid UserId) : IQuery<CurrentUserDto>;

public sealed record CurrentUserDto(Guid Id, string FullName, IReadOnlyList<string> Roles, IReadOnlyList<PermissionGrant> Permissions);

internal sealed class GetCurrentUserQueryHandler(IUserRepository users) : IRequestHandler<GetCurrentUserQuery, CurrentUserDto>
{
    public async Task<Result<CurrentUserDto>> Handle(GetCurrentUserQuery request, CancellationToken cancellationToken)
    {
        var user = await users.GetAsync(request.UserId, cancellationToken);
        if (user is null || user.Status == UserStatus.Disabled)
        {
            return Error.NotFound("user.not_found", "User not found.");
        }

        var access = await users.GetAccessAsync(user.Id, user.FullName, cancellationToken);
        return new CurrentUserDto(access.UserId, access.FullName, access.Roles, access.Permissions);
    }
}
