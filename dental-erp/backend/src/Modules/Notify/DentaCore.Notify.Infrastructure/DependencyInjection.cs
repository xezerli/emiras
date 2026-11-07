using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DentaCore.Notify.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNotifyModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSignalR();
        services.AddMessaging(configuration);   // RabbitMQ seçimləri + tenant kataloqu (consumer host tenant-ı slug ilə tapır)
        services.AddIntegrationConsumer<RealtimeConsumer>();
        services.AddIntegrationConsumerHost();
        return services;
    }
}
