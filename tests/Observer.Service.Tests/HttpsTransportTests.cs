using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Observer.Core.Security;
using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// TLS vero, su Kestrel vero, con il certificato che il servizio genera per se'.
/// </summary>
/// <remarks>
/// Questa classe copre il buco piu' vecchio della suite: WebApplicationFactory sostituisce
/// Kestrel con un TestServer in memoria, quindi finora NESSUN test ha mai toccato un trasporto
/// reale. Un certificato che non si riesce a ricaricare, una chiave privata persa nel viaggio
/// verso il deposito, un'impronta calcolata su byte diversi da quelli che finiscono sul filo:
/// niente di tutto cio' sarebbe stato visto.
/// <para>
/// Il certificato non viene usato appena generato ma <b>esportato e riletto</b>, perche' quello
/// e' il percorso del SECONDO avvio, cioe' di tutti gli avvii tranne il primo.
/// </para>
/// </remarks>
public class HttpsTransportTests
{
    private const string ProbeBody = "observer";

    [Fact]
    public async Task TheCertificateReloadedFromTheStoreCanServeTls()
    {
        using CertificateRoundTrip certificate = CertificateRoundTrip.GenerateAndReload();

        Assert.True(certificate.Reloaded.HasPrivateKey, "senza chiave privata Kestrel non puo' servirlo");
        Assert.Equal(certificate.Fingerprint, MachineCertificate.Fingerprint(certificate.Generated));

        await using KestrelHost host = await KestrelHost.StartAsync(certificate.Reloaded);

        using HttpClient client = PinningClient(certificate.Fingerprint);
        string body = await client.GetStringAsync(new Uri(host.Address, "prova"));

        Assert.Equal(ProbeBody, body);
    }

