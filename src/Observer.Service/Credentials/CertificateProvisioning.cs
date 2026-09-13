using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Observer.Service.Credentials;

/// <summary>Where the certificate in use comes from.</summary>
public enum CertificateOrigin
{
    /// <summary>Generated in memory and never stored: the fingerprint changes at every start.</summary>
    Ephemeral = 0,

    /// <summary>Read back from the store.</summary>
    Stored,

    /// <summary>Generated now and stored.</summary>
    CreatedAndStored,
}

/// <summary>The certificate in use, with where it came from.</summary>
/// <param name="Certificate">The certificate, with its private key.</param>
/// <param name="Fingerprint">The fingerprint to hand to the clients.</param>
/// <param name="Origin">Where it comes from.</param>
/// <param name="Path">The file used, or null if it was not stored.</param>
public sealed record ProvisionedCertificate(
    X509Certificate2 Certificate,
    string Fingerprint,
    CertificateOrigin Origin,
    string? Path);

/// <summary>
/// Provides the service with the certificate it presents to the other machines.
/// </summary>
/// <remarks>
/// Same shape as <see cref="CredentialProvisioning"/>, and for the same reason: it is the installer
/// that must know nothing. A certificate created by the installer would be a certificate the
/// installer has seen, with the private key handed around somewhere.
/// <para>
/// The perimeter is the same too, and not out of convenience: the private key is worth as much
/// as the token — whoever holds it can impersonate this machine in front of every dashboard that
/// pinned its fingerprint.
/// </para>
/// </remarks>
public static class CertificateProvisioning
{
    /// <summary>Provides the certificate.</summary>
    /// <param name="storePath">The path of <c>credentials.json</c>.</param>
    /// <param name="machineName">The name to put in the certificate.</param>
    /// <param name="now">The instant from which validity is counted.</param>
    /// <param name="runningAsService">Whether the process is registered as a system service.</param>
    /// <returns>The certificate and where it came from.</returns>
    /// <exception cref="InvalidOperationException">
    /// When it runs as a service and the certificate cannot be stored safely.
    /// </exception>
    public static ProvisionedCertificate Provision(
        string storePath,
        string machineName,
        DateTimeOffset now,
        bool runningAsService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        string path = MachineCertificate.PathNextTo(storePath);

        try
        {
            CredentialDirectory.Prepare(storePath);

            if (ReadStored(path) is { } stored)
            {
                return new ProvisionedCertificate(
                    stored,
                    MachineCertificate.Fingerprint(stored),
                    CertificateOrigin.Stored,
                    path);
            }

            using X509Certificate2 created = MachineCertificate.Create(machineName, now);

            byte[] pkcs12 = MachineCertificate.Export(created);

            Store(path, pkcs12);

            // What goes to Kestrel is the certificate READ BACK, never the one just created, and
            // the difference is measured: the object that comes out of CreateSelfSigned carries
            // the private key in memory only, and on Windows SChannel cannot serve it - the
            // handshake dies with "Received an unexpected EOF or 0 bytes from the transport stream".
            //
            // The first start would have been the only broken one, and that is the worst symptom
            // a fault can have: on the client side that error arrives as IOException and not as
            // AuthenticationException, so the dashboard would have said "check that the machine is
            // switched on"; and from the second start on it goes through ReadStored, so at the
            // first service restart it would all have vanished. A fault that looks like a network
            // problem and repairs itself.
            return new ProvisionedCertificate(
                LoadForServing(pkcs12),
                MachineCertificate.Fingerprint(created),
                CertificateOrigin.CreatedAndStored,
                path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (runningAsService)
            {
                throw new InvalidOperationException(RefusalMessage(path), error);
            }

            return CreateEphemeral(machineName, now);
        }
        catch (InvalidOperationException error)
            when (!runningAsService && error.InnerException is not CryptographicException)
        {
            // The ephemeral fallback is for a store that cannot be MADE SAFE, not for a
            // certificate that is there and unreadable. That case must be told to whoever launches
            // the service by hand too: falling back silently would show them a service that
            // starts, a new fingerprint at every start, and no clue about the broken file sitting
            // on their disk.
            return CreateEphemeral(machineName, now);
        }
    }

    /// <summary>A certificate good for this run and nothing more.</summary>
    /// <remarks>
    /// As for the token: never a fallback on disk outside the perimeter. Here the price is
    /// visible — the fingerprint changes at every start, so the remote dashboards will not
    /// connect — and it is right that it shows, instead of letting one believe everything is
    /// fine while the private key sits where nobody protects it.
    /// </remarks>
    private static ProvisionedCertificate CreateEphemeral(string machineName, DateTimeOffset now)
    {
        using X509Certificate2 created = MachineCertificate.Create(machineName, now);

        // Same round trip here too, even though it never touches the disk: without it the
        // ephemeral certificate would hold no handshake at all on Windows, and the start-up
        // message would promise a fingerprint that changes at every restart on a port that
        // never works.
        return new ProvisionedCertificate(
            LoadForServing(MachineCertificate.Export(created)),
            MachineCertificate.Fingerprint(created),
            CertificateOrigin.Ephemeral,
            null);
    }

    /// <summary>The certificate in a form a TLS server can really serve.</summary>
    /// <param name="pkcs12">The certificate packaged together with its key.</param>
    /// <returns>The reloaded certificate.</returns>
    private static X509Certificate2 LoadForServing(byte[] pkcs12) =>
        MachineCertificate.Load(pkcs12);

    /// <summary>Reads the store back, telling "it is not there" from "I cannot read it".</summary>
    /// <remarks>
    /// The distinction is the same one <see cref="CredentialStore.Read"/> makes, and here the
    /// reason is even stronger: regenerating the certificate because it could not be read would
    /// change its fingerprint, that is, it would cut off every remote dashboard in one go.
    /// Better not to start.
    /// </remarks>
    private static X509Certificate2? ReadStored(string path)
    {
        byte[] content;

        try
        {
            content = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            return MachineCertificate.Load(content);
        }
        catch (CryptographicException error)
        {
            throw new InvalidOperationException(
                $"The machine certificate '{path}' exists but can't be read ({error.Message}). " +
                "Observer will not replace it on its own: a new certificate has a new fingerprint, " +
                "and every dashboard that pinned the old one would stop connecting at once. " +
                "Delete the file deliberately if you mean to issue a new one.",
                error);
        }
    }

    /// <summary>Stores the certificate with the same recipe as the token.</summary>
    /// <remarks>
    /// Temporary file in the same folder, created ALREADY protected, atomic replacement,
    /// deletion in a <c>finally</c>. The reasons for each step are in
    /// <see cref="CredentialStore"/> and hold identically here, because here instead of a token
    /// there is a private key.
    /// </remarks>
    private static void Store(string path, byte[] pkcs12)
    {
        string tempPath = path + ".new";

        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            using (Stream stream = CredentialFile.CreateProtected(tempPath))
            {
                stream.Write(pkcs12, 0, pkcs12.Length);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string RefusalMessage(string path) =>
        $"Observer runs as a system service and can't secure its machine certificate at '{path}'. " +
        "It will not start: the private key of that certificate is what proves this machine's " +
        "identity to every dashboard that pinned its fingerprint, so leaving it where other " +
        "accounts can read it would be worse than not starting at all.";
}