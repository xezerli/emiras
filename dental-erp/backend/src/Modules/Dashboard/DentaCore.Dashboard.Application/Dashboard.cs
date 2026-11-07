using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using Microsoft.Extensions.Options;

namespace DentaCore.Dashboard.Application;

public static class DashboardPermissions
{
    public const string Read = "report:read";
}

public sealed class DashboardOptions
{
    public const string Section = "Dashboard";

    /// <summary>"Bu gün" klinikanın yerli tarixidir.</summary>
    public string TimeZone { get; set; } = "Asia/Baku";

    /// <summary>Eyni tenant/filial/gün üçün nəticə bu müddət keşlənir (Mərhələ 3: 30 san).</summary>
    public int CacheSeconds { get; set; } = 30;
}

public sealed record AppointmentsToday(int Total, int Completed, int NoShow, int Waiting);

public sealed record TopService(string Name, int Count, decimal Revenue);

public sealed record TopProvider(Guid ProviderId, string Name, decimal Revenue);

public sealed record RoomState(Guid RoomId, string Name, string State);

public sealed record RevenuePoint(DateOnly Day, decimal Amount);

/// <summary>
/// Dashboard göstəriciləri. Gəlir = qəbul olunan ödənişlər − geri qaytarmalar (nağd əsaslı, ödəniş tarixinə görə).
/// Pul məbləğləri tək valyutalı hesab olunur (hər tenant bir valyuta).
/// </summary>
public sealed record DashboardSummaryDto(
    DateOnly Date,
    AppointmentsToday AppointmentsToday,
    decimal RevenueToday,
    decimal RevenueMonth,
    decimal OutstandingDebt,
    int NewPatients,
    IReadOnlyList<TopService> TopServices,
    IReadOnlyList<TopProvider> TopProviders,
    IReadOnlyList<RoomState> Rooms,
    IReadOnlyList<RevenuePoint> RevenueSeries);

/// <summary>Oxuma tərəfi (CQRS): tenant sxemindən birbaşa, yalnız oxuma. Branches null = məhdudiyyətsiz.</summary>
public interface IDashboardReader
{
    Task<DashboardSummaryDto> ReadAsync(IReadOnlyCollection<Guid>? branches, DateOnly date, CancellationToken cancellationToken);
}

public sealed record GetDashboardSummaryQuery(Guid? BranchId, DateOnly? Date) : IQuery<DashboardSummaryDto>, IRequiresAccess
{
    public string Permission => DashboardPermissions.Read;
}

/// <summary>
/// Scope: tenant hamısını (və ya seçilmiş filialı), branch yalnız öz filiallarını görür. own scope-un dashboard üçün mənası yoxdur.
/// Başqa filialın id-si verilərsə "tapılmadı" qaytarılır ki, filial varlığı sızmasın.
/// </summary>
internal sealed class GetDashboardSummaryQueryHandler(IDashboardReader reader, ICurrentUser user, IClock clock, IOptions<DashboardOptions> options)
    : IRequestHandler<GetDashboardSummaryQuery, DashboardSummaryDto>
{
    public async Task<Result<DashboardSummaryDto>> Handle(GetDashboardSummaryQuery request, CancellationToken cancellationToken)
    {
        var scope = user.ScopeOf(DashboardPermissions.Read);
        if (scope is null or PermissionScope.Own)
        {
            return Error.Forbidden("dashboard.scope_insufficient", "The dashboard needs branch or tenant scope.");
        }

        var allowed = user.BranchFilterFor(DashboardPermissions.Read);   // null = bütün filiallar
        IReadOnlyCollection<Guid>? branches = allowed;
        if (request.BranchId is { } requested)
        {
            if (allowed is not null && !allowed.Contains(requested))
            {
                return Error.NotFound("branch.not_found", "Branch not found.");
            }

            branches = [requested];
        }
        else if (allowed is { Count: 0 })
        {
            return Error.Forbidden("dashboard.no_branches", "Your account has no branches.");
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).DateTime);
        var date = request.Date ?? today;
        if (date > today)
        {
            return Error.Validation("dashboard.future_date", "The date cannot be in the future.");
        }

        return await reader.ReadAsync(branches, date, cancellationToken);
    }
}
