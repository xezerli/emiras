using DentaCore.TestSupport;

namespace DentaCore.ClinicCore.IntegrationTests;

[Collection(ClinicCoreDefinition.Name)]
public sealed class EndpointSecurityTests(PatientApiFixture f)
{
    [IntegrationFact]
    public void Every_route_requires_authorization_except_the_health_probe()
    {
        var services = f.Factory!.Services;

        Assert.True(EndpointSecurity.RouteCount(services) > 40, "route enumeration must actually see the API surface");
        Assert.Equal(["GET /health"], EndpointSecurity.AnonymousRoutes(services));
    }
}
