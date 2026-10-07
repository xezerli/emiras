using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Outbox;
using DentaCore.BuildingBlocks.Infrastructure.Security;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.Identity.Application;
using DentaCore.Identity.Infrastructure.Persistence;
using DentaCore.Identity.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DentaCore.Identity.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddApplicationCore(typeof(LoginCommand).Assembly);
        services.AddTenancy(configuration);

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Section));
        services.Configure<PiiOptions>(configuration.GetSection(PiiOptions.Section));
        services.AddSingleton<JwtKeyProvider>();
        services.AddSingleton<IPiiProtector, AesGcmPiiProtector>();
        services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        services.AddSingleton<IRefreshTokenFactory, RefreshTokenFactory>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();

        // Hər sorğu öz tenant-ının sxeminə qoşulur (search_path)
        services.AddDbContext<IdentityDbContext>((sp, options) =>
        {
            var tenant = sp.GetRequiredService<ITenantContext>();
            options
                .UseNpgsql(sp.GetRequiredService<ITenantDataSources>().Get(tenant.Schema))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(new DomainEventsToOutboxInterceptor());
        });
        services.AddScoped<IIdentityUnitOfWork>(sp => sp.GetRequiredService<IdentityDbContext>());
        services.AddScoped<IModuleUnitOfWork>(sp => sp.GetRequiredService<IdentityDbContext>());
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        return services;
    }

    /// <summary>JWT Bearer autentifikasiyası (RS256, açar Identity-dən).</summary>
    public static IServiceCollection AddIdentityAuthentication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearer>());
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddAuthorization();
        return services;
    }
}
