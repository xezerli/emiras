using System.Text.RegularExpressions;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Patient.Domain;

namespace DentaCore.Patient.Application;

/// <summary>Telefon/email/FİN normallaşdırması. Eyni şəxsin müxtəlif yazılışları eyni blind-index heş-ini verməlidir (dublikat aşkarı).</summary>
public static partial class ContactNormalizer
{
    [GeneratedRegex(@"^\+[0-9]{7,15}$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex E164();

    [GeneratedRegex(@"[\s\-\.\(\)]", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex Separators();

    /// <summary>
    /// "+994 50 123-45-67", "00994501234567", "0501234567" → "+994501234567".
    /// Yerli "0" ilə başlayan nömrə üçün defaultCountryCode (məs. "994") tələb olunur.
    /// </summary>
    public static Result<string> Phone(string raw, string defaultCountryCode)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var s = Separators().Replace(raw.Trim(), string.Empty);
        if (s.StartsWith("00", StringComparison.Ordinal))
        {
            s = "+" + s[2..];
        }
        else if (s.StartsWith('0') && defaultCountryCode.Length > 0)
        {
            s = "+" + defaultCountryCode + s[1..];
        }
        else if (!s.StartsWith('+') && s.All(char.IsAsciiDigit) && defaultCountryCode.Length > 0 && s.Length <= 9)
        {
            s = "+" + defaultCountryCode + s;   // 9 rəqəmdən qısa: milli nömrə (başında 0 olmadan)
        }

        return E164().IsMatch(s) ? s : Error.Validation("patient.invalid_phone", "Phone number is not valid.");
    }

    public static string Email(string raw) => raw.Trim().ToLowerInvariant();

    public static string NationalId(string raw) => raw.Trim().ToUpperInvariant();

    /// <summary>Axtarış sorğusunun telefon kimi tanınması (yalnız rəqəm/separator, 7+ rəqəm).</summary>
    public static string? TryPhone(string query, string defaultCountryCode)
    {
        var digits = query.Count(char.IsAsciiDigit);
        if (digits < 7 || query.Any(c => char.IsLetter(c)))
        {
            return null;
        }

        var result = Phone(query, defaultCountryCode);
        return result.IsSuccess ? result.Value : null;
    }
}

/// <summary>UI-da göstərmə üçün maskalama. Tam dəyər yalnız açıq "reveal" ilə (audit olunur) qaytarılır.</summary>
public static class Masking
{
    public static string Phone(string e164) =>
        e164.Length <= 6 ? new string('*', e164.Length) : $"{e164[..4]}{new string('*', e164.Length - 6)}{e164[^2..]}";

    public static string Email(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 1 ? "***" + (at < 0 ? string.Empty : email[at..]) : $"{email[0]}***{email[at..]}";
    }

    public static string NationalId(string id) => id.Length <= 2 ? "***" : $"***{id[^2..]}";
}

public sealed class PatientOptions
{
    public const string Section = "Patient";

    /// <summary>Yerli (0 ilə başlayan) nömrələri E.164-ə çevirmək üçün ölkə kodu. Azərbaycan: 994.</summary>
    public string DefaultCountryCode { get; set; } = "994";
}

internal static class Protect
{
    public static ProtectedValue Value(IPiiProtector pii, string normalized) => new(pii.Encrypt(normalized), pii.BlindIndex(normalized));
}
