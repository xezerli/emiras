using System.Security.Cryptography;
using System.Text;
using DentaCore.BuildingBlocks.Application;
using Microsoft.Extensions.Options;

namespace DentaCore.BuildingBlocks.Infrastructure.Security;

public sealed class PiiOptions
{
    public const string Section = "Security";

    /// <summary>Base64, 32 bayt (AES-256). İstehsalda KMS/Vault-dan, tenant üzrə data-key ilə (Mərhələ 1 §8).</summary>
    public string PiiEncryptionKey { get; set; } = string.Empty;

    /// <summary>Base64, ≥ 32 bayt. Şifrələmə açarından FƏRQLİ olmalıdır.</summary>
    public string PiiHashKey { get; set; } = string.Empty;
}

/// <summary>Format: [versiya=1][nonce 12][tag 16][ciphertext]. Versiya baytı açar rotasiyası üçün yer saxlayır.</summary>
public sealed class AesGcmPiiProtector : IPiiProtector
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _encryptionKey;
    private readonly byte[] _hashKey;

    public AesGcmPiiProtector(IOptions<PiiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _encryptionKey = Decode(options.Value.PiiEncryptionKey, 32, nameof(PiiOptions.PiiEncryptionKey));
        _hashKey = Decode(options.Value.PiiHashKey, 32, nameof(PiiOptions.PiiHashKey));
        if (CryptographicOperations.FixedTimeEquals(_encryptionKey, _hashKey))
        {
            throw new InvalidOperationException("PiiEncryptionKey and PiiHashKey must differ.");
        }
    }

    public byte[] Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[1 + NonceSize + TagSize + data.Length];
        output[0] = Version;
        var nonce = output.AsSpan(1, NonceSize);
        var tag = output.AsSpan(1 + NonceSize, TagSize);
        var cipher = output.AsSpan(1 + NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_encryptionKey, TagSize);
        aes.Encrypt(nonce, data, cipher, tag);
        return output;
    }

    public string Decrypt(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (ciphertext.Length < 1 + NonceSize + TagSize || ciphertext[0] != Version)
        {
            throw new CryptographicException("Unsupported or corrupted ciphertext.");
        }

        var nonce = ciphertext.AsSpan(1, NonceSize);
        var tag = ciphertext.AsSpan(1 + NonceSize, TagSize);
        var cipher = ciphertext.AsSpan(1 + NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_encryptionKey, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    public byte[] BlindIndex(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return HMACSHA256.HashData(_hashKey, Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant()));
    }

    private static byte[] Decode(string base64, int minLength, string name)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length < minLength || (name == nameof(PiiOptions.PiiEncryptionKey) && bytes.Length != 32))
            {
                throw new InvalidOperationException($"{name} must decode to {(name == nameof(PiiOptions.PiiEncryptionKey) ? "exactly" : "at least")} {minLength} bytes.");
            }

            return bytes;
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{name} is missing or not valid base64.", ex);
        }
    }
}
