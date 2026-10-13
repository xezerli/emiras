using System.Net;
using System.Text.Json;
using DentaCore.BuildingBlocks.Infrastructure.Observability;
using DentaCore.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DentaCore.ClinicCore.IntegrationTests;

[Collection(ClinicCoreDefinition.Name)]
public sealed class HealthTests(PatientApiFixture f)
{
    private static async Task<HttpClient> StartAsync(string connectionString, List<IAsyncDisposable> cleanup)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        var app = builder.Build();
        app.MapDentaCoreHealth();
        await app.StartAsync();
        cleanup.Add(app);
        return app.GetTestClient();
    }

    [IntegrationFact]
    public async Task Liveness_never_touches_the_database_but_readiness_does()
    {
        var cleanup = new List<IAsyncDisposable>();
        try
        {
            // Əlçatmaz baza: canlılıq 200 (pod yenidən başladılmamalıdır), hazırlıq 503 (trafik göndərilməməlidir), cavabda təfərrüat yoxdur
            using var broken = await StartAsync("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=1;Command Timeout=1", cleanup);
            Assert.Equal(HttpStatusCode.OK, (await broken.GetAsync("/health")).StatusCode);
            var down = await broken.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
            var body = await down.Content.ReadAsStringAsync();
            Assert.Equal("unavailable", JsonDocument.Parse(body).RootElement.GetProperty("status").GetString());
            Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);   // bağlantı detalları sızmır

            using var healthy = await StartAsync(f.Db.ConnectionString!, cleanup);
            Assert.Equal(HttpStatusCode.OK, (await healthy.GetAsync("/health/ready")).StatusCode);
        }
        finally
        {
            foreach (var c in cleanup)
            {
                await c.DisposeAsync();
            }
        }
    }

    [IntegrationFact]
    public async Task The_real_host_exposes_both_probes_anonymously()
    {
        using var client = f.Factory!.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }
}
