using System.Security.Cryptography;
using Observer.Core.Security;

namespace Observer.Core.Tests;

/// <summary>
/// The fingerprint is the only thing that ties a connection to one specific machine.
/// </summary>
/// <remarks>
/// Observer's certificate is self-signed: no authority vouches for it, the chain says nothing,
/// and ordinary TLS validation would reject everything. This comparison stands in its place.
/// If it errs on the permissive side, the encrypted connection protects against someone
/// listening but not against someone in the middle - which is the worst case, because it looks
/// safe.
/// </remarks>
public class CertificateFingerprintTests
{
    private static readonly byte[] Certificate = [1, 2, 3, 4, 5];

    private static string ExpectedFingerprint() => CertificateFingerprint.From(Certificate);

    [Fact]
    public void TheFingerprintIsTheSha256HexOfTheCertificate()
    {
        // Not an invented format: it has to be comparable with what openssl prints.
        string expected = "sha256:" + Convert.ToHexString(SHA256.HashData(Certificate));

        Assert.Equal(expected, CertificateFingerprint.From(Certificate));
    }

    [Fact]
    public void DifferentCertificatesGiveDifferentFingerprints()
    {
        Assert.NotEqual(
            CertificateFingerprint.From([1, 2, 3]),
            CertificateFingerprint.From([1, 2, 4]));
    }

    [Theory]
    [InlineData("sha256:")]
    [InlineData("SHA256:")]
    [InlineData("")]
    public void ThePrefixIsOptionalAndCaseInsensitive(string prefix)
    {
        string digits = Convert.ToHexString(SHA256.HashData(Certificate));

        Assert.True(CertificateFingerprint.Match(prefix + digits, ExpectedFingerprint()));
    }

    [Fact]
    public void SeparatorsAndCaseDoNotChangeTheVerdict()
    {
        // This value is copied by a PERSON from a terminal into a text file, and the tools
        // that print fingerprints do not agree on how to separate them.
        string readable = CertificateFingerprint.ForHumans(ExpectedFingerprint());

        Assert.Contains(":", readable, StringComparison.Ordinal);
        Assert.True(CertificateFingerprint.Match(readable, ExpectedFingerprint()));
        Assert.True(CertificateFingerprint.Match(readable.ToLowerInvariant(), ExpectedFingerprint()));
        Assert.True(CertificateFingerprint.Match(readable.Replace(':', ' '), ExpectedFingerprint()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sha256:")]
    [InlineData("not-a-fingerprint")]
    public void WhatIsNotAFingerprintIsNotNormalized(string? text)
    {
        Assert.Null(CertificateFingerprint.Normalize(text));
    }

    [Fact]
    public void AFingerprintTooShortOrTooLongIsRejected()
    {
        string digits = Convert.ToHexString(SHA256.HashData(Certificate));

        Assert.Null(CertificateFingerprint.Normalize(digits[..62]));
        Assert.Null(CertificateFingerprint.Normalize(digits + "AB"));
    }

    [Fact]
    public void OneNonHexCharacterRejectsTheWholeFingerprint()
    {
        // "Almost right" has to be rejected, not repaired: skipping unexpected characters
        // would accept a fingerprint with a typo inside it.
        string digits = Convert.ToHexString(SHA256.HashData(Certificate));

        Assert.Null(CertificateFingerprint.Normalize("Z" + digits[1..]));
        Assert.Null(CertificateFingerprint.Normalize(digits[..63] + "Z"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "sha256:AA")]
    [InlineData("sha256:AA", null)]
    [InlineData("", "")]
    [InlineData("look-this-is-not-a-fingerprint", "look-this-is-not-a-fingerprint")]
    public void TwoInvalidValuesAreNeverEqual(string? a, string? b)
    {
        // The most dangerous case of all: if two unreadable fingerprints came out equal,
        // a typo in BOTH places would switch the check off without saying so.
        Assert.False(CertificateFingerprint.Match(a, b));
    }

    [Fact]
    public void AFingerprintOffByOneCharacterDoesNotMatch()
    {
        string digits = Convert.ToHexString(SHA256.HashData(Certificate));
        char first = digits[0] == 'A' ? 'B' : 'A';

        Assert.False(CertificateFingerprint.Match(first + digits[1..], ExpectedFingerprint()));
    }

    [Fact]
    public void TheReadableFormDoesNotMangleWhatItCannotRead()
    {
        Assert.Equal("I do not know", CertificateFingerprint.ForHumans("I do not know"));
    }
}