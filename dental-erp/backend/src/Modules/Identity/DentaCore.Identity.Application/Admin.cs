using System.Text;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Identity.Domain;
using FluentValidation;

namespace DentaCore.Identity.Application;

public static class AdminPermissions
{
    public const string UserManage = "user:manage";

    /// <summary>İdarəçi rolları. Redaktə olunmur ki, heç kim özünü və ya başqalarını sistemdən kilidləyə bilməsin.</summary>
    public static readonly IReadOnlySet<string> ProtectedRoles = new HashSet<string>(StringComparer.Ordinal) { "director", "clinic_admin" };

    public static readonly IReadOnlySet<string> Scopes = new HashSet<string>(StringComparer.Ordinal) { "own", "branch", "tenant" };
}

public static class PasswordPolicy
{
    public const int MinLength = 10;

    public static IRuleBuilderOptions<T, string> MeetsPolicy<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MinimumLength(MinLength).MaximumLength(128)
            .Must(p => p.Any(char.IsLetter) && p.Any(char.IsDigit))
            .WithMessage($"Password must be at least {MinLength} characters and contain a letter and a digit.");
}

// ---------------- Rollar ----------------
public sealed record RoleDto(string Code, string Name, bool IsSystem, bool Editable, IReadOnlyList<RoleGrant> Permissions);

public sealed record RolesDto(IReadOnlyList<RoleDto> Roles, IReadOnlyList<PermissionInfo> Permissions);

public sealed record ListRolesQuery : IQuery<RolesDto>, IRequiresAccess
{
    public string Permission => AdminPermissions.UserManage;
}

internal sealed class ListRolesQueryHandler(IUserAdminStore store) : IRequestHandler<ListRolesQuery, RolesDto>
{
    public async Task<Result<RolesDto>> Handle(ListRolesQuery request, CancellationToken cancellationToken)
    {
        var roles = await store.ListRolesAsync(cancellationToken);
        var permissions = await store.ListPermissionsAsync(cancellationToken);
        return new RolesDto(
            roles.Select(r => new RoleDto(r.Code, r.Name, r.IsSystem, !AdminPermissions.ProtectedRoles.Contains(r.Code), r.Grants)).ToList(),
            permissions);
    }
}

public sealed record SetRolePermissionsCommand(string RoleCode, IReadOnlyList<RoleGrant> Grants)
    : ICommand<RoleDto>, IRequiresAccess, IAuditable<RoleDto>
{
    public string Permission => AdminPermissions.UserManage;

    public AuditDescriptor Describe(RoleDto response) =>
        new("role.permissions.set", "role", null, $"{{\"role\":\"{RoleCode}\",\"count\":{Grants.Count}}}");
}

public sealed class SetRolePermissionsCommandValidator : AbstractValidator<SetRolePermissionsCommand>
{
    public SetRolePermissionsCommandValidator()
    {
        RuleFor(x => x.RoleCode).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Grants).NotNull().Must(g => g.Select(x => x.Permission).Distinct(StringComparer.Ordinal).Count() == g.Count)
            .WithMessage("Duplicate permission.");
        RuleForEach(x => x.Grants).ChildRules(g =>
        {
            g.RuleFor(x => x.Permission).NotEmpty().MaximumLength(64);
            g.RuleFor(x => x.Scope).Must(AdminPermissions.Scopes.Contains).WithMessage("Scope must be own, branch or tenant.");
            g.RuleFor(x => x.MaxAmount).GreaterThanOrEqualTo(0).When(x => x.MaxAmount.HasValue);
        });
    }
}

