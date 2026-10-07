using System.Reflection;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace DentaCore.BuildingBlocks.Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>RabbitMQ seçimləri, publisher və tenant siyahısı. Workers host-u çağırır.</summary>
    public static IServiceCollection AddMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddTenancy(configuration);
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.Section));
        services.TryAddSingleton<IEventPublisher, RabbitMqPublisher>();
        services.TryAddSingleton<ITenantEnumerator>(sp => new NpgsqlTenantEnumerator(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<OutboxProcessor>();
        services.TryAddSingleton<TenantMaintenance>();
        return services;
    }

    /// <summary>
    /// Sıra vacibdir: consumer host-lar (topologiya) publisher servisindən ƏVVƏL qeydiyyatdan keçməlidir.
    /// Ona görə Workers əvvəlcə AddIntegrationConsumer, sonra bu metodu çağırır.
    /// </summary>
    public static IServiceCollection AddOutboxPublisher(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<RabbitMqConsumerHost>();
        services.AddHostedService<OutboxPublisherService>();
        return services;
    }

    public static IServiceCollection AddTenantMaintenance(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<TenantMaintenanceService>();
        return services;
    }

    /// <summary>[Consumes] atributlu consumer-i scoped qeydiyyata alır və consumer host-a tanıdır.</summary>
    public static IServiceCollection AddIntegrationConsumer<TConsumer>(this IServiceCollection services)
        where TConsumer : class, IIntegrationConsumer
    {
        ArgumentNullException.ThrowIfNull(services);
        var attribute = typeof(TConsumer).GetCustomAttribute<ConsumesAttribute>()
            ?? throw new InvalidOperationException($"{typeof(TConsumer).Name} must be annotated with [Consumes].");
        services.AddScoped<TConsumer>();
        services.AddSingleton(new ConsumerRegistrationHolder(new ConsumerRegistration(typeof(TConsumer), attribute.Name, attribute.RoutingKeys)));
        return services;
    }
}
