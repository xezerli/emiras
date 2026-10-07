using DentaCore.Identity.Application;
using DentaCore.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace DentaCore.Identity.Infrastructure.Persistence;

internal sealed class UserRepository(IdentityDbContext db) : IUserRepository
{
    public Task<User?> FindByEmailHashAsync(byte[] emailHash, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.EmailHash == emailHash, cancellationToken);

    public Task<User?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

    public async Task<IReadOnlyList<string>> GetAllowedIpsAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Database
            .SqlQuery<string>($"SELECT ip::text AS \"Value\" FROM users u, unnest(u.allowed_ips) AS ip WHERE u.id = {userId}")
            .ToListAsync(cancellationToken);

    public async Task<UserAccess> GetAccessAsync(Guid userId, string fullName, CancellationToken cancellationToken)
    {
        var roles = await db.Database
            .SqlQuery<string>($"SELECT r.code AS \"Value\" FROM user_roles ur JOIN roles r ON r.id = ur.role_id WHERE ur.user_id = {userId} ORDER BY r.code")
            .ToListAsync(cancellationToken);

        var rows = await db.Database
            .SqlQuery<PermissionRow>($"SELECT DISTINCT rp.permission_code AS \"Code\", rp.scope AS \"Scope\" FROM user_roles ur JOIN role_permissions rp ON rp.role_id = ur.role_id WHERE ur.user_id = {userId}")
            .ToListAsync(cancellationToken);

        // Eyni icazə bir neçə rolda fərqli scope ilə gələ bilər: ən genişi qalır (tenant > branch > own)
        var permissions = rows
            .GroupBy(r => r.Code)
            .Select(g => new PermissionGrant(g.Key, g.MaxBy(r => ScopeRank(r.Scope))!.Scope))
            .OrderBy(p => p.Code, StringComparer.Ordinal)
            .ToList();

        return new UserAccess(userId, fullName, roles, permissions);
    }

    private static int ScopeRank(string scope) => scope switch { "tenant" => 3, "branch" => 2, _ => 1 };

    private sealed record PermissionRow(string Code, string Scope);
}

internal sealed class RefreshTokenRepository(IdentityDbContext db) : IRefreshTokenRepository
{
    public void Add(RefreshToken token) => db.RefreshTokens.Add(token);

    public Task<RefreshToken?> FindByHashAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
        db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    public async Task<bool> TryConsumeAsync(Guid tokenId, Guid replacedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Tək atomik UPDATE: used_at IS NULL şərti paralel iki sorğudan yalnız birinin qalib gəlməsini təmin edir
        var affected = await db.Database.ExecuteSqlAsync(
            $"UPDATE refresh_tokens SET used_at = {now}, replaced_by = {replacedBy} WHERE id = {tokenId} AND used_at IS NULL AND revoked_at IS NULL",
            cancellationToken);
        return affected == 1;
    }

    public async Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync(
            $"UPDATE refresh_tokens SET revoked_at = {now} WHERE family_id = {familyId} AND revoked_at IS NULL",
            cancellationToken);
}
