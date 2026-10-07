namespace DentaCore.BuildingBlocks.Application;

/// <summary>
/// PHI sahələrinin qorunması (Mərhələ 2 §1): AES-256-GCM şifrələmə + dəqiq axtarış üçün HMAC blind index.
/// </summary>
public interface IPiiProtector
{
    byte[] Encrypt(string plaintext);

    string Decrypt(byte[] ciphertext);

    /// <summary>Deterministik HMAC-SHA256. Dəyər normallaşdırılır (trim + kiçik hərf), açıq mətn heç yerdə saxlanmır.</summary>
    byte[] BlindIndex(string value);
}
