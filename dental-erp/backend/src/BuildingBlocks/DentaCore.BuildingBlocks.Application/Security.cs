using System.Net;

namespace DentaCore.BuildingBlocks.Application;

/// <summary>
/// PHI sahələrinin qorunması (Mərhələ 2 §1): AES-256-GCM şifrələmə + dəqiq axtarış üçün HMAC blind index.
/// </summary>
public interface IPiiProtector
{
    byte[] Encrypt(string plaintext);

    string Decrypt(byte[] ciphertext);

    /// <summary>Deterministik HMAC-SHA256. Dəyər normallaşdırılır (trim + kiçik hərf), açıq mətn heç yerdə saxlanmır.</summary>
    byte[] BlindIndex(string value);
}

/// <summary>RBAC scope (role_permissions.scope): own &lt; branch &lt; tenant.</summary>
public enum PermissionScope
{
    Own = 1,
    Branch = 2,
    Tenant = 3,
}

/// <summary>Cari sorğunun istifadəçisi. JWT claim-lərindən qurulur.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid UserId { get; }

    IPAddress? Ip { get; }

    PermissionScope? ScopeOf(string permission);

    /// <summary>İcazə var VƏ obyekt scope-a daxildir (tenant: hamısı, branch: istifadəçinin filialları, own: öz yaratdığı).</summary>
    bool CanAccess(string permission, Guid branchId, Guid? ownerUserId = null);

    /// <summary>Siyahı sorğuları üçün filial filtri: null = məhdudiyyət yoxdur; boş = heç nə görə bilməz (own scope-da ayrıca owner filtri istifadə olunur).</summary>
    IReadOnlyCollection<Guid>? BranchFilterFor(string permission);
}

/// <summary>Claim-lərdən qurulan, DB və HTTP-dən asılı olmayan, tam test olunan icazə modeli.</summary>
public sealed class CurrentUser : ICurrentUser
{
    private readonly Dictionary<string, PermissionScope> _permissions;
    private readonly HashSet<Guid> _branches;
    private readonly bool _allBranches;

    public CurrentUser(Guid userId, IEnumerable<string> permClaims, IEnumerable<string> branchClaims, IPAddress? ip)
    {
        ArgumentNullException.ThrowIfNull(permClaims);
        ArgumentNullException.ThrowIfNull(branchClaims);
        UserId = userId;
        Ip = ip;
        IsAuthenticated = userId != Guid.Empty;
        _permissions = [];
        foreach (var claim in permClaims)
        {
            // "code@scope", məs. "patient:read@branch"
            var at = claim.LastIndexOf('@');
            if (at > 0 && TryParseScope(claim[(at + 1)..], out var scope))
            {
                var code = claim[..at];
                if (!_permissions.TryGetValue(code, out var existing) || scope > existing)
                {
                    _permissions[code] = scope;
                }
            }
        }

        _branches = [];
        foreach (var claim in branchClaims)
        {
            if (claim == "*")
            {
                _allBranches = true;
            }
            else if (Guid.TryParse(claim, out var id))
            {
                _branches.Add(id);
            }
        }
    }

    public static CurrentUser Anonymous { get; } = new(Guid.Empty, [], [], null);

    public bool IsAuthenticated { get; }

    public Guid UserId { get; }

    public IPAddress? Ip { get; }

    public PermissionScope? ScopeOf(string permission) =>
        _permissions.TryGetValue(permission, out var scope) ? scope : null;

    public bool CanAccess(string permission, Guid branchId, Guid? ownerUserId = null) =>
        ScopeOf(permission) switch
        {
            PermissionScope.Tenant => true,
            PermissionScope.Branch => _allBranches || _branches.Contains(branchId),
            PermissionScope.Own => ownerUserId is { } owner && owner == UserId,
            _ => false,
        };

    /// <summary>Siyahı sorğuları üçün: icazənin scope-una görə filial filtri (null = məhdudiyyət yoxdur).</summary>
    public IReadOnlyCollection<Guid>? BranchFilterFor(string permission) =>
        ScopeOf(permission) switch
        {
            PermissionScope.Tenant => null,
            PermissionScope.Branch => _allBranches ? null : _branches.ToArray(),
            _ => [],
        };

    private static bool TryParseScope(string value, out PermissionScope scope)
    {
        switch (value)
        {
            case "own": scope = PermissionScope.Own; return true;
            case "branch": scope = PermissionScope.Branch; return true;
            case "tenant": scope = PermissionScope.Tenant; return true;
            default: scope = default; return false;
        }
    }
}

/// <summary>Sorğu bu interfeysi daşıyırsa, Authorization behavior icazəni handler-dən ƏVVƏL yoxlayır.</summary>
public interface IRequiresAccess
{
    string Permission { get; }
}
