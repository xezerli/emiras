using DentaCore.Identity.Application;
using Microsoft.EntityFrameworkCore;

namespace DentaCore.Identity.Infrastructure.Persistence;

/// <summary>user_roles və role_permissions EF-də xəritələnmir (T002), ona görə idarəetmə sorğuları birbaşa SQL ilədir.</summary>
internal sealed class UserAdminStore(IdentityDbContext db) : IUserAdminStore
{
    // Effektiv status (SQL-də CASE ... locked_until > now()): müvəqqəti bloklanmış aktiv hesab "locked" kimi göstərilir

    public async Task<IReadOnlyList<UserRow>> ListUsersAsync((string FullName, Guid Id)? after, string? status, int limit, CancellationToken cancellationToken)
    {
        var afterName = after?.FullName;
        Guid? afterId = after?.Id;
        var rows = await db.Database.SqlQuery<UserDbRow>($@"
            SELECT id, full_name, email_enc, (CASE WHEN status = 'active' AND locked_until > now() THEN 'locked' ELSE status END) AS status,
                   last_login_at, two_factor_enabled
            FROM users
            WHERE deleted_at IS NULL
              AND ({status}::text IS NULL OR (CASE WHEN status = 'active' AND locked_until > now() THEN 'locked' ELSE status END) = {status})
              AND ({afterName}::text IS NULL OR (full_name, id) > ({afterName}, {afterId}::uuid))
            ORDER BY full_name, id
            LIMIT {limit}").ToListAsync(cancellationToken);
        return await WithRolesAsync(rows, cancellationToken);
    }

    public async Task<UserRow?> GetUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQuery<UserDbRow>($@"
            SELECT id, full_name, email_enc, (CASE WHEN status = 'active' AND locked_until > now() THEN 'locked' ELSE status END) AS status,
                   last_login_at, two_factor_enabled
            FROM users WHERE id = {userId} AND deleted_at IS NULL").ToListAsync(cancellationToken);
        var withRoles = await WithRolesAsync(rows, cancellationToken);
        return withRoles.Count == 0 ? null : withRoles[0];
    }

    public async Task<IReadOnlyList<RoleRow>> ListRolesAsync(CancellationToken cancellationToken)
    {
        var roles = await db.Database.SqlQuery<RoleDbRow>($"SELECT code, name, is_system FROM roles ORDER BY code").ToListAsync(cancellationToken);
        var grants = await db.Database.SqlQuery<GrantDbRow>($@"
            SELECT r.code AS role_code, rp.permission_code AS permission, rp.scope, rp.max_amount
            FROM role_permissions rp JOIN roles r ON r.id = rp.role_id ORDER BY rp.permission_code").ToListAsync(cancellationToken);
        var byRole = grants.ToLookup(g => g.RoleCode);
        return roles.Select(r => new RoleRow(r.Code, r.Name, r.IsSystem, byRole[r.Code].Select(g => new RoleGrant(g.Permission, g.Scope, g.MaxAmount)).ToList())).ToList();
    }

    public async Task<IReadOnlyList<PermissionInfo>> ListPermissionsAsync(CancellationToken cancellationToken) =>
        (await db.Database.SqlQuery<PermissionDbRow>($"SELECT code, description FROM permissions ORDER BY code").ToListAsync(cancellationToken))
            .Select(p => new PermissionInfo(p.Code, p.Description)).ToList();

    public async Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(CancellationToken cancellationToken) =>
        (await db.Database.SqlQuery<BranchDbRow>($"SELECT id, code, name FROM branches WHERE is_active ORDER BY code").ToListAsync(cancellationToken))
            .Select(b => new BranchInfo(b.Id, b.Code, b.Name)).ToList();

    public async Task<IReadOnlyList<string>> MissingRolesAsync(IReadOnlyCollection<string> roleCodes, CancellationToken cancellationToken)
    {
        var codes = roleCodes.ToArray();
        return await db.Database.SqlQuery<string>($"SELECT c AS \"Value\" FROM unnest({codes}::text[]) AS c WHERE c NOT IN (SELECT code FROM roles)").ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> MissingBranchesAsync(IReadOnlyCollection<Guid> branchIds, CancellationToken cancellationToken)
    {
        var ids = branchIds.ToArray();
        return await db.Database.SqlQuery<Guid>($"SELECT b AS \"Value\" FROM unnest({ids}::uuid[]) AS b WHERE b NOT IN (SELECT id FROM branches WHERE is_active)").ToListAsync(cancellationToken);
    }

    public async Task ReplaceUserRolesAsync(Guid userId, IReadOnlyCollection<RoleAssignment> assignments, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"DELETE FROM user_roles WHERE user_id = {userId}", cancellationToken);
        foreach (var a in assignments)
        {
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO user_roles(user_id, role_id, branch_id) SELECT {userId}, r.id, {a.BranchId} FROM roles r WHERE r.code = {a.Role}",
                cancellationToken);
        }
    }

    public async Task ReplaceRoleGrantsAsync(string roleCode, IReadOnlyCollection<RoleGrant> grants, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM role_permissions WHERE role_id = (SELECT id FROM roles WHERE code = {roleCode})", cancellationToken);
        foreach (var g in grants)
        {
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO role_permissions(role_id, permission_code, scope, max_amount) SELECT r.id, {g.Permission}, {g.Scope}, {g.MaxAmount} FROM roles r WHERE r.code = {roleCode}",
                cancellationToken);
        }
    }

    private async Task<IReadOnlyList<UserRow>> WithRolesAsync(List<UserDbRow> users, CancellationToken cancellationToken)
    {
        if (users.Count == 0)
        {
            return [];
        }

        var ids = users.Select(u => u.Id).ToArray();
        var roles = await db.Database.SqlQuery<UserRoleDbRow>($@"
            SELECT ur.user_id, r.code AS role, ur.branch_id
            FROM user_roles ur JOIN roles r ON r.id = ur.role_id WHERE ur.user_id = ANY({ids}) ORDER BY r.code").ToListAsync(cancellationToken);
        var byUser = roles.ToLookup(r => r.UserId);
        return users.Select(u => new UserRow(u.Id, u.FullName, u.EmailEnc, u.Status, u.LastLoginAt, u.TwoFactorEnabled,
            byUser[u.Id].Select(r => new RoleAssignment(r.Role, r.BranchId)).ToList())).ToList();
    }

    private sealed record UserDbRow(Guid Id, string FullName, byte[] EmailEnc, string Status, DateTimeOffset? LastLoginAt, bool TwoFactorEnabled);

    private sealed record UserRoleDbRow(Guid UserId, string Role, Guid? BranchId);

    private sealed record BranchDbRow(Guid Id, string Code, string Name);

    private sealed record RoleDbRow(string Code, string Name, bool IsSystem);

    private sealed record GrantDbRow(string RoleCode, string Permission, string Scope, decimal? MaxAmount);

    private sealed record PermissionDbRow(string Code, string? Description);
}
