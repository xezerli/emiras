using DentaCore.Billing.Application;
using DentaCore.Billing.Infrastructure.Persistence;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Auth;
using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using DentaCore.BuildingBlocks.Infrastructure.Outbox;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DentaCore.Billing.Infrastructure;

public static class DependencyInjection
{
    /// <summary>IPatientDirectory və ICareRelationships (Patient və Scheduling modulları) host-da qeydiyyatdan keçməlidir.</summary>
    public static IServiceCollection AddBillingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddApplicationCore(typeof(CreateInvoiceCommand).Assembly);
        services.AddTenancy(configuration);
        services.AddRequestContext();
        services.Configure<BillingOptions>(configuration.GetSection(BillingOptions.Section));
        services.AddDbContext<BillingDbContext>((sp, options) =>
        {
            var tenant = sp.GetRequiredService<ITenantContext>();
            options
                .UseNpgsql(sp.GetRequiredService<ITenantDataSources>().Get(tenant.Schema))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(new DomainEventsToOutboxInterceptor());
        });
        services.AddScoped<IBillingUnitOfWork>(sp => sp.GetRequiredService<BillingDbContext>());
        services.AddScoped<IModuleUnitOfWork>(sp => sp.GetRequiredService<BillingDbContext>());
        services.AddScoped<IBillingRepository, BillingRepository>();
        services.AddScoped<IRefundLimits, RefundLimits>();
        services.AddScoped<BillingAccess>();
        return services;
    }

    /// <summary>Workers host-u çağırır: Billing modulunun mesaj consumer-ləri.</summary>
    public static IServiceCollection AddBillingIntegrationConsumers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIntegrationConsumer<ProcedurePerformedConsumer>();
        return services;
    }
}
