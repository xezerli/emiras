using System.Security.Cryptography;
using DentaCore.BuildingBlocks.Infrastructure.Security;
using DentaCore.Identity.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace DentaCore.Identity.UnitTests;

public class PiiProtectorTests
{
    [Fact]
    public void Roundtrip_and_nondeterministic_ciphertext()
    {
        var pii = TestPii.Create();

        var a = pii.Encrypt("+994501234567");
        var b = pii.Encrypt("+994501234567");

        Assert.Equal("+994501234567", pii.Decrypt(a));
        Assert.NotEqual(a, b);   // hər dəfə yeni nonce
    }

    [Fact]
    public void Tampered_ciphertext_is_rejected()
    {
        var pii = TestPii.Create();
        var data = pii.Encrypt("secret");
        data[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => pii.Decrypt(data));
    }

    [Fact]
    public void Blind_index_is_normalized_and_deterministic()
    {
        var pii = TestPii.Create();

        Assert.Equal(pii.BlindIndex("A@B.az"), pii.BlindIndex("  a@b.AZ "));
        Assert.NotEqual(pii.BlindIndex("a@b.az"), pii.BlindIndex("c@b.az"));
    }

    [Fact]
    public void Misconfigured_keys_fail_fast()
    {
        var same = Convert.ToBase64String(new byte[32]);

        Assert.Throws<InvalidOperationException>(() => new AesGcmPiiProtector(Options.Create(new PiiOptions { PiiEncryptionKey = same, PiiHashKey = same })));
        Assert.Throws<InvalidOperationException>(() => new AesGcmPiiProtector(Options.Create(new PiiOptions())));
        Assert.Throws<InvalidOperationException>(() => new AesGcmPiiProtector(Options.Create(new PiiOptions { PiiEncryptionKey = Convert.ToBase64String(new byte[16]), PiiHashKey = same })));
    }
}

public class PasswordHasherTests
{
    private readonly Argon2idPasswordHasher _hasher = new();

    [Fact]
    public void Hash_verifies_and_uses_unique_salt()
    {
        var h1 = _hasher.Hash("pa$$w0rd");
        var h2 = _hasher.Hash("pa$$w0rd");

        Assert.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", h1, StringComparison.Ordinal);
        Assert.NotEqual(h1, h2);
        Assert.True(_hasher.Verify("pa$$w0rd", h1));
        Assert.False(_hasher.Verify("wrong", h1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("$argon2id$v=19$m=x,t=2,p=1$AAAA$AAAA")]
    [InlineData("$bcrypt$v=19$m=1,t=2,p=1$AAAA$AAAA")]
    public void Malformed_hash_is_rejected_without_throwing(string hash) => Assert.False(_hasher.Verify("x", hash));
}

public class RefreshTokenFactoryTests
{
    [Fact]
    public void Tokens_are_unique_and_hash_matches()
    {
        var factory = new RefreshTokenFactory();

        var (raw1, hash1) = factory.Create();
        var (raw2, _) = factory.Create();

        Assert.NotEqual(raw1, raw2);
        Assert.Equal(hash1, factory.Hash(raw1));
        Assert.True(raw1.Length >= 43);   // 32 bayt base64url
    }
}