    [Fact]
    public async Task AWrongFingerprintFailsTheConnection()
    {
        // Il caso che conta: cifrato non basta. Senza questo controllo chi si mette in mezzo
        // presenta il PROPRIO certificato, il collegamento riesce, e il token gli arriva.
        using CertificateRoundTrip certificate = CertificateRoundTrip.GenerateAndReload();
        using X509Certificate2 foreign = MachineCertificate.Create("un-altra-macchina", DateTimeOffset.UtcNow);

        await using KestrelHost host = await KestrelHost.StartAsync(certificate.Reloaded);

        using HttpClient client = PinningClient(MachineCertificate.Fingerprint(foreign));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetStringAsync(new Uri(host.Address, "prova")));
    }

    [Fact]
    public async Task WithoutFingerprintPinningTheSelfSignedCertificateIsRejected()
    {
        // La controprova che l'impronta e' l'UNICA cosa che regge il collegamento: con la
        // validazione ordinaria un certificato autofirmato non passa. Se un giorno questo
        // test cominciasse a fallire vorrebbe dire che il certificato e' finito in un
        // archivio di fiducia della macchina, cioe' che vale per molto piu' del dovuto.
        using CertificateRoundTrip certificate = CertificateRoundTrip.GenerateAndReload();

        await using KestrelHost host = await KestrelHost.StartAsync(certificate.Reloaded);

        using HttpClient client = new();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetStringAsync(new Uri(host.Address, "prova")));
    }

    [Fact]
    public async Task TheComputedFingerprintIsTheOneThatArrivesOnTheWire()
    {
        // Non e' una tautologia: l'impronta si calcola sui byte DER del certificato in
        // memoria, e cio' che il client vede e' cio' che Kestrel gli ha spedito. Se i due
        // insiemi di byte divergessero, il fissaggio non proteggerebbe niente e nessun altro
        // test se ne accorgerebbe.
        using CertificateRoundTrip certificate = CertificateRoundTrip.GenerateAndReload();

        await using KestrelHost host = await KestrelHost.StartAsync(certificate.Reloaded);

        string? seenByTheClient = null;

        using SocketsHttpHandler handler = new();
#pragma warning disable CA5359 // Accepts di proposito QUALUNQUE certificato: questo test serve
        handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
        {                      // a osservare cosa arriva sul filo, non a decidere se fidarsi.
            seenByTheClient = presented is X509Certificate2 arrived
                ? CertificateFingerprint.From(arrived.RawDataMemory.Span)
                : null;        // Confrontare qui trasformerebbe una divergenza fra i byte in
                               // memoria e quelli spediti in un errore di rete oscuro, invece
            return true;       // che in un confronto leggibile con un messaggio chiaro.
        };
#pragma warning restore CA5359

        using HttpClient client = new(handler);
        await client.GetStringAsync(new Uri(host.Address, "prova"));

        Assert.Equal(certificate.Fingerprint, seenByTheClient);
    }

    [Fact]
    public async Task TheFIRSTStartCertificateCanServeTls()
    {
        // Il gemello mancante, e l'assenza costava caro: il primo avvio NON rilegge dal
        // deposito, usa l'oggetto appena generato. Su Windows quella chiave privata sta solo
        // in memoria, e SChannel non la sa servire: l'handshake muore con lo stesso
        // "unexpected EOF" gia' misurato per EphemeralKeySet.
        //
        // Il sintomo era peggiore del guasto. Lato client l'eccezione interna e' una
        // IOException e non una AuthenticationException, quindi la dashboard diceva
        // "Service unreachable - check that the machine is on"; e al primo riavvio del
        // servizio spariva tutto, perche' dal secondo avvio in poi si passa dal deposito.
        // Un guasto che sembra un problema di rete e che si ripara da solo.
        string folder = Path.Combine(
            Path.GetTempPath(),
            "observer-primo-avvio-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(folder);

        try
        {
            ProvisionedCertificate provisioned = CertificateProvisioning.Provision(
                Path.Combine(folder, CredentialDirectory.FileName),
                "primo-avvio",
                DateTimeOffset.UtcNow,
                runningAsService: false);

            Assert.Equal(CertificateOrigin.CreatedAndStored, provisioned.Origin);

            try
            {
                await using KestrelHost host = await KestrelHost.StartAsync(provisioned.Certificate);

                using HttpClient client = PinningClient(provisioned.Fingerprint);

                Assert.Equal(ProbeBody, await client.GetStringAsync(new Uri(host.Address, "prova")));
            }
            finally
            {
                provisioned.Certificate.Dispose();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static HttpClient PinningClient(string fingerprint)
    {
        SocketsHttpHandler handler = new();

        handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
            presented is X509Certificate2 certificate
            && CertificateFingerprint.Match(
                fingerprint,
                CertificateFingerprint.From(certificate.RawDataMemory.Span));

        return new HttpClient(handler, disposeHandler: true);
    }

    /// <summary>Il certificato generato, depositato e riletto: il percorso del secondo avvio.</summary>
    private sealed class CertificateRoundTrip : IDisposable
    {
        private CertificateRoundTrip(X509Certificate2 generated, X509Certificate2 reloaded)
        {
            Generated = generated;
            Reloaded = reloaded;
            Fingerprint = MachineCertificate.Fingerprint(reloaded);
        }

        public X509Certificate2 Generated { get; }

        public X509Certificate2 Reloaded { get; }

        public string Fingerprint { get; }

        public static CertificateRoundTrip GenerateAndReload()
        {
            X509Certificate2 generated = MachineCertificate.Create("questa-macchina", DateTimeOffset.UtcNow);

            return new CertificateRoundTrip(generated, MachineCertificate.Load(MachineCertificate.Export(generated)));
        }

        public void Dispose()
        {
            Generated.Dispose();
            Reloaded.Dispose();
        }
    }

    /// <summary>Kestrel VERO, su una porta effimera di localhost.</summary>
    private sealed class KestrelHost : IAsyncDisposable
    {
        private readonly WebApplication app;

        private KestrelHost(WebApplication application, Uri address)
        {
            app = application;
            Address = address;
        }

        public Uri Address { get; }

        public static async Task<KestrelHost> StartAsync(X509Certificate2 certificate)
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

            // Le sorgenti di configurazione si SVUOTANO, e non e' pulizia: il progetto di
            // prova si porta in output l'appsettings.json del servizio, quindi senza
            // questa riga il builder legge la sezione Kestrel vera e prova ad aprire
            // 0.0.0.0:5057 - cioe' la porta del servizio installato su questa macchina.
            // Misurato: address already in use, su tutte e quattro le prove.
            builder.Configuration.Sources.Clear();

            // Porta 0: la sceglie il sistema. Una porta fissa farebbe fallire questa classe
            // sulla macchina di chi ha gia' qualcosa in ascolto li'.
            //
            // Listen(IPAddress.Loopback) e NON ListenLocalhost: con la porta dinamica il
            // secondo rifiuta di partire con "Dynamic port binding is not supported when
            // binding to localhost", perche' localhost sono DUE indirizzi e il sistema ne
            // sceglierebbe una diversa per ciascuno.
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Loopback, 0, port => port.UseHttps(certificate)));

            WebApplication application = builder.Build();

            application.MapGet("/prova", () => ProbeBody);

            await application.StartAsync();

            return new KestrelHost(application, new Uri(application.Urls.First(), UriKind.Absolute));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
