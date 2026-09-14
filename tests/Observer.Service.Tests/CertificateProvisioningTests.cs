using System.Security.Cryptography;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// Il certificato di macchina: generato una volta, e da li' in poi sempre lo stesso.
/// </summary>
/// <remarks>
/// La proprieta' che conta e' la STABILITA' dell'impronta fra un avvio e l'altro. I client la
/// fissano: un certificato rigenerato a ogni avvio non e' un fastidio, e' ogni dashboard remota
/// che smette di collegarsi tutta insieme, con un messaggio che parla di un attacco.
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
            // Una cartella temporanea che resta non fa danno a nessuno.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private ProvisionedCertificate ProvisionForTest() =>
        CertificateProvisioning.Provision(storePath, "macchina-di-prova", DateTimeOffset.UtcNow, false);

    [Fact]
    public void TheSecondStartREUSESTheSameCertificate()
    {
        // Il test piu' importante del file. Se questo fallisse, ogni riavvio del servizio
        // taglierebbe fuori tutte le dashboard remote insieme.
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
            Assert.True(provisioned.Certificate.HasPrivateKey, "senza chiave privata non serve a niente");

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
        // Con l'impronta fissata, una scadenza e' un guasto simultaneo di tutte le dashboard
        // remote. Non aggiungerebbe sicurezza: qui la fiducia non viene dalla scadenza.
        ProvisionedCertificate provisioned = ProvisionForTest();

        try
        {
            Assert.True(
                provisioned.Certificate.NotAfter > DateTime.Now.AddYears(5),
                "una scadenza vicina taglierebbe fuori i client senza avvisare nessuno");

            Assert.True(
                provisioned.Certificate.NotBefore < DateTime.Now,
                "un certificato che vale solo da adesso viene rifiutato da un orologio indietro");
        }
        finally
        {
            provisioned.Certificate.Dispose();
        }
    }

    [Fact]
    public void ADAMAGEDStoreIsNotReplacedBehindYourBack()
    {
        // Sostituirlo sarebbe la cosa comoda, e sarebbe sbagliata: un certificato nuovo ha
        // un'impronta nuova. Meglio fermarsi e farlo decidere a una persona.
        File.WriteAllText(MachineCertificate.PathNextTo(storePath), "non sono un PKCS#12");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => CertificateProvisioning.Provision(
                storePath,
                "macchina-di-prova",
                DateTimeOffset.UtcNow,
                runningAsService: true));

        Assert.Contains("fingerprint", error.Message, StringComparison.Ordinal);
        Assert.Equal("non sono un PKCS#12", File.ReadAllText(MachineCertificate.PathNextTo(storePath)));
    }

    [Fact]
    public void TheCertificateIsStoredNEXTToTheToken()
    {
        // Stesso perimetro, e non per comodita': la chiave privata vale quanto il token.
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
        // Un temporaneo abbandonato conterrebbe la chiave privata, e con i permessi ereditati
        // della cartella invece di quelli del deposito.
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
        // Il ripiego effimero vale per un deposito che non si riesce a METTERE IN SICUREZZA,
        // non per un certificato che c'e' ed e' illeggibile. Ripiegare in silenzio mostrerebbe
        // un servizio che parte, un'impronta nuova a ogni avvio, e nessun indizio sul file
        // rotto che sta sul disco.
        File.WriteAllText(MachineCertificate.PathNextTo(storePath), "non sono un PKCS#12");

        Assert.Throws<InvalidOperationException>(
            () => CertificateProvisioning.Provision(
                storePath,
                "macchina-di-prova",
                DateTimeOffset.UtcNow,
                runningAsService: false));
    }
}