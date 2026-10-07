using System.Security.Cryptography;
using System.Text;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.Identity.Application;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DentaCore.Identity.Infrastructure.Security;

/// <summary>
/// Argon2id (OWASP tövsiyəsi: m=19 MiB, t=2, p=1). Format: $argon2id$v=19$m=..,t=..,p=..$salt$hash.
/// Parametrlər heş-in içindədir, sonradan gücləndirmək mümkündür (köhnə heşlər işləməyə davam edir).
/// </summary>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    private const int MemoryKiB = 19456;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private static readonly string DummyHash = new Argon2idPasswordHasher().Hash("dummy-password-for-timing");

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Compute(password, salt, MemoryKiB, Iterations, Parallelism, HashSize);
        return $"$argon2id$v=19$m={MemoryKiB},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string hash)
    {
        var parts = hash.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[0] != "argon2id")
        {
            return false;
        }

        try
        {
            var p = parts[2].Split(',').Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => int.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture));
            var salt = Convert.FromBase64String(parts[3]);
            var expected = Convert.FromBase64String(parts[4]);
            var actual = Compute(password, salt, p["m"], p["t"], p["p"], expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    public void BurnTime(string password) => Verify(password, DummyHash);

    private static byte[] Compute(string password, byte[] salt, int memoryKiB, int iterations, int parallelism, int size)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKiB,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(size);
    }
}

public sealed class RefreshTokenFactory : IRefreshTokenFactory
{
    public (string Raw, byte[] Hash) Create()
    {
        var raw = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return (raw, Hash(raw));
    }

    public byte[] Hash(string raw) => SHA256.HashData(Encoding.UTF8.GetBytes(raw));
}

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "https://auth.dentacore.app";

    public string Audience { get; set; } = "dentacore-api";

    public int AccessTokenMinutes { get; set; } = 10;

    /// <summary>RSA-2048 PEM (PKCS#8). İstehsalda Vault/KMS-dən. Boşdursa yalnız Development-də müvəqqəti açar yaradılır.</summary>
    public string? SigningKeyPem { get; set; }
}

/// <summary>RS256 açarı: Identity imzalayır, digər servislər yalnız public açarla (JWKS) yoxlayır.</summary>
public sealed class JwtKeyProvider : IDisposable
{
    private readonly RSA _rsa = RSA.Create();

    public JwtKeyProvider(IOptions<JwtOptions> options, Microsoft.Extensions.Hosting.IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        var pem = options.Value.SigningKeyPem;
        if (!string.IsNullOrWhiteSpace(pem))
        {
            _rsa.ImportFromPem(pem);
        }
        else if (Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions.IsDevelopment(environment))
        {
            _rsa.KeySize = 2048;   // müvəqqəti açar: proses yenidən başlayanda tokenlər keçərsiz olur
        }
        else
        {
            throw new InvalidOperationException("Jwt:SigningKeyPem must be configured outside Development.");
        }

        if (_rsa.KeySize < 2048)
        {
            throw new InvalidOperationException("Jwt signing key must be at least RSA-2048.");
        }

        Key = new RsaSecurityKey(_rsa) { KeyId = "dc-1" };
    }

    public RsaSecurityKey Key { get; }

    public void Dispose() => _rsa.Dispose();
}

public sealed class JwtAccessTokenIssuer(IOptions<JwtOptions> options, JwtKeyProvider keys, DentaCore.BuildingBlocks.Application.IClock clock) : IAccessTokenIssuer
{
    public IssuedAccessToken Issue(UserAccess access, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(access);
        var o = options.Value;
        var now = clock.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = access.UserId.ToString(),
            ["tid"] = tenantId.ToString(),
            ["name"] = access.FullName,
            ["role"] = access.Roles.ToArray(),
            // "code@scope": token şişməsin deyə yalnız ad və scope. Məbləğ limitləri (max_amount) DB-dən yoxlanılır.
            ["perm"] = access.Permissions.Select(p => $"{p.Code}@{p.Scope}").ToArray(),
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.UtcDateTime.AddMinutes(o.AccessTokenMinutes),
            Claims = claims,
            SigningCredentials = new SigningCredentials(keys.Key, SecurityAlgorithms.RsaSha256),
        };

        return new IssuedAccessToken(new JsonWebTokenHandler().CreateToken(descriptor), o.AccessTokenMinutes * 60);
    }
}

/// <summary>JwtBearer yoxlama parametrləri Identity açarından götürülür (eyni host-da).</summary>
internal sealed class ConfigureJwtBearer(IOptions<JwtOptions> options, JwtKeyProvider keys) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(string? name, JwtBearerOptions o)
    {
        o.MapInboundClaims = false;   // "sub", "role" adları dəyişdirilmir
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = options.Value.Issuer,
            ValidAudience = options.Value.Audience,
            IssuerSigningKey = keys.Key,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],   // alqoritm sabitdir ("alg: none" və HS256 qarışıqlığı yoxdur)
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = "role",
        };
    }

    public void Configure(JwtBearerOptions o) => Configure(Microsoft.Extensions.Options.Options.DefaultName, o);
}
