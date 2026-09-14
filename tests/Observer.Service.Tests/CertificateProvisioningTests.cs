using System.Security.Cryptography;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// The machine certificate: generated once, and the same one from then on.
/// </summary>
/// <remarks>
/// The property that matters is the STABILITY of the fingerprint from one start to the next.
/// Clients pin it: a certificate regenerated at every start is not an annoyance, it is every
/// remote dashboard failing to connect all at once, with a message that talks about an attack.
/// </remarks>
public class CertificateProvisioningTests : IDisposable
{
    private readonly string folder;
    private readonly string storePath;

    public CertificateProvisioningTests()
    {
        folder = Path.Combine(
            Path.GetTempPath(),
            "observer-cert-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(folder);
        storePath = Path.Combine(folder, CredentialDirectory.FileName);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind harms nobody.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private ProvisionedCertificate ProvisionForTest() =>
        CertificateProvisioning.Provision(storePath, "test-machine", DateTimeOffset.UtcNow, false);

    [Fact]
    public void TheSecondStartREUSESTheSameCertificate()
    {
        // The most important test in this file. If it failed, every restart of the service
        // would cut off all the remote dashboards at once.
        ProvisionedCertificate first = ProvisionForTest();
        ProvisionedCertificate second = ProvisionForTest();

        Assert.Equal(first.Fingerprint, second.Fingerprint);

        first.Certificate.Dispose();
        second.Certificate.Dispose();
    }

    [Fact]
    public void TheCertificateWorksAsASERVERAndKeepsThePrivateKey()
    {
        ProvisionedCertificate provisioned = ProvisionForTest();

        try
        {
            Assert.True(provisioned.Certificate.HasPrivateKey, "without the private key it is useless");

            X509EnhancedKeyUsageExtension keyUsage = provisioned.Certificate.Extensions
                .OfType<X509EnhancedKeyUsageExtension>()
                .Single();

            Assert.Contains(
                keyUsage.EnhancedKeyUsages.Cast<Oid>(),
                oid => oid.Value == "1.3.6.1.5.5.7.3.1");
        }
        finally
        {
            provisioned.Certificate.Dispose();
        }
    }

    [Fact]
    public void TheValidityIsLongEnoughNotToExpireOutFromUnderTheClients()
    {
        // With the fingerprint pinned, an expiry is a simultaneous failure of every remote
        // dashboard. It would add no security: trust here does not come from the expiry.
        ProvisionedCertificate provisioned = ProvisionForTest();

        try
        {
            Assert.True(
                provisioned.Certificate.NotAfter > DateTime.Now.AddYears(5),
                "a near expiry would cut off every client with no warning to anyone");

            Assert.True(
                provisioned.Certificate.NotBefore < DateTime.Now,
                "a certificate valid only from now on is rejected by a clock that is behind");
        }
        finally
        {
            provisioned.Certificate.Dispose();
        }
    }

    [Fact]
    public void ADAMAGEDStoreIsNotReplacedBehindYourBack()
    {
        // Replacing it would be the convenient thing to do, and it would be wrong: a new
        // certificate has a new fingerprint. Better to stop and let a person decide.
        File.WriteAllText(MachineCertificate.PathNextTo(storePath), "not a PKCS#12");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => CertificateProvisioning.Provision(
                storePath,
                "test-machine",
                DateTimeOffset.UtcNow,
                runningAsService: true));

        Assert.Contains("fingerprint", error.Message, StringComparison.Ordinal);
        Assert.Equal("not a PKCS#12", File.ReadAllText(MachineCertificate.PathNextTo(storePath)));
    }

    [Fact]
    public void TheCertificateIsStoredNEXTToTheToken()
    {
        // The same perimeter, and not for convenience: the private key is worth as much as
        // the token.
        ProvisionedCertificate provisioned = ProvisionForTest();

        try
        {
            Assert.Equal(CertificateOrigin.CreatedAndStored, provisioned.Origin);
            Assert.Equal(Path.GetDirectoryName(storePath), Path.GetDirectoryName(provisioned.Path));
            Assert.True(File.Exists(provisioned.Path));
        }
        finally
        {
            provisioned.Certificate.Dispose();
        }
    }

    [Fact]
    public void NoTempFileIsEverLeftOnDisk()
    {
        // An abandoned temp file would hold the private key, and with the permissions
        // inherited from the folder instead of the store's own.
        ProvisionedCertificate provisioned = ProvisionForTest();

        try
        {
            Assert.Empty(Directory.GetFiles(folder, "*.new"));
        }
        finally
        {
            provisioned.Certificate.Dispose();
        }
    }

    [Fact]
    public void ADamagedCertificateIsReportedEVENWhenTheServiceIsLaunchedByHand()
    {
        // The ephemeral fallback is for a store that cannot be SECURED, not for a certificate
        // that is there and unreadable. Falling back silently would show a service that
        // starts, a new fingerprint at every start, and no clue about the broken file sitting
        // on disk.
        File.WriteAllText(MachineCertificate.PathNextTo(storePath), "not a PKCS#12");

        Assert.Throws<InvalidOperationException>(
            () => CertificateProvisioning.Provision(
                storePath,
                "test-machine",
                DateTimeOffset.UtcNow,
                runningAsService: false));
    }
}