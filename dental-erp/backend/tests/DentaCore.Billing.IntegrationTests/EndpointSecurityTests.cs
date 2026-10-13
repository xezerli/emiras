using DentaCore.TestSupport;

namespace DentaCore.Billing.IntegrationTests;

[Collection(BillingApiDefinition.Name)]
public sealed class EndpointSecurityTests(BillingApiFixture f)
{
    [IntegrationFact]
    public void Every_route_requires_authorization_except_the_health_probe()
    {
        var services = f.Factory!.Services;

        Assert.True(EndpointSecurity.RouteCount(services) > 10, "route enumeration must actually see the API surface");
        Assert.Equal(["GET /health"], EndpointSecurity.AnonymousRoutes(services));
    }
}
