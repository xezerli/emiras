using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DentaCore.TestSupport;

/// <summary>
/// Marşrut səviyyəsində "default-deny" yoxlaması: hər endpoint ya avtorizasiya tələb edir, ya da açıq şəkildə icazə siyahısındadır.
/// Yeni endpoint yazılıb <c>RequireAuthorization()</c> unudulsa test düşür (sızma səhvi ən çox bu cür yaranır).
/// </summary>
public static class EndpointSecurity
{
    /// <summary>Mövcud, avtorizasiyasız marşrutların (metod + şablon) siyahısı. Siyahıda olmayan açıq marşrut səhvdir.</summary>
    public static IReadOnlyList<string> AnonymousRoutes(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetServices<EndpointDataSource>()
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null || e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .SelectMany(e =>
            {
                var methods = e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"];
                return methods.Select(m => $"{m} {e.RoutePattern.RawText}");
            })
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public static int RouteCount(IServiceProvider services) =>
        services.GetServices<EndpointDataSource>().SelectMany(ds => ds.Endpoints).OfType<RouteEndpoint>().Count();
}
