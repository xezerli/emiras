using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Auth;
using DentaCore.BuildingBlocks.Infrastructure.Outbox;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.Clinical.Application;
using DentaCore.Clinical.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DentaCore.Clinical.Infrastructure;

public static class DependencyInjection
{
    /// <summary>IPatientDirectory, ICareRelationships və IAppointmentLifecycle (Patient və Scheduling modulları) host-da qeydiyyatdan keçməlidir.</summary>
    public static IServiceCollection AddClinicalModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddApplicationCore(typeof(StartVisitCommand).Assembly);
        services.AddTenancy(configuration);
        services.AddRequestContext();
        services.AddDbContext<ClinicalDbContext>((sp, options) =>
        {
            var tenant = sp.GetRequiredService<ITenantContext>();
            options
                .UseNpgsql(sp.GetRequiredService<ITenantDataSources>().Get(tenant.Schema))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(new DomainEventsToOutboxInterceptor());
        });
        services.AddScoped<IClinicalUnitOfWork>(sp => sp.GetRequiredService<ClinicalDbContext>());
        services.AddScoped<IModuleUnitOfWork>(sp => sp.GetRequiredService<ClinicalDbContext>());
        services.AddScoped<IClinicalRepository, ClinicalRepository>();
        services.AddScoped<ClinicalAccess>();
        return services;
    }
}
