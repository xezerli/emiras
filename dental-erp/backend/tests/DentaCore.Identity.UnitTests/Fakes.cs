using System.Net;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Security;
using DentaCore.Identity.Application;
using DentaCore.Identity.Domain;
using Microsoft.Extensions.Options;

namespace DentaCore.Identity.UnitTests;

internal sealed class FakeClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = start;
}

internal sealed class FakeTenant : ITenantContext
{
    public bool IsResolved => true;

    public Guid TenantId { get; } = Guid.NewGuid();

    public string Slug => "demo";

    public string Schema => "t_demo";
}

/// <summary>Sürətli test üçün: Argon2 əvəzinə sadə müqayisə, çağırış sayğacları ilə.</summary>
internal sealed class FakeHasher : IPasswordHasher
{
    public int BurnCount { get; private set; }

    public string Hash(string password) => "hash:" + password;

    public bool Verify(string password, string hash) => hash == "hash:" + password;

    public void BurnTime(string password) => BurnCount++;
}

internal sealed class FakeTokenIssuer : IAccessTokenIssuer
{
    public IssuedAccessToken Issue(UserAccess access, Guid tenantId) => new($"access-for-{access.UserId}-{tenantId}", 600);
}

internal sealed class FakeUnitOfWork : IIdentityUnitOfWork
{
    public int Saves { get; private set; }

    public int Commits { get; private set; }

    public System.Reflection.Assembly ApplicationAssembly => typeof(IIdentityUnitOfWork).Assembly;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Saves++;
        return Task.FromResult(0);
    }

    public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IUnitOfWorkTransaction>(new Tx(this));

    private sealed class Tx(FakeUnitOfWork owner) : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            owner.Commits++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FakeUsers : IUserRepository
{
    public List<User> Items { get; } = [];

    public List<string> AllowedIps { get; } = [];

    public Task<User?> FindByEmailHashAsync(byte[] emailHash, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(u => u.EmailHash.SequenceEqual(emailHash)));

    public Task<User?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(u => u.Id == userId));

    public Task<IReadOnlyList<string>> GetAllowedIpsAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(AllowedIps);

    public Task<UserAccess> GetAccessAsync(Guid userId, string fullName, CancellationToken cancellationToken) =>
        Task.FromResult(new UserAccess(userId, fullName, ["reception"], [new PermissionGrant("patient:read", "branch")]));
}

internal sealed class FakeRefreshTokens : IRefreshTokenRepository
{
    public List<RefreshToken> Items { get; } = [];

    public HashSet<Guid> Consumed { get; } = [];

    public HashSet<Guid> RevokedFamilies { get; } = [];

    /// <summary>Paralel sorğunu təqlid etmək üçün: true olarsa TryConsume həmişə uğursuz olur.</summary>
    public bool LoseConsumeRace { get; set; }

    public void Add(RefreshToken token) => Items.Add(token);

    public Task<RefreshToken?> FindByHashAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        var token = Items.FirstOrDefault(t => t.TokenHash.SequenceEqual(tokenHash));
        return Task.FromResult(token);
    }

    public Task<bool> TryConsumeAsync(Guid tokenId, Guid replacedBy, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(!LoseConsumeRace && Consumed.Add(tokenId));

    public Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(RevokedFamilies.Add(familyId) ? 1 : 0);
}

internal static class TestPii
{
    public static AesGcmPiiProtector Create() => new(Options.Create(new PiiOptions
    {
        PiiEncryptionKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()),
        PiiHashKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 101)).ToArray()),
    }));
}

internal static class Ip
{
    public static readonly IPAddress Office = IPAddress.Parse("10.1.2.3");
}
