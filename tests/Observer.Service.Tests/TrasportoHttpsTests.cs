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
public class TrasportoHttpsTests
{
    private const string Risposta = "observer";

    [Fact]
    public async Task IlCertificatoRilettoDalDepositoReggeUnaConnessioneTls()
    {
        using Certificato certificato = Certificato.Depositato();

        Assert.True(certificato.Riletto.HasPrivateKey, "senza chiave privata Kestrel non puo' servirlo");
        Assert.Equal(certificato.Impronta, MachineCertificate.Impronta(certificato.Generato));

        await using Servizio servizio = await Servizio.AvviaAsync(certificato.Riletto);

        using HttpClient client = ClientCheFissa(certificato.Impronta);
        string corpo = await client.GetStringAsync(new Uri(servizio.Indirizzo, "prova"));

        Assert.Equal(Risposta, corpo);
    }

    [Fact]
    public async Task UnImprontaSbagliataFaFallireIlCollegamento()
    {
        // Il caso che conta: cifrato non basta. Senza questo controllo chi si mette in mezzo
        // presenta il PROPRIO certificato, il collegamento riesce, e il token gli arriva.
        using Certificato certificato = Certificato.Depositato();
        using X509Certificate2 estraneo = MachineCertificate.Genera("un-altra-macchina", DateTimeOffset.UtcNow);

        await using Servizio servizio = await Servizio.AvviaAsync(certificato.Riletto);

        using HttpClient client = ClientCheFissa(MachineCertificate.Impronta(estraneo));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetStringAsync(new Uri(servizio.Indirizzo, "prova")));
    }

    [Fact]
    public async Task SenzaFissareLImprontaIlCertificatoAutofirmatoVieneRifiutato()
    {
        // La controprova che l'impronta e' l'UNICA cosa che regge il collegamento: con la
        // validazione ordinaria un certificato autofirmato non passa. Se un giorno questo
        // test cominciasse a fallire vorrebbe dire che il certificato e' finito in un
        // archivio di fiducia della macchina, cioe' che vale per molto piu' del dovuto.
        using Certificato certificato = Certificato.Depositato();

        await using Servizio servizio = await Servizio.AvviaAsync(certificato.Riletto);

        using HttpClient client = new();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetStringAsync(new Uri(servizio.Indirizzo, "prova")));
    }

    [Fact]
    public async Task LImprontaCalcolataEQuellaCheArrivaSulFilo()
    {
        // Non e' una tautologia: l'impronta si calcola sui byte DER del certificato in
        // memoria, e cio' che il client vede e' cio' che Kestrel gli ha spedito. Se i due
        // insiemi di byte divergessero, il fissaggio non proteggerebbe niente e nessun altro
        // test se ne accorgerebbe.
        using Certificato certificato = Certificato.Depositato();

        await using Servizio servizio = await Servizio.AvviaAsync(certificato.Riletto);

        string? vistaDalClient = null;

        using SocketsHttpHandler handler = new();
#pragma warning disable CA5359 // Accetta di proposito QUALUNQUE certificato: questo test serve
        handler.SslOptions.RemoteCertificateValidationCallback = (_, presentato, _, _) =>
        {                      // a osservare cosa arriva sul filo, non a decidere se fidarsi.
            vistaDalClient = presentato is X509Certificate2 arrivato
                ? CertificateFingerprint.Da(arrivato.RawDataMemory.Span)
                : null;        // Confrontare qui trasformerebbe una divergenza fra i byte in
                               // memoria e quelli spediti in un errore di rete oscuro, invece
            return true;       // che in un confronto leggibile con un messaggio chiaro.
        };
#pragma warning restore CA5359

        using HttpClient client = new(handler);
        await client.GetStringAsync(new Uri(servizio.Indirizzo, "prova"));

        Assert.Equal(certificato.Impronta, vistaDalClient);
    }

    private static HttpClient ClientCheFissa(string impronta)
    {
        SocketsHttpHandler handler = new();

        handler.SslOptions.RemoteCertificateValidationCallback = (_, presentato, _, _) =>
            presentato is X509Certificate2 certificato
            && CertificateFingerprint.Uguali(
                impronta,
                CertificateFingerprint.Da(certificato.RawDataMemory.Span));

        return new HttpClient(handler, disposeHandler: true);
    }

    /// <summary>Il certificato generato, depositato e riletto: il percorso del secondo avvio.</summary>
    private sealed class Certificato : IDisposable
    {
        private Certificato(X509Certificate2 generato, X509Certificate2 riletto)
        {
            Generato = generato;
            Riletto = riletto;
            Impronta = MachineCertificate.Impronta(riletto);
        }

        public X509Certificate2 Generato { get; }

        public X509Certificate2 Riletto { get; }

        public string Impronta { get; }

        public static Certificato Depositato()
        {
            X509Certificate2 generato = MachineCertificate.Genera("questa-macchina", DateTimeOffset.UtcNow);

            return new Certificato(generato, MachineCertificate.Carica(MachineCertificate.Esporta(generato)));
        }

        public void Dispose()
        {
            Generato.Dispose();
            Riletto.Dispose();
        }
    }

    /// <summary>Kestrel VERO, su una porta effimera di localhost.</summary>
    private sealed class Servizio : IAsyncDisposable
    {
        private readonly WebApplication app;

        private Servizio(WebApplication applicazione, Uri indirizzo)
        {
            app = applicazione;
            Indirizzo = indirizzo;
        }

        public Uri Indirizzo { get; }

        public static async Task<Servizio> AvviaAsync(X509Certificate2 certificato)
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
                kestrel.Listen(IPAddress.Loopback, 0, porta => porta.UseHttps(certificato)));

            WebApplication applicazione = builder.Build();

            applicazione.MapGet("/prova", () => Risposta);

            await applicazione.StartAsync();

            return new Servizio(applicazione, new Uri(applicazione.Urls.First(), UriKind.Absolute));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
