using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Auth;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.Dashboard.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DentaCore.Dashboard.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Keş: <c>ConnectionStrings:Redis</c> verilibsə Redis, yoxsa proses daxili yaddaş (lokal/tək nüsxə).</summary>
    public static IServiceCollection AddDashboardModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddApplicationCore(typeof(GetDashboardSummaryQuery).Assembly);
        services.AddTenancy(configuration);
        services.AddRequestContext();
        services.Configure<DashboardOptions>(configuration.GetSection(DashboardOptions.Section));
        var redis = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redis))
        {
            services.AddDistributedMemoryCache();
        }
        else
        {
            services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = redis;
                o.InstanceName = "dentacore:";
            });
        }

        services.AddScoped<SqlDashboardReader>();
        services.AddScoped<IDashboardReader, CachedDashboardReader>();
        return services;
    }
}
