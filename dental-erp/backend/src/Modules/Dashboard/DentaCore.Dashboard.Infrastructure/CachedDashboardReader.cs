using System.Text.Json;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.Dashboard.Application;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace DentaCore.Dashboard.Infrastructure;

/// <summary>
/// Qısa müddətli keş (Redis, sınaqda yaddaş). Açar tenant sxemini, filial dəstini və günü daşıyır: bir tenant-ın nəticəsi başqasına,
/// bir filial dəstinin nəticəsi başqa filial dəstinə verilə bilməz. Keş əlçatmaz olarsa sorğu birbaşa DB-yə düşür (keş uğursuzluğu xəta deyil).
/// Köhnəlmə müddəti: <see cref="DashboardOptions.CacheSeconds"/>; hadisə ilə ləğvetmə yoxdur.
/// </summary>
internal sealed class CachedDashboardReader(SqlDashboardReader inner, IDistributedCache cache, ITenantContext tenant, IOptions<DashboardOptions> options) : IDashboardReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DashboardSummaryDto> ReadAsync(IReadOnlyCollection<Guid>? branches, DateOnly date, CancellationToken cancellationToken)
    {
        var ttl = TimeSpan.FromSeconds(options.Value.CacheSeconds);
        if (ttl <= TimeSpan.Zero)
        {
            return await inner.ReadAsync(branches, date, cancellationToken);
        }

        var branchKey = branches is null ? "*" : string.Join(',', branches.Select(b => b.ToString("N")).Order(StringComparer.Ordinal));
        var key = $"dash:{tenant.Schema}:{date:yyyy-MM-dd}:{branchKey}";
        try
        {
            var cached = await cache.GetStringAsync(key, cancellationToken);
            if (cached is not null && JsonSerializer.Deserialize<DashboardSummaryDto>(cached, Json) is { } hit)
            {
                return hit;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // keş oxunmadı: DB-dən oxuyuruq
        }

        var fresh = await inner.ReadAsync(branches, date, cancellationToken);
        try
        {
            await cache.SetStringAsync(
                key, JsonSerializer.Serialize(fresh, Json), new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // keş yazılmadı: nəticə yenə düzgündür
        }

        return fresh;
    }
}
