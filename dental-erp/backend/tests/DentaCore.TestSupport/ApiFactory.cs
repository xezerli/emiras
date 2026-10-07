using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DentaCore.TestSupport;

/// <summary>
/// Host-u real kimi qaldırır. Konfiqurasiya mühit dəyişənləri ilə verilir: minimal hosting-də builder.Configuration
/// Program.cs-də dərhal oxunur, ona görə WebApplicationFactory-nin gec konfiqurasiyası kifayət etmir.
/// </summary>
public sealed class ApiFactory<TProgram> : WebApplicationFactory<TProgram>
    where TProgram : class
{
    private readonly Dictionary<string, string?> _previous = [];

    public ApiFactory(PostgresFixture db, params (string Key, string Value)[] extra)
    {
        ArgumentNullException.ThrowIfNull(db);
        Set("ConnectionStrings__Default", db.ConnectionString);
        Set("Tenancy__AllowTenantHeader", "true");
        Set("Security__PiiEncryptionKey", db.EncryptionKey);
        Set("Security__PiiHashKey", db.HashKey);
        Set("RateLimiting__AuthPerMinute", "10000");
        foreach (var (key, value) in extra)
        {
            Set(key, value);
        }
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

/// <summary>Test RSA açarı: Identity-ni işə salmadan, başqa host-lar üçün imzalı JWT yaradır.</summary>
public sealed class TestKeys : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);

    public string PublicPem => _rsa.ExportSubjectPublicKeyInfoPem();

    public string PrivatePem => _rsa.ExportPkcs8PrivateKeyPem();

    public string Mint(Guid userId, Guid tenantId, IEnumerable<string> permissions, IEnumerable<string>? branches = null, string name = "Test User")
    {
        var descriptor = new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = "https://auth.dentacore.app",
            Audience = "dentacore-api",
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId.ToString(),
                ["tid"] = tenantId.ToString(),
                ["name"] = name,
                ["perm"] = permissions.ToArray(),
                ["branch"] = (branches ?? ["*"]).ToArray(),
            },
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                new Microsoft.IdentityModel.Tokens.RsaSecurityKey(_rsa) { KeyId = "dc-1" },
                Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256),
        };
        return new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(descriptor);
    }

    public void Dispose() => _rsa.Dispose();
}
