using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Observer.Core.Security;

namespace Observer.Service.Credentials;

/// <summary>
/// The certificate the service presents to the OTHER machines.
/// </summary>
/// <remarks>
/// Self-signed, one per machine, generated on first start and kept in the same perimeter as the
/// token. No authority vouches for it: what ties a connection to this machine is its
/// fingerprint, which is taken by hand with <c>observer share</c>.
/// <para>
/// <b>The validity is long on purpose, and it is not laziness.</b> With the fingerprint pinned by
/// the client, replacing the certificate means breaking EVERY client until someone rewrites the
/// fingerprint by hand on each one. A short expiry would add no security — trust here comes
/// neither from the expiry nor from a chain — and would turn an automatic renewal into a
/// simultaneous failure of every remote dashboard.
/// </para>
/// </remarks>
public static class MachineCertificate
{
    /// <summary>The certificate's file name, next to the token store.</summary>
    public const string FileName = "certificate.pfx";

    /// <summary>How long the certificate is valid. See the type's notes: it is long on purpose.</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromDays(3653);

    /// <summary>How far back the validity starts, to tolerate clocks that are not aligned.</summary>
    /// <remarks>
    /// A certificate that starts being valid "now" is refused by a machine whose clock is a few
    /// minutes behind, and the symptom — a TLS error at start-up that clears itself shortly
    /// after — does not name its own cause.
    /// </remarks>
    public static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromDays(1);

    /// <summary>Creates a new certificate for this machine.</summary>
    /// <param name="machineName">The name to put in the subject and among the subject alternative names.</param>
    /// <param name="now">The instant to count the validity from.</param>
    /// <returns>The certificate, with its private key.</returns>
    public static X509Certificate2 Create(string machineName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);

        using RSA key = RSA.Create(3072);

        CertificateRequest request = new(
            "CN=" + machineName,
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));

        // SERVER authentication and nothing else. A certificate with no declared use is a
        // certificate that is good for everything, and this one must be good for nothing else.
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")],
            critical: false));

        // The subject alternative names are not for us — the client compares the fingerprint, not the name —
        // but they are for anyone who points a browser or "openssl s_client" at this port to
        // understand what they are talking to.
        SubjectAlternativeNameBuilder subjectAlternativeNames = new();
        subjectAlternativeNames.AddDnsName(machineName);
        subjectAlternativeNames.AddDnsName("localhost");
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        return request.CreateSelfSigned(now - ClockSkewAllowance, now + Validity);
    }

    /// <summary>Packages the certificate with its key, to store it.</summary>
    /// <param name="certificate">The certificate to export.</param>
    /// <returns>The PKCS#12 in bytes.</returns>
    /// <remarks>
    /// With no password, and it is not an oversight: a password written next to the file it is
    /// supposed to protect protects nothing. The file already sits in a folder that excludes
    /// every other account, and the protection here is the perimeter — exactly as for the token.
    /// </remarks>
    public static byte[] Export(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return certificate.Export(X509ContentType.Pkcs12);
    }

    /// <summary>Reads back a stored certificate.</summary>
    /// <param name="pkcs12">The file's content.</param>
    /// <returns>The certificate with its private key.</returns>
    /// <remarks>
    /// <b>The flag changes per operating system, and it is not a preference: it is measured.</b>
    /// <para>
    /// The obvious choice would be <c>EphemeralKeySet</c> everywhere — the key stays in memory
    /// and never touches the system keychain. On Windows it <b>does not work</b>: the certificate
    /// loads perfectly well, but SChannel cannot serve it and the TLS handshake dies with
    /// <i>"Received an unexpected EOF or 0 bytes from the transport stream"</i> — an error that
    /// does not name its own cause and that no unit test would have seen, because until
    /// <c>TrasportoHttpsTests</c> no test touched a real transport.
    /// </para>
    /// <para>
    /// On Windows what is needed is therefore the keychain of the process's USER: as LocalSystem
    /// that is SYSTEM's profile, as protected as the store. Deliberately NOT
    /// <c>MachineKeySet</c>, which would end up in <c>ProgramData\Microsoft\Crypto\RSA\MachineKeys</c>,
    /// a folder with far wider permissions. And deliberately NOT <c>PersistKeySet</c>: without
    /// it, the key container deletes itself. Measured by counting the keychain files before and
    /// after — eight loads, the service process included, killed without a clean shutdown:
    /// fifteen files before, fifteen after.
    /// </para>
    /// </remarks>
    public static X509Certificate2 Load(byte[] pkcs12)
    {
        ArgumentNullException.ThrowIfNull(pkcs12);

        X509KeyStorageFlags flag = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;

        return X509CertificateLoader.LoadPkcs12(pkcs12, null, flag);
    }

    /// <summary>Reads back a certificate to LOOK at it, without importing its key.</summary>
    /// <param name="pkcs12">The file's content.</param>
    /// <returns>The certificate, usable only for reading its data.</returns>
    /// <remarks>
    /// It is for the command line, which wants only the certificate's fingerprint. With
    /// <see cref="Load"/> the private key would end up in the keychain of the user who ran the
    /// command — any administrator at all — whereas <c>EphemeralKeySet</c> keeps it in memory
    /// and throws it away. It does not hold up a TLS handshake, and here it must not.
    /// </remarks>
    public static X509Certificate2 LoadForInspection(byte[] pkcs12)
    {
        ArgumentNullException.ThrowIfNull(pkcs12);

        return X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.EphemeralKeySet);
    }

    /// <summary>The fingerprint by which clients recognise it.</summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>The fingerprint in canonical form.</returns>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return CertificateFingerprint.From(certificate.RawDataMemory.Span);
    }

    /// <summary>The certificate's path, next to the token store.</summary>
    /// <param name="storePath">The path of <c>credentials.json</c>.</param>
    /// <returns>The path of the certificate file.</returns>
    public static string PathNextTo(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        return Path.Combine(Path.GetDirectoryName(storePath) ?? ".", FileName);
    }
}