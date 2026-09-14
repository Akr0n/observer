using System.IO.Compression;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Observer.Core.Security;
using Observer.Service.Credentials;
using Observer.Service.LocalChannel;

namespace Observer.Service.Tests;

/// <summary>
/// I byte VERI sul filo, con e senza compressione, su Kestrel vero e su TLS vero.
/// </summary>
/// <remarks>
/// <para>
/// Deve stare qui e non fra i test con <c>TestServer</c>: <c>WebApplicationFactory</c> sostituisce
/// Kestrel con un trasporto in memoria, e la dimensione sul filo e' l'unica cosa che questa
/// funzione esiste per cambiare.
/// </para>
/// <para>
/// La prova che conta e' quella su HTTPS. <c>ResponseCompressionOptions.EnableForHttps</c> vale
/// <b>false</b> per impostazione predefinita: senza quell'unica opzione il servizio
/// comprimerebbe soltanto il canale locale - dove i byte non attraversano niente - e lascerebbe
/// in chiaro l'unico percorso dove costano. Un test che misurasse su HTTP resterebbe verde con
/// quella riga cancellata, cioe' non proverebbe niente.
/// </para>
/// </remarks>
// Sta nella collezione perche' costruisce un InMemoryService, che scrive variabili
// d'ambiente del PROCESSO e cancella la propria cartella temporanea: senza questa riga
// gira in parallelo alla collezione e cancella il database sotto al servizio condiviso.
// Non dichiararla era un difetto che restava verde per fortuna - il nome della classe
// decide l'ordine di xunit, e su main rinominarla E BASTA fa fallire 4 test.
[Collection(ProcessEnvironment.Name)]
public class WireCompressionTests
{
    /// <summary>Un corpo della forma vera: ripetitivo come lo storico, che e' cio' che pesa.</summary>
    private static readonly string Body = JsonSerializer.Serialize(new
    {
        resolution = "5m",
        bucketSeconds = 300,
        points = Enumerable.Range(0, 400).Select(i => new
        {
            timestamp = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero).AddMinutes(5 * i),
            count = 300,
            avg = 12.5d + (i % 7),
            min = 1.25d,
            max = 98.5d,
            last = 40.125d,
        }),
    });

    /// <summary>Un certificato ESPORTATO E RILETTO, che e' l'unico che sappia servire TLS.</summary>
    /// <remarks>
    /// Non e' un giro inutile: su Windows un certificato appena uscito da
    /// <c>CertificateRequest.CreateSelfSigned</c> ha la chiave privata solo in memoria, e Kestrel
    /// lo accetta, dichiara <c>HasPrivateKey</c> true, e poi la stretta di mano muore con
    /// "Received an unexpected EOF or 0 bytes from the transport stream". E' scritto nel codice
    /// del servizio e questa classe ci e' cascata lo stesso, quindi vale la pena ripeterlo qui:
    /// il percorso vero e' sempre esporta-e-rileggi, che e' anche quello di ogni avvio dopo il
    /// primo.
    /// </remarks>
    private static X509Certificate2 StoredCertificate()
    {
        using X509Certificate2 generated = MachineCertificate.Create("banco", DateTimeOffset.UtcNow);

        return MachineCertificate.Load(MachineCertificate.Export(generated));
    }

    [Fact]
    public async Task OverTlsTheResponseIsCompressedAndArrivesIntact()
    {
        using X509Certificate2 certificate = StoredCertificate();
        string fingerprint = MachineCertificate.Fingerprint(certificate);

        await using Bench bench = await Bench.StartAsync(certificate);

        // Senza Accept-Encoding non si comprime: la codifica si NEGOZIA, quindi un client
        // vecchio continua a ricevere esattamente cio' che riceveva prima.
        (long plainBytes, string? noEncoding, string plainBody) = await bench.ReadAsync(fingerprint, null);

        Assert.Null(noEncoding);
        Assert.Equal(Body, plainBody);

        // Con Accept-Encoding si comprime, E SU HTTPS: e' l'asserzione che diventa rossa se
        // qualcuno toglie EnableForHttps credendo di essere prudente.
        (long compressedBytes, string? encoding, string compressedBody) = await bench.ReadAsync(fingerprint, "gzip");

        Assert.Equal("gzip", encoding);

        // Byte per byte identico una volta decompresso: la compressione non deve poter cambiare
        // un numero.
        Assert.Equal(Body, compressedBody);

        // E vale la pena: meno della meta'. La soglia e' larga di proposito, perche' la
        // dimensione esatta dipende dalla versione della libreria; cio' che si pinna e' che la
        // compressione sia AVVENUTA e che serva a qualcosa. Sui corpi veri di questo servizio il
        // rapporto misurato e' fra 4x e 6x.
        Assert.True(
            compressedBytes * 2 < plainBytes,
            $"compressa {compressedBytes} byte contro {plainBytes} in chiaro: non vale il lavoro");
    }

    [Fact]
    public async Task OfferingEveryEncodingYieldsTheSmallestBodyOnTheWire()
    {
        // Il client offre "gzip, deflate, br" e a parita' di preferenza il servizio sceglie il
        // PRIMO provider registrato. Sembra un dettaglio e non lo e': il servizio SERIALIZZA,
        // cioe' lo JSON esce dal writer a pezzi con un flush per segmento, e i flush puniscono
        // Brotli molto piu' di Gzip. Misurato QUI, sul filo vero, non su un buffer compresso in
        // un colpo solo - che e' esattamente l'errore che aveva fatto preferire Brotli.
        using X509Certificate2 certificate = StoredCertificate();
        string fingerprint = MachineCertificate.Fingerprint(certificate);

        await using Bench bench = await Bench.StartAsync(certificate);

        (long withEveryEncoding, string? chosen, string body) = await bench.ReadAsync(fingerprint, "gzip, deflate, br");
        (long brotliOnly, _, _) = await bench.ReadAsync(fingerprint, "br");
        (long gzipOnly, _, _) = await bench.ReadAsync(fingerprint, "gzip");

        Assert.Equal(Body, body);

        // La scelta non e' un gusto: deve essere la piu' PICCOLA fra quelle disponibili, e il
        // test la MISURA invece di fidarsi del nome dell'encoder. Confrontare con un encoder
        // solo non proverebbe niente - se il servizio sceglie quello, si confronta con se
        // stesso - quindi si confronta con il minimo dei due.
        long best = Math.Min(gzipOnly, brotliOnly);

        Assert.True(
            withEveryEncoding <= best,
            $"offrendo tutto si ottengono {withEveryEncoding} byte (scelto: {chosen}), ma il migliore "
            + $"disponibile ne fa {best} — gzip {gzipOnly}, br {brotliOnly}");
    }

    [Fact]
    public async Task TheRealPipelineCompressesNotJustTheBench()
    {
        // Le prove qui sopra costruiscono una COPIA della registrazione di Program.cs dentro il
        // proprio host: provano che la compressione funziona, non che il servizio la ABBIA.
        // Cancellando le due righe da Program.cs resterebbero tutte verdi, e il ramo perderebbe
        // la funzione in silenzio lasciando in piedi trenta righe di commento che la
        // giustificano. Questa prova monta il servizio VERO - ServizioInMemoria e'
        // WebApplicationFactory<Program> - e guarda due cose che solo li' si vedono.
        using InMemoryService service = new();
        using HttpClient client = service.CreateAuthorizedClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "metrics/catalog");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");

        using HttpResponseMessage response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        // Sotto TestServer la richiesta e' http, quindi passa dal compressore comunque: questa
        // asserzione pinna la PRESENZA delle due righe e la loro posizione utile, non l'opzione.
        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.FirstOrDefault());

        // E l'opzione la si legge dal contenitore del servizio vero, che e' l'unico posto dove
        // EnableForHttps si puo' osservare senza un trasporto TLS.
        Assert.True(
            service.Services.GetRequiredService<IOptions<ResponseCompressionOptions>>().Value.EnableForHttps,
            "EnableForHttps e' tornato al predefinito: sulla rete non si comprimerebbe piu' niente");
    }

    [Fact]
    public async Task ARejectedRequestHasNothingToCompress()
    {
        // La precondizione su cui poggia la decisione di sicurezza scritta in Program.cs: chi non
        // ha la credenziale non ottiene alcun corpo, quindi non ottiene nemmeno un corpo
        // COMPRESSO di cui misurare la lunghezza. E' la ragione per cui la compressione sta DOPO
        // il controllo d'accesso nella pipeline, e per cui BREACH qui non ha da dove cominciare.
        // Se un giorno un rifiuto imparasse a spiegarsi con un corpo, questa prova diventa rossa
        // e la decisione va riaperta.
        using X509Certificate2 certificate = StoredCertificate();
        string fingerprint = MachineCertificate.Fingerprint(certificate);

        await using Bench bench = await Bench.StartAsync(certificate, withAccessControl: true);

        using HttpClient client = Bench.PinningClient(fingerprint);
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(bench.Address, "storia"));
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentEncoding.FirstOrDefault());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>Kestrel vero su porta effimera, con la stessa registrazione del servizio.</summary>
    private sealed class Bench : IAsyncDisposable
    {
        private readonly WebApplication app;

        private Bench(WebApplication application, Uri address)
        {
            app = application;
            Address = address;
        }

        public Uri Address { get; }

        public static async Task<Bench> StartAsync(X509Certificate2 certificate, bool withAccessControl = false)
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

            // Le sorgenti si svuotano per la ragione scritta in TrasportoHttpsTests: il progetto
            // di prova si porta in output l'appsettings.json del servizio, e senza questa riga il
            // banco proverebbe ad aprire la porta del servizio installato.
            builder.Configuration.Sources.Clear();
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Loopback, 0, port => port.UseHttps(certificate)));

            // La stessa identica registrazione di Program.cs, unica opzione compresa.
            builder.Services.AddResponseCompression(options =>
            {
                options.EnableForHttps = true;
                options.Providers.Add<GzipCompressionProvider>();
                options.Providers.Add<BrotliCompressionProvider>();
            });

            WebApplication application = builder.Build();

            if (withAccessControl)
            {
                // Il middleware VERO, non una copia scritta nel banco: la sua stessa classe
                // esiste perche' i test possano montarlo invece di riscriverlo, e riscriverlo
                // qui renderebbe la prova circolare - misurerebbe lo stub, e un 401 che un
                // giorno imparasse a portare un corpo resterebbe verde. Le credenziali sono
                // nuove e il test non manda alcun header: cade nel ramo di rifiuto vero.
                application.UseObserverAccessControl(MachineCredentials.Create());
            }

            application.UseResponseCompression();
            application.MapGet("/storia", () => Results.Content(Body, "application/json"));

            await application.StartAsync();

            return new Bench(application, new Uri(application.Urls.First(), UriKind.Absolute));
        }

        public static HttpClient PinningClient(string fingerprint)
        {
            SocketsHttpHandler handler = new();

            handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
                presented is X509Certificate2 certificate
                && CertificateFingerprint.Match(fingerprint, MachineCertificate.Fingerprint(certificate));

            return new HttpClient(handler, disposeHandler: true);
        }

        /// <summary>Legge, e riporta i byte CONTATI SUL FILO, non quelli del corpo decompresso.</summary>
        public async Task<(long WireBytes, string? ContentEncoding, string Body)> ReadAsync(string fingerprint, string? encoding)
        {
            // Niente AutomaticDecompression: l'handler non deve decomprimere da se', o i byte
            // misurati sarebbero quelli gia' espansi e la prova non direbbe niente.
            using HttpClient client = PinningClient(fingerprint);
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(Address, "storia"));

            if (encoding is not null)
            {
                request.Headers.TryAddWithoutValidation("Accept-Encoding", encoding);
            }

            using HttpResponseMessage response = await client.SendAsync(request);

            response.EnsureSuccessStatusCode();

            byte[] wireBytes = await response.Content.ReadAsByteArrayAsync();
            string? responseEncoding = response.Content.Headers.ContentEncoding.FirstOrDefault();

            if (responseEncoding is null)
            {
                return (wireBytes.Length, null, Encoding.UTF8.GetString(wireBytes));
            }

            using MemoryStream compressed = new(wireBytes);
            using Stream decompressor = responseEncoding switch
            {
                "gzip" => new GZipStream(compressed, CompressionMode.Decompress),
                "br" => new BrotliStream(compressed, CompressionMode.Decompress),
                "deflate" => new DeflateStream(compressed, CompressionMode.Decompress),
                _ => throw new InvalidOperationException($"codifica inattesa: {responseEncoding}"),
            };
            using StreamReader reader = new(decompressor);

            return (wireBytes.Length, responseEncoding, await reader.ReadToEndAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}