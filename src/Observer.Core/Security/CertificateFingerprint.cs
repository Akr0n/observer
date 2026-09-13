using System.Globalization;
using System.Security.Cryptography;

namespace Observer.Core.Security;

/// <summary>
/// A certificate's fingerprint: how it is computed, how it is written, how it is compared.
/// </summary>
/// <remarks>
/// It lives in Observer.Core and not on one side alone because BOTH sides need it: the service
/// prints it, the client compares it with the one it brought along. Client and service cannot
/// reference each other, so one copy per side would be two copies of the rule that decides who
/// to trust - and the day they diverge, the symptom is a refusal nobody manages to explain.
/// <para>
/// Observer's certificate is SELF-SIGNED: no authority vouches for it, and the chain says
/// nothing. The only thing that ties a connection to one precise machine is this fingerprint,
/// taken by hand from the machine itself with <c>observer share</c>.
/// </para>
/// </remarks>
public static class CertificateFingerprint
{
    /// <summary>The prefix that declares the algorithm. Always present on output.</summary>
    public const string Prefisso = "sha256:";

    /// <summary>How many hexadecimal characters an SHA-256 has.</summary>
    public const int CifreEsadecimali = 64;

    /// <summary>Computes the fingerprint of a certificate's DER encoding.</summary>
    /// <param name="derCertificate">The certificate encoded in DER.</param>
    /// <returns>The fingerprint in canonical form, with the prefix.</returns>
    public static string From(ReadOnlySpan<byte> derCertificate) =>
        Prefisso + Convert.ToHexString(SHA256.HashData(derCertificate));

    /// <summary>
    /// Reduces to canonical form what a human copied by hand.
    /// </summary>
    /// <param name="text">The fingerprint written in a configuration file.</param>
    /// <returns>The 64 digits in upper case, or null if it is not an SHA-256 fingerprint.</returns>
    /// <remarks>
    /// Tolerant on input and strict on output, and that is not indulgence: this value is copied
    /// by a person from a terminal into a text file, and the tools that print it do not agree on
    /// how to separate it. Colons, spaces and dashes are accepted; nothing else is, because an
    /// "almost right" fingerprint must be refused and not fixed up.
    /// </remarks>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        ReadOnlySpan<char> rest = text.AsSpan().Trim();

        if (rest.StartsWith(Prefisso, StringComparison.OrdinalIgnoreCase))
        {
            rest = rest[Prefisso.Length..];
        }

        Span<char> digits = stackalloc char[CifreEsadecimali];
        int count = 0;

        foreach (char character in rest)
        {
            if (character is ':' or ' ' or '-')
            {
                continue;
            }

            if (count == CifreEsadecimali || !Uri.IsHexDigit(character))
            {
                return null;
            }

            digits[count++] = char.ToUpperInvariant(character);
        }

        return count == CifreEsadecimali ? new string(digits) : null;
    }

    /// <summary>Says whether two fingerprints designate the same certificate.</summary>
    /// <param name="expected">The fingerprint pinned in the configuration.</param>
    /// <param name="presented">The fingerprint of the certificate that arrived from the network.</param>
    /// <returns>True only if both are valid and equal.</returns>
    /// <remarks>
    /// An unreadable fingerprint is never equal to anything. Treating it as "skip the check"
    /// would turn a typo into the silent disabling of the only thing that protects the
    /// connection.
    /// </remarks>
    public static bool Match(string? expected, string? presented) =>
        Normalize(expected) is { } a && Normalize(presented) is { } b
        && string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>Writes the fingerprint in groups of two digits, for whoever has to compare it by eye.</summary>
    /// <param name="fingerprint">The fingerprint, in any accepted form.</param>
    /// <returns>The readable form, or the original text if it is not a valid fingerprint.</returns>
    public static string ForHumans(string fingerprint)
    {
        if (Normalize(fingerprint) is not { } digits)
        {
            return fingerprint;
        }

        string[] pairs = new string[CifreEsadecimali / 2];

        for (int i = 0; i < pairs.Length; i++)
        {
            pairs[i] = digits.Substring(i * 2, 2);
        }

        return string.Join(':', pairs).ToUpperInvariant();
    }

    /// <summary>The digit count, for error messages.</summary>
    /// <returns>The count as text.</returns>
    public static string DigitCount() =>
        CifreEsadecimali.ToString(CultureInfo.InvariantCulture);
}