using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Auth;
using DentaCore.BuildingBlocks.Infrastructure.Outbox;
using DentaCore.BuildingBlocks.Infrastructure.Security;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.Patient.Application;
using DentaCore.Patient.Contracts;
using DentaCore.Scheduling.Contracts;
using DentaCore.Patient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DentaCore.Patient.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPatientModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddApplicationCore(typeof(RegisterPatientCommand).Assembly);
        services.AddTenancy(configuration);
        services.AddRequestContext();
        services.Configure<PatientOptions>(configuration.GetSection(PatientOptions.Section));
        services.Configure<PiiOptions>(configuration.GetSection(PiiOptions.Section));
        services.TryAddSingleton<IPiiProtector, AesGcmPiiProtector>();

        services.AddDbContext<PatientDbContext>((sp, options) =>
        {
            var tenant = sp.GetRequiredService<ITenantContext>();
            options
                .UseNpgsql(sp.GetRequiredService<ITenantDataSources>().Get(tenant.Schema))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(new DomainEventsToOutboxInterceptor());
        });
        services.AddScoped<IPatientUnitOfWork>(sp => sp.GetRequiredService<PatientDbContext>());
        services.AddScoped<IModuleUnitOfWork>(sp => sp.GetRequiredService<PatientDbContext>());
        services.AddScoped<IPatientRepository, PatientRepository>();
        services.AddScoped<IPatientReadModel, PatientReadModel>();
        services.AddScoped<PatientAccess>();
        services.AddScoped<IPatientDirectory, PatientDirectory>();
        services.TryAddScoped<ICareRelationships, NoCareRelationships>();   // Scheduling modulu qoşulubsa əvəz olunur
        return services;
    }
}