internal sealed class SetRolePermissionsCommandHandler(IUserAdminStore store, IIdentityUnitOfWork unitOfWork)
    : IRequestHandler<SetRolePermissionsCommand, RoleDto>
{
    public async Task<Result<RoleDto>> Handle(SetRolePermissionsCommand request, CancellationToken cancellationToken)
    {
        var role = (await store.ListRolesAsync(cancellationToken)).FirstOrDefault(r => r.Code == request.RoleCode);
        if (role is null)
        {
            return Error.NotFound("role.not_found", "Role not found.");
        }

        if (AdminPermissions.ProtectedRoles.Contains(role.Code))
        {
            return Error.Conflict("role.protected", "Administrator roles cannot be changed.");
        }

        var known = (await store.ListPermissionsAsync(cancellationToken)).Select(p => p.Code).ToHashSet(StringComparer.Ordinal);
        var unknown = request.Grants.Where(g => !known.Contains(g.Permission)).Select(g => g.Permission).ToList();
        if (unknown.Count > 0)
        {
            return Error.Validation("role.unknown_permission", $"Unknown permission: {string.Join(", ", unknown)}.");
        }

        // Yalnız qorunan rollar idarəetmə hüququ daşıyır: admin başqa rola bu hüququ verib hüquq genişləndirə bilməz
        if (request.Grants.Any(g => g.Permission == AdminPermissions.UserManage))
        {
            return Error.Validation("role.permission_not_assignable", $"{AdminPermissions.UserManage} cannot be granted to this role.");
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await store.ReplaceRoleGrantsAsync(role.Code, request.Grants, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RoleDto(role.Code, role.Name, role.IsSystem, Editable: true, request.Grants.OrderBy(g => g.Permission, StringComparer.Ordinal).ToList());
    }
}

// ---------------- İstifadəçilər ----------------
/// <param name="Email">Maskalanmış (ilk hərf və domen). İstifadəçi siyahısında tam ünvan göstərilmir.</param>
public sealed record UserDto(Guid Id, string FullName, string Email, string Status, DateTimeOffset? LastLoginAt, bool TwoFactorEnabled, IReadOnlyList<RoleAssignment> Roles);

public sealed record UserPage(IReadOnlyList<UserDto> Items, string? NextCursor);

internal static class UserMapper
{
    public static UserDto ToDto(UserRow row, IPiiProtector pii) =>
        new(row.Id, row.FullName, MaskEmail(pii.Decrypt(row.EmailEnc)), row.Status, row.LastLoginAt, row.TwoFactorEnabled, row.Roles);

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 1 ? "***" + (at < 0 ? string.Empty : email[at..]) : $"{email[0]}***{email[at..]}";
    }
}

public sealed record ListUsersQuery(string? Status, string? Cursor, int Limit) : IQuery<UserPage>, IRequiresAccess
{
    public string Permission => AdminPermissions.UserManage;
}

public sealed class ListUsersQueryValidator : AbstractValidator<ListUsersQuery>
{
    public ListUsersQueryValidator()
    {
        RuleFor(x => x.Limit).InclusiveBetween(1, 100);
        RuleFor(x => x.Status).Must(s => s is null or "active" or "locked" or "disabled").WithMessage("Status must be active, locked or disabled.");
    }
}

internal sealed class ListUsersQueryHandler(IUserAdminStore store, IPiiProtector pii) : IRequestHandler<ListUsersQuery, UserPage>
{
    public async Task<Result<UserPage>> Handle(ListUsersQuery request, CancellationToken cancellationToken)
    {
        (string, Guid)? after = null;
        if (request.Cursor is not null)
        {
            var decoded = DecodeCursor(request.Cursor);
            if (decoded is null)
            {
                return Error.Validation("cursor.invalid", "Cursor is invalid.");
            }

            after = decoded;
        }

        var rows = await store.ListUsersAsync(after, request.Status, request.Limit + 1, cancellationToken);
        var page = rows.Take(request.Limit).ToList();
        var next = rows.Count > request.Limit ? EncodeCursor(page[^1]) : null;
        return new UserPage(page.Select(r => UserMapper.ToDto(r, pii)).ToList(), next);
    }

    private static string EncodeCursor(UserRow row) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{row.Id:N}|{row.FullName}"));

    private static (string, Guid)? DecodeCursor(string cursor)
    {
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var sep = text.IndexOf('|', StringComparison.Ordinal);
            return sep == 32 && Guid.TryParseExact(text[..sep], "N", out var id) ? (text[(sep + 1)..], id) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public sealed record GetUserQuery(Guid Id) : IQuery<UserDto>, IRequiresAccess
{
    public string Permission => AdminPermissions.UserManage;
}

internal sealed class GetUserQueryHandler(IUserAdminStore store, IPiiProtector pii) : IRequestHandler<GetUserQuery, UserDto>
{
    public async Task<Result<UserDto>> Handle(GetUserQuery request, CancellationToken cancellationToken)
    {
        var row = await store.GetUserAsync(request.Id, cancellationToken);
        return row is null ? Error.NotFound("user.not_found", "User not found.") : UserMapper.ToDto(row, pii);
    }
}

internal static class AssignmentRules
{
    public static async Task<Error?> ValidateAsync(IUserAdminStore store, IReadOnlyList<RoleAssignment> assignments, CancellationToken cancellationToken)
    {
        var missingRoles = await store.MissingRolesAsync(assignments.Select(a => a.Role).ToList(), cancellationToken);
        if (missingRoles.Count > 0)
        {
            return Error.Validation("user.unknown_role", $"Unknown role: {string.Join(", ", missingRoles)}.");
        }

        var branches = assignments.Where(a => a.BranchId.HasValue).Select(a => a.BranchId!.Value).Distinct().ToList();
        var missingBranches = await store.MissingBranchesAsync(branches, cancellationToken);
        return missingBranches.Count > 0 ? Error.Validation("user.unknown_branch", "Branch not found or inactive.") : null;
    }

    public static IRuleBuilderOptions<T, IReadOnlyList<RoleAssignment>> ValidAssignments<T>(this IRuleBuilder<T, IReadOnlyList<RoleAssignment>> rule) =>
        rule.NotNull().Must(a => a.Count is >= 1 and <= 10).WithMessage("Provide between 1 and 10 roles.")
            .Must(a => a.Select(x => x.Role).Distinct(StringComparer.Ordinal).Count() == a.Count).WithMessage("A role can be assigned once.")
            .Must(a => a.All(x => !string.IsNullOrWhiteSpace(x.Role))).WithMessage("Role is required.");
}

public sealed record CreateUserCommand(string Email, string FullName, string Password, IReadOnlyList<RoleAssignment> Roles)
    : ICommand<UserDto>, IRequiresAccess, IAuditable<UserDto>
{
    public string Permission => AdminPermissions.UserManage;

    public AuditDescriptor Describe(UserDto response) =>
        new("user.create", "user", response?.Id, $"{{\"roles\":[{string.Join(",", Roles.Select(r => $"\"{r.Role}\""))}]}}");
}

