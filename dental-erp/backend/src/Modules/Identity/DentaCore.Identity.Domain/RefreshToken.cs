using System.Net;
using DentaCore.BuildingBlocks.Domain;

namespace DentaCore.Identity.Domain;

public sealed record RefreshTokenReuseDetected(Guid EventId, DateTimeOffset OccurredAt, Guid UserId, Guid FamilyId, string? Ip)
    : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "identity.refresh-token-reuse-detected";
}

/// <summary>
/// Rotasiya olunan refresh token. Eyni "family" (giriş sessiyası) daxilində hər token yalnız bir dəfə istifadə olunur.
/// İstifadə olunmuş token təkrar təqdim edilərsə, o oğurlanmış sayılır və bütün family ləğv olunur.
/// Açıq token saxlanmır, yalnız SHA-256 heş-i.
/// </summary>
public sealed class RefreshToken : AggregateRoot<Guid>
{
    private RefreshToken()
        : base(Guid.Empty)
    {
    }

    public Guid UserId { get; private set; }

    public Guid? DeviceId { get; private set; }

    public Guid FamilyId { get; private set; }

    public byte[] TokenHash { get; private set; } = [];

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? ReplacedBy { get; private set; }

    public IPAddress? Ip { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static RefreshToken Issue(
        Guid id,
        Guid userId,
        Guid familyId,
        byte[] tokenHash,
        DateTimeOffset now,
        TimeSpan lifetime,
        IPAddress? ip,
        Guid? deviceId = null)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        return new RefreshToken
        {
            Id = id,
            UserId = userId,
            FamilyId = familyId,
            TokenHash = tokenHash,
            ExpiresAt = now + lifetime,
            CreatedAt = now,
            Ip = ip,
            DeviceId = deviceId,
        };
    }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;

    public bool IsConsumedOrRevoked => UsedAt is not null || RevokedAt is not null;

    public void RaiseReuseDetected(DateTimeOffset now, IPAddress? ip) =>
        Raise(new RefreshTokenReuseDetected(Guid.NewGuid(), now, UserId, FamilyId, ip?.ToString()));
}
