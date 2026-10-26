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

public sealed record RoleAssignment(string Role, Guid? BranchId);

public sealed record RoleGrant(string Permission, string Scope, decimal? MaxAmount);

public sealed record RoleRow(string Code, string Name, bool IsSystem, IReadOnlyList<RoleGrant> Grants);

public sealed record PermissionInfo(string Code, string? Description);

public sealed record UserRow(Guid Id, string FullName, byte[] EmailEnc, string Status, DateTimeOffset? LastLoginAt, bool TwoFactorEnabled, IReadOnlyList<RoleAssignment> Roles);

/// <summary>İstifadəçi/rol idarəetməsi üçün oxu və rol cədvəllərinin yazısı (user_roles, role_permissions EF-də xəritələnmir).</summary>
public interface IUserAdminStore
{
    /// <summary>Ada və id-yə görə sıralı, keyset səhifələmə. <paramref name="after"/> əvvəlki səhifənin son sətridir.</summary>
    Task<IReadOnlyList<UserRow>> ListUsersAsync((string FullName, Guid Id)? after, string? status, int limit, CancellationToken cancellationToken);

    Task<UserRow?> GetUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RoleRow>> ListRolesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionInfo>> ListPermissionsAsync(CancellationToken cancellationToken);

    /// <summary>Verilənlərdən bazada olmayan rol kodlarını qaytarır.</summary>
    Task<IReadOnlyList<string>> MissingRolesAsync(IReadOnlyCollection<string> roleCodes, CancellationToken cancellationToken);

    /// <summary>Verilənlərdən aktiv olmayan və ya olmayan filial id-lərini qaytarır.</summary>
    Task<IReadOnlyList<Guid>> MissingBranchesAsync(IReadOnlyCollection<Guid> branchIds, CancellationToken cancellationToken);

    Task ReplaceUserRolesAsync(Guid userId, IReadOnlyCollection<RoleAssignment> assignments, CancellationToken cancellationToken);

    Task ReplaceRoleGrantsAsync(string roleCode, IReadOnlyCollection<RoleGrant> grants, CancellationToken cancellationToken);
}

public interface IIdentityUnitOfWork : IModuleUnitOfWork
{
}

public interface IUserRepository
{
    Task<User?> FindByEmailHashAsync(byte[] emailHash, CancellationToken cancellationToken);

    Task<User?> GetAsync(Guid userId, CancellationToken cancellationToken);

    void Add(User user);

    /// <summary>users.allowed_ips (CIDR). Boşdursa IP məhdudiyyəti yoxdur.</summary>
    Task<IReadOnlyList<string>> GetAllowedIpsAsync(Guid userId, CancellationToken cancellationToken);

    Task<UserAccess> GetAccessAsync(Guid userId, string fullName, CancellationToken cancellationToken);

    /// <summary>Aktiv filialların id-ləri (kod sırası ilə). "Bütün filiallar" hüququ olan istifadəçinin /auth/me cavabı üçün.</summary>
    Task<IReadOnlyList<Guid>> GetActiveBranchIdsAsync(CancellationToken cancellationToken);
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

    /// <summary>İstifadəçinin bütün aktiv sessiyalarını ləğv edir (söndürmə, parol sıfırlama, rol dəyişikliyi).</summary>
    Task<int> RevokeAllForUserAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
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