public sealed class CreateUserCommandValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254).EmailAddress();
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Password).MeetsPolicy();
        RuleFor(x => x.Roles).ValidAssignments();
    }
}

internal sealed class CreateUserCommandHandler(
    IUserRepository users,
    IUserAdminStore store,
    IPiiProtector pii,
    IPasswordHasher hasher,
    IIdentityUnitOfWork unitOfWork) : IRequestHandler<CreateUserCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(CreateUserCommand request, CancellationToken cancellationToken)
    {
        var emailHash = pii.BlindIndex(request.Email);
        if (await users.FindByEmailHashAsync(emailHash, cancellationToken) is not null)
        {
            return Error.Conflict("user.email_exists", "A user with this email already exists.");
        }

        if (await AssignmentRules.ValidateAsync(store, request.Roles, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var user = User.Register(Guid.NewGuid(), emailHash, pii.Encrypt(request.Email.Trim().ToLowerInvariant()), hasher.Hash(request.Password), request.FullName);

        // user_roles EF-də xəritələnmir: istifadəçi və rolları bir tranzaksiyada yazılır
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            users.Add(user);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await store.ReplaceUserRolesAsync(user.Id, request.Roles, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var row = await store.GetUserAsync(user.Id, cancellationToken);
        return UserMapper.ToDto(row!, pii);
    }
}

public sealed record RenameUserCommand(Guid Id, string FullName) : ICommand<UserDto>, IRequiresAccess, IAuditable<UserDto>
{
    public string Permission => AdminPermissions.UserManage;

    public AuditDescriptor Describe(UserDto response) => new("user.rename", "user", Id);
}

public sealed class RenameUserCommandValidator : AbstractValidator<RenameUserCommand>
{
    public RenameUserCommandValidator() => RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
}

internal sealed class RenameUserCommandHandler(IUserRepository users, IUserAdminStore store, IPiiProtector pii, IIdentityUnitOfWork unitOfWork)
    : IRequestHandler<RenameUserCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(RenameUserCommand request, CancellationToken cancellationToken)
    {
        var user = await users.GetAsync(request.Id, cancellationToken);
        if (user is null)
        {
            return Error.NotFound("user.not_found", "User not found.");
        }

        user.Rename(request.FullName);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return UserMapper.ToDto((await store.GetUserAsync(user.Id, cancellationToken))!, pii);
    }
}

public sealed record SetUserRolesCommand(Guid Id, IReadOnlyList<RoleAssignment> Roles) : ICommand<UserDto>, IRequiresAccess, IAuditable<UserDto>
{
    public string Permission => AdminPermissions.UserManage;

    public AuditDescriptor Describe(UserDto response) =>
        new("user.roles.set", "user", Id, $"{{\"roles\":[{string.Join(",", Roles.Select(r => $"\"{r.Role}\""))}]}}");
}

