using System.Globalization;
using System.Text.Json;

namespace DentaCore.Notify.Application;

/// <summary>
/// Klientə gedən bildiriş. Yalnız "nə baş verdi və hansı obyektdə": məbləğ, ad, diaqnoz kimi məzmun YOXDUR.
/// Klient dəyişikliyi bilib öz səlahiyyəti ilə API-dən oxuyur, ona görə bildiriş kanalı məlumat sızdıra bilməz.
/// </summary>
public sealed record Notification(string Event, Guid? EntityId, Guid? PatientId, Guid? BranchId, DateTimeOffset At);

public sealed record RoutedNotification(Notification Notification, IReadOnlyList<string> Groups);

/// <summary>Bildiriş sahələri: hər sahə üçün oxuma icazəsi tələb olunur.</summary>
public static class NotificationAreas
{
    public const string Appointments = "appointments";
    public const string Billing = "billing";
    public const string Clinical = "clinical";

    /// <summary>sahə → onu görmək üçün icazə.</summary>
    public static readonly IReadOnlyDictionary<string, string> Permissions = new Dictionary<string, string>
    {
        [Appointments] = "appointment:read",
        [Billing] = "invoice:read",
        [Clinical] = "clinical:read",
    };

    public static string? OfRoutingKey(string routingKey)
    {
        ArgumentNullException.ThrowIfNull(routingKey);
        if (routingKey.StartsWith("scheduling.", StringComparison.Ordinal))
        {
            return Appointments;
        }

        if (routingKey.StartsWith("billing.", StringComparison.Ordinal))
        {
            return Billing;
        }

        return routingKey.StartsWith("clinical.", StringComparison.Ordinal) ? Clinical : null;
    }
}

/// <summary>
/// Qrup adları. Tenant HƏR qrupun adındadır, ona görə bir tenant-ın istifadəçisi başqasının qrupuna düşə bilməz
/// (tenant id token claim-indən gəlir, klient seçmir).
/// </summary>
public static class GroupNames
{
    public static string Tenant(Guid tenantId, string area) => string.Create(CultureInfo.InvariantCulture, $"t:{tenantId:N}:{area}");

    public static string Branch(Guid tenantId, string area, Guid branchId) => string.Create(CultureInfo.InvariantCulture, $"t:{tenantId:N}:{area}:b:{branchId:N}");

    public static string User(Guid tenantId, string area, Guid userId) => string.Create(CultureInfo.InvariantCulture, $"t:{tenantId:N}:{area}:u:{userId:N}");
}

/// <summary>Hadisə → bildiriş + hədəf qruplar. Təmiz məntiqdir (broker və SignalR-dan asılı deyil), tam unit-test olunur.</summary>
public static class NotificationRouter
{
    private static readonly string[] EntityKeys = ["appointmentId", "invoiceId", "visitId", "planId"];

    public static RoutedNotification? Route(string routingKey, Guid tenantId, DateTimeOffset occurredAt, JsonElement payload)
    {
        var area = NotificationAreas.OfRoutingKey(routingKey);
        if (area is null)
        {
            return null;
        }

        var obj = payload.ValueKind == JsonValueKind.Object ? payload : default;
        var entityId = EntityKeys.Select(k => ReadGuid(obj, k)).FirstOrDefault(v => v is not null);
        var patientId = ReadGuid(obj, "patientId");
        var branchId = ReadGuid(obj, "branchId");
        var providerId = ReadGuid(obj, "providerId");

        // Tenant-scope abunəçilər hamısını görür; filial-scope yalnız öz filialını; own-scope yalnız özünə aid olanı
        var groups = new List<string> { GroupNames.Tenant(tenantId, area) };
        if (branchId is { } branch)
        {
            groups.Add(GroupNames.Branch(tenantId, area, branch));
        }

        if (providerId is { } provider)
        {
            groups.Add(GroupNames.User(tenantId, area, provider));
        }

        return new RoutedNotification(new Notification(routingKey, entityId, patientId, branchId, occurredAt), groups);
    }

    private static Guid? ReadGuid(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String
                && Guid.TryParse(property.Value.GetString(), out var id))
            {
                return id;
            }
        }

        return null;
    }
}

/// <summary>Bağlantı zamanı istifadəçinin token claim-lərindən hansı qruplara qoşulacağını müəyyənləşdirir.</summary>
public static class GroupMembership
{
    public static IReadOnlyList<string> For(Guid tenantId, Guid userId, IEnumerable<string> permClaims, IEnumerable<string> branchClaims)
    {
        ArgumentNullException.ThrowIfNull(permClaims);
        ArgumentNullException.ThrowIfNull(branchClaims);
        var branches = branchClaims.ToList();
        var allBranches = branches.Contains("*", StringComparer.Ordinal);
        var branchIds = branches.Select(b => Guid.TryParse(b, out var id) ? id : (Guid?)null).Where(id => id is not null).Select(id => id!.Value).ToList();
        var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var claim in permClaims)
        {
            var at = claim.LastIndexOf('@');
            if (at <= 0)
            {
                continue;
            }

            var code = claim[..at];
            var scope = claim[(at + 1)..];
            if (!scopes.TryGetValue(code, out var existing) || Rank(scope) > Rank(existing))
            {
                scopes[code] = scope;
            }
        }

        var groups = new List<string>();
        foreach (var (area, permission) in NotificationAreas.Permissions)
        {
            if (!scopes.TryGetValue(permission, out var scope))
            {
                continue;
            }

            switch (scope)
            {
                case "tenant":
                    groups.Add(GroupNames.Tenant(tenantId, area));
                    break;
                case "branch" when allBranches:
                    groups.Add(GroupNames.Tenant(tenantId, area));
                    break;
                case "branch":
                    groups.AddRange(branchIds.Select(b => GroupNames.Branch(tenantId, area, b)));
                    break;
                case "own":
                    groups.Add(GroupNames.User(tenantId, area, userId));
                    break;
            }
        }

        return groups;
    }

    private static int Rank(string scope) => scope switch { "tenant" => 3, "branch" => 2, "own" => 1, _ => 0 };
}
