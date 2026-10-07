using System.Net;
using DentaCore.BuildingBlocks.Domain;

namespace DentaCore.Identity.Domain;

public enum UserStatus
{
    Active,
    Locked,
    Disabled,
}

/// <summary>Bloklama siyasəti: N uğursuz cəhddən sonra müəyyən müddət giriş bağlanır.</summary>
public sealed record LockoutPolicy(int MaxFailedAttempts, TimeSpan LockDuration)
{
    public static LockoutPolicy Default { get; } = new(5, TimeSpan.FromMinutes(15));
}

public sealed record UserLoggedIn(Guid EventId, DateTimeOffset OccurredAt, Guid UserId, string? Ip) : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "identity.user-logged-in";
}

public sealed record UserLockedOut(Guid EventId, DateTimeOffset OccurredAt, Guid UserId, DateTimeOffset LockedUntil) : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "identity.user-locked-out";
}

/// <summary>İstifadəçi aggregate-i. Parol heş-i və PHI (email) şifrələnmiş saxlanır, domen onları şəffaf bayt/sətir kimi daşıyır.</summary>
public sealed class User : AggregateRoot<Guid>
{
    // EF Core üçün
    private User()
        : base(Guid.Empty)
    {
    }

    /// <summary>Yeni istifadəçi. Parol artıq heşlənmiş, email şifrələnmiş və blind-index olunmuş gəlir (domen kriptoqrafiyanı bilmir).</summary>
    public static User Register(Guid id, byte[] emailHash, byte[] emailEnc, string passwordHash, string fullName)
    {
        ArgumentNullException.ThrowIfNull(emailHash);
        ArgumentNullException.ThrowIfNull(emailEnc);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        return new User
        {
            Id = id,
            EmailHash = emailHash,
            EmailEnc = emailEnc,
            PasswordHash = passwordHash,
            FullName = fullName.Trim(),
            Status = UserStatus.Active,
        };
    }

    public byte[] EmailHash { get; private set; } = [];

    public byte[] EmailEnc { get; private set; } = [];

    public string PasswordHash { get; private set; } = string.Empty;

    public string FullName { get; private set; } = string.Empty;

    public UserStatus Status { get; private set; }

    public int FailedLogins { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public bool TwoFactorEnabled { get; private set; }

    public void EnableTwoFactor() => TwoFactorEnabled = true;

    public void Disable() => Status = UserStatus.Disabled;

    public bool IsLockedAt(DateTimeOffset now) => LockedUntil is { } until && until > now;

    /// <summary>Giriş cəhdindən əvvəl yoxlanılır: söndürülmüş və ya müvəqqəti bloklanmış hesab.</summary>
    public Result CanAttemptSignIn(DateTimeOffset now)
    {
        if (Status == UserStatus.Disabled)
        {
            return Error.Forbidden("auth.account_disabled", "Account is disabled.");
        }

        if (IsLockedAt(now))
        {
            return Error.Locked("auth.account_locked", "Account is temporarily locked. Try again later.");
        }

        return Result.Success();
    }

    public void RegisterFailedLogin(DateTimeOffset now, LockoutPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Bloklama müddəti bitibsə sayğac təzədən başlayır
        if (LockedUntil is { } until && until <= now)
        {
            LockedUntil = null;
            FailedLogins = 0;
        }

        FailedLogins++;
        if (FailedLogins >= policy.MaxFailedAttempts)
        {
            LockedUntil = now + policy.LockDuration;
            FailedLogins = 0;
            Raise(new UserLockedOut(Guid.NewGuid(), now, Id, LockedUntil.Value));
        }
    }

    public void RegisterSuccessfulLogin(DateTimeOffset now, IPAddress? ip)
    {
        FailedLogins = 0;
        LockedUntil = null;
        LastLoginAt = now;
        Raise(new UserLoggedIn(Guid.NewGuid(), now, Id, ip?.ToString()));
    }
}