public sealed class SetUserRolesCommandValidator : AbstractValidator<SetUserRolesCommand>
{
    public SetUserRolesCommandValidator() => RuleFor(x => x.Roles).ValidAssignments();
}

internal sealed class SetUserRolesCommandHandler(
    IUserRepository users,
    IUserAdminStore store,
    IRefreshTokenRepository refreshTokens,
    IPiiProtector pii,
    ICurrentUser current,
    IIdentityUnitOfWork unitOfWork,
    IClock clock) : IRequestHandler<SetUserRolesCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(SetUserRolesCommand request, CancellationToken cancellationToken)
    {
        // Öz rolunu dəyişən idarəçi özünü kilidləyə bilər; son idarəçi qorunması da buna əsaslanır (icra edən həmişə aktiv idarəçidir)
        if (request.Id == current.UserId)
        {
            return Error.Forbidden("user.self_change", "You cannot change your own roles.");
        }

        if (await users.GetAsync(request.Id, cancellationToken) is null)
        {
            return Error.NotFound("user.not_found", "User not found.");
        }

        if (await AssignmentRules.ValidateAsync(store, request.Roles, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            await store.ReplaceUserRolesAsync(request.Id, request.Roles, cancellationToken);
            // Köhnə access token ≤10 dəq. qüvvədə qalır; yeni icazələr refresh-dən sonra gəlsin deyə sessiyalar bağlanır
            await refreshTokens.RevokeAllForUserAsync(request.Id, clock.UtcNow, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return UserMapper.ToDto((await store.GetUserAsync(request.Id, cancellationToken))!, pii);
    }
}

public sealed record SetUserStatusCommand(Guid Id, bool Enabled) : ICommand<UserDto>, IRequiresAccess, IAuditable<UserDto>
{
    public string Permission => AdminPermissions.UserManage;

    public AuditDescriptor Describe(UserDto response) => new(Enabled ? "user.enable" : "user.disable", "user", Id);
}

internal sealed class SetUserStatusCommandHandler(
    IUserRepository users,
    IUserAdminStore store,
    IRefreshTokenRepository refreshTokens,
    IPiiProtector pii,
    ICurrentUser current,
    IIdentityUnitOfWork unitOfWork,
    IClock clock) : IRequestHandler<SetUserStatusCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(SetUserStatusCommand request, CancellationToken cancellationToken)
    {
        if (!request.Enabled && request.Id == current.UserId)
        {
            return Error.Forbidden("user.self_change", "You cannot disable your own account.");
        }

        var user = await users.GetAsync(request.Id, cancellationToken);
        if (user is null)
        {
            return Error.NotFound("user.not_found", "User not found.");
        }

        if (request.Enabled)
        {
            user.Enable();
        }
        else
        {
            user.Disable();
        }

        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            if (!request.Enabled)
            {
                await refreshTokens.RevokeAllForUserAsync(user.Id, clock.UtcNow, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return UserMapper.ToDto((await store.GetUserAsync(user.Id, cancellationToken))!, pii);
    }
}

public sealed record ResetPasswordCommand(Guid Id, string NewPassword) : ICommand<Unit>, IRequiresAccess, IAuditable<Unit>
{
    public string Permission => AdminPermissions.UserManage;

    public AuditDescriptor Describe(Unit response) => new("user.password.reset", "user", Id);
}

public sealed class ResetPasswordCommandValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordCommandValidator() => RuleFor(x => x.NewPassword).MeetsPolicy();
}

internal sealed class ResetPasswordCommandHandler(
    IUserRepository users,
    IPasswordHasher hasher,
    IRefreshTokenRepository refreshTokens,
    IIdentityUnitOfWork unitOfWork,
    IClock clock) : IRequestHandler<ResetPasswordCommand, Unit>
{
    public async Task<Result<Unit>> Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await users.GetAsync(request.Id, cancellationToken);
        if (user is null)
        {
            return Error.NotFound("user.not_found", "User not found.");
        }

        user.ChangePassword(hasher.Hash(request.NewPassword));

        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await refreshTokens.RevokeAllForUserAsync(user.Id, clock.UtcNow, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return new Unit();
    }
}
