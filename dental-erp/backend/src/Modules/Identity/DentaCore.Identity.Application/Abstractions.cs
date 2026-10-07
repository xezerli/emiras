using DentaCore.BuildingBlocks.Application;
using DentaCore.Identity.Domain;

namespace DentaCore.Identity.Application;

public sealed record PermissionGrant(string Code, string Scope);

/// <summary>İstifadəçinin rolları və effektiv icazələri (eyni icazə bir neçə rolda olarsa ən geniş scope qalır).</summary>
/// <param name="BranchIds">user_roles.branch_id dəyərləri. AllBranches=true olduqda (NULL = bütün filiallar) boşdur.</param>
public sealed record UserAccess(
    Guid UserId,
    string FullName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<PermissionGrant> Permissions,
    IReadOnlyList<Guid> BranchIds,
    bool AllBranches);

public sealed record IssuedAccessToken(string Token, int ExpiresInSeconds);

public interface IIdentityUnitOfWork : IModuleUnitOfWork
{
}

public interface IUserRepository
{
    Task<User?> FindByEmailHashAsync(byte[] emailHash, CancellationToken cancellationToken);

    Task<User?> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>users.allowed_ips (CIDR). Boşdursa IP məhdudiyyəti yoxdur.</summary>
    Task<IReadOnlyList<string>> GetAllowedIpsAsync(Guid userId, CancellationToken cancellationToken);

    Task<UserAccess> GetAccessAsync(Guid userId, string fullName, CancellationToken cancellationToken);
}

public interface IRefreshTokenRepository
{
    void Add(RefreshToken token);

    Task<RefreshToken?> FindByHashAsync(byte[] tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// Atomik "istifadə et": yalnız hələ istifadə olunmamış və ləğv olunmamış token üçün true qaytarır.
    /// Eyni anda iki sorğu gəlsə, yalnız biri qalib gəlir (race condition-a qarşı).
    /// </summary>
    Task<bool> TryConsumeAsync(Guid tokenId, Guid replacedBy, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Family-nin hələ ləğv olunmamış tokenlərini ləğv edir. Ləğv olunan sətirlərin sayını qaytarır (0 = artıq ləğv olunub).</summary>
    Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string hash);

    /// <summary>Mövcud olmayan istifadəçi üçün də eyni vaxt sərf etmək (timing ilə email enumerasiyasının qarşısı).</summary>
    void BurnTime(string password);
}

public interface IAccessTokenIssuer
{
    IssuedAccessToken Issue(UserAccess access, Guid tenantId);
}

public interface IRefreshTokenFactory
{
    /// <summary>Kriptoqrafik təsadüfi token + heş-i.</summary>
    (string Raw, byte[] Hash) Create();

    byte[] Hash(string raw);
}
