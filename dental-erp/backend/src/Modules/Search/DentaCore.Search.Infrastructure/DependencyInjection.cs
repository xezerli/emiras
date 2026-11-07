using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Auth;
using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.Search.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DentaCore.Search.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Axtarış tərəfi (sorğular). ClinicCore çağırır.</summary>
    public static IServiceCollection AddSearchModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddApplicationCore(typeof(FindPatientsQuery).Assembly);
        services.AddTenancy(configuration);
        services.AddRequestContext();
        services.Configure<ElasticOptions>(configuration.GetSection(ElasticOptions.Section));
        services.AddHttpClient<ElasticClient>((sp, client) => ElasticHttp.Configure(client, sp.GetRequiredService<IOptions<ElasticOptions>>().Value));
        services.AddScoped<IPatientSearch, ElasticPatientSearch>();
        services.AddScoped<IAuditSearch, ElasticAuditSearch>();
        return services;
    }

    /// <summary>İndeksləmə tərəfi (Workers): audit consumer-i və pasiyent indexer-i. <c>AddMessaging</c> və <c>AddSearchModule</c> əvvəlcədən çağırılmalıdır.</summary>
    public static IServiceCollection AddSearchIndexing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<PatientIndexer>();
        services.AddSingleton<PatientIndexerService>();
        services.AddHostedService(sp => sp.GetRequiredService<PatientIndexerService>());
        services.AddIntegrationConsumer<AuditIndexConsumer>();
        return services;
    }
}
