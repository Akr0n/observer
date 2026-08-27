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
    private readonly string cartella;
    private readonly string deposito;

    public CertificateProvisioningTests()
    {
        cartella = Path.Combine(
            Path.GetTempPath(),
            "observer-cert-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(cartella);
        deposito = Path.Combine(cartella, CredentialDirectory.NomeFile);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            Directory.Delete(cartella, recursive: true);
        }
        catch (IOException)
        {
            // Una cartella temporanea che resta non fa danno a nessuno.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private ProvisionedCertificate Provvedi() =>
        CertificateProvisioning.Provvedi(deposito, "macchina-di-prova", DateTimeOffset.UtcNow, false);

    [Fact]
    public void IlSecondoAvvioRIUSAloStessoCertificato()
    {
        // Il test piu' importante del file. Se questo fallisse, ogni riavvio del servizio
        // taglierebbe fuori tutte le dashboard remote insieme.
        ProvisionedCertificate primo = Provvedi();
        ProvisionedCertificate secondo = Provvedi();

        Assert.Equal(primo.Fingerprint, secondo.Fingerprint);

        primo.Certificate.Dispose();
        secondo.Certificate.Dispose();
    }

    [Fact]
    public void IlCertificatoSiPuoUsareComeSERVERETieneLaChiavePrivata()
    {
        ProvisionedCertificate provvisto = Provvedi();

        try
        {
            Assert.True(provvisto.Certificate.HasPrivateKey, "senza chiave privata non serve a niente");

            X509EnhancedKeyUsageExtension uso = provvisto.Certificate.Extensions
                .OfType<X509EnhancedKeyUsageExtension>()
                .Single();

            Assert.Contains(
                uso.EnhancedKeyUsages.Cast<Oid>(),
                oid => oid.Value == "1.3.6.1.5.5.7.3.1");
        }
        finally
        {
            provvisto.Certificate.Dispose();
        }
    }

    [Fact]
    public void LaValiditaEabbastanzaLungaDaNonScadereSottoAiClient()
    {
        // Con l'impronta fissata, una scadenza e' un guasto simultaneo di tutte le dashboard
        // remote. Non aggiungerebbe sicurezza: qui la fiducia non viene dalla scadenza.
        ProvisionedCertificate provvisto = Provvedi();

        try
        {
            Assert.True(
                provvisto.Certificate.NotAfter > DateTime.Now.AddYears(5),
                "una scadenza vicina taglierebbe fuori i client senza avvisare nessuno");

            Assert.True(
                provvisto.Certificate.NotBefore < DateTime.Now,
                "un certificato che vale solo da adesso viene rifiutato da un orologio indietro");
        }
        finally
        {
            provvisto.Certificate.Dispose();
        }
    }

    [Fact]
    public void UnDepositoDANNEGGIATONonVieneSostituitoDiNascosto()
    {
        // Sostituirlo sarebbe la cosa comoda, e sarebbe sbagliata: un certificato nuovo ha
        // un'impronta nuova. Meglio fermarsi e farlo decidere a una persona.
        File.WriteAllText(MachineCertificate.PercorsoAccantoA(deposito), "non sono un PKCS#12");

        InvalidOperationException errore = Assert.Throws<InvalidOperationException>(
            () => CertificateProvisioning.Provvedi(
                deposito,
                "macchina-di-prova",
                DateTimeOffset.UtcNow,
                giraComeServizio: true));

        Assert.Contains("fingerprint", errore.Message, StringComparison.Ordinal);
        Assert.Equal("non sono un PKCS#12", File.ReadAllText(MachineCertificate.PercorsoAccantoA(deposito)));
    }

    [Fact]
    public void IlCertificatoSiDepositaACCANTOalToken()
    {
        // Stesso perimetro, e non per comodita': la chiave privata vale quanto il token.
        ProvisionedCertificate provvisto = Provvedi();

        try
        {
            Assert.Equal(CertificateOrigin.GeneratoEDepositato, provvisto.Origin);
            Assert.Equal(Path.GetDirectoryName(deposito), Path.GetDirectoryName(provvisto.Percorso));
            Assert.True(File.Exists(provvisto.Percorso));
        }
        finally
        {
            provvisto.Certificate.Dispose();
        }
    }

    [Fact]
    public void NonRestaMaiUnTemporaneoSulDisco()
    {
        // Un temporaneo abbandonato conterrebbe la chiave privata, e con i permessi ereditati
        // della cartella invece di quelli del deposito.
        ProvisionedCertificate provvisto = Provvedi();

        try
        {
            Assert.Empty(Directory.GetFiles(cartella, "*.nuovo"));
        }
        finally
        {
            provvisto.Certificate.Dispose();
        }
    }
}
