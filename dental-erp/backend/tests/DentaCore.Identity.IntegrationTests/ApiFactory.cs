using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DentaCore.Identity.IntegrationTests;

/// <summary>
/// Identity.Api-ni real host kimi qaldırır. Konfiqurasiya mühit dəyişənləri ilə verilir: minimal hosting-də
/// builder.Configuration Program.cs-də dərhal oxunur, ona görə WebApplicationFactory-nin gec konfiqurasiyası kifayət etmir.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _previous = [];

    public ApiFactory(PostgresFixture db)
    {
        ArgumentNullException.ThrowIfNull(db);
        using var rsa = RSA.Create(2048);
        Set("ConnectionStrings__Default", db.ConnectionString);
        Set("Tenancy__AllowTenantHeader", "true");
        Set("Security__PiiEncryptionKey", db.EncryptionKey);
        Set("Security__PiiHashKey", db.HashKey);
        Set("Jwt__SigningKeyPem", rsa.ExportPkcs8PrivateKeyPem());
        Set("RateLimiting__AuthPerMinute", "10000");
    }

    public HttpClient CreateClientFor(string tenantSlug)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (var (key, value) in _previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private void Set(string key, string? value)
    {
        _previous[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }
}
