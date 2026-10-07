using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Auth;
using DentaCore.BuildingBlocks.Infrastructure.Outbox;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.Scheduling.Application;
using DentaCore.Scheduling.Contracts;
using DentaCore.Scheduling.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DentaCore.Scheduling.Infrastructure;

public static class DependencyInjection
{
    /// <summary>IPatientDirectory (Patient modulu) host-da qeydiyyatdan keçməlidir.</summary>
    public static IServiceCollection AddSchedulingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddApplicationCore(typeof(BookAppointmentCommand).Assembly);
        services.AddTenancy(configuration);
        services.AddRequestContext();
        services.Configure<SchedulingOptions>(configuration.GetSection(SchedulingOptions.Section));

        services.AddDbContext<SchedulingDbContext>((sp, options) =>
        {
            var tenant = sp.GetRequiredService<ITenantContext>();
            options
                .UseNpgsql(sp.GetRequiredService<ITenantDataSources>().Get(tenant.Schema))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(new DomainEventsToOutboxInterceptor());
        });
        services.AddScoped<ISchedulingUnitOfWork>(sp => sp.GetRequiredService<SchedulingDbContext>());
        services.AddScoped<IModuleUnitOfWork>(sp => sp.GetRequiredService<SchedulingDbContext>());
        services.AddScoped<ISchedulingRepository, SchedulingRepository>();
        services.AddScoped<IAppointmentLifecycle, AppointmentLifecycle>();
        services.AddScoped<SchedulingReadModel>();
        services.AddScoped<ISchedulingReadModel>(sp => sp.GetRequiredService<SchedulingReadModel>());

        // Patient modulunun default "əlaqə yoxdur" implementasiyasını əsl implementasiya ilə əvəz edir
        services.Replace(ServiceDescriptor.Scoped<ICareRelationships>(sp => sp.GetRequiredService<SchedulingReadModel>()));
        return services;
    }
}
