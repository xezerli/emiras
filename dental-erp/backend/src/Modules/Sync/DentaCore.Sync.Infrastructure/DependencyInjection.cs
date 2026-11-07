using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Auth;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.Sync.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DentaCore.Sync.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Offline obyektləri yazan modullar <c>ISyncEntityHandler</c>-i özləri qeydiyyatdan keçirir (məs. <c>AddClinicalModule</c>).
    /// Sync modulunun öz verilənləri üçün EF yoxdur: hər şey SQL ilə, ona görə UnitOfWork boşdur.
    /// </summary>
    public static IServiceCollection AddSyncModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddApplicationCore(typeof(PushChangesCommand).Assembly);
        services.AddTenancy(configuration);
        services.AddRequestContext();
        services.AddScoped<ISyncReader, SqlSyncReader>();
        services.AddScoped<ISyncClientChangeStore, SqlClientChangeStore>();
        services.AddScoped<IModuleUnitOfWork, NoOpSyncUnitOfWork>();
        return services;
    }
}

internal sealed class NoOpSyncUnitOfWork : IModuleUnitOfWork
{
    public System.Reflection.Assembly ApplicationAssembly => typeof(PushChangesCommand).Assembly;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

    public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The sync module has no transactional state of its own.");
}
