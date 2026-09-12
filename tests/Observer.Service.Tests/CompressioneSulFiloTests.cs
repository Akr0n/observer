using System.IO.Compression;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Observer.Core.Security;
using Observer.Service.Credentials;

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
public class CompressioneSulFiloTests
{
    /// <summary>Un corpo della forma vera: ripetitivo come lo storico, che e' cio' che pesa.</summary>
    private static readonly string Corpo = JsonSerializer.Serialize(new
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
    private static X509Certificate2 Depositato()
    {
        using X509Certificate2 generato = MachineCertificate.Genera("banco", DateTimeOffset.UtcNow);

        return MachineCertificate.Carica(MachineCertificate.Esporta(generato));
    }

    [Fact]
    public async Task SuTlsLaRispostaViaggiaCompressaEArrivaIdentica()
    {
        using X509Certificate2 certificato = Depositato();
        string impronta = MachineCertificate.Impronta(certificato);

        await using Banco banco = await Banco.AvviaAsync(certificato);

        // Senza Accept-Encoding non si comprime: la codifica si NEGOZIA, quindi un client
        // vecchio continua a ricevere esattamente cio' che riceveva prima.
        (long inChiaro, string? codificaAssente, string corpoChiaro) = await banco.LeggiAsync(impronta, null);

        Assert.Null(codificaAssente);
        Assert.Equal(Corpo, corpoChiaro);

        // Con Accept-Encoding si comprime, E SU HTTPS: e' l'asserzione che diventa rossa se
        // qualcuno toglie EnableForHttps credendo di essere prudente.
        (long compressi, string? codifica, string corpoCompresso) = await banco.LeggiAsync(impronta, "gzip");

        Assert.Equal("gzip", codifica);

        // Byte per byte identico una volta decompresso: la compressione non deve poter cambiare
        // un numero.
        Assert.Equal(Corpo, corpoCompresso);

        // E vale la pena: meno della meta'. La soglia e' larga di proposito, perche' la
        // dimensione esatta dipende dalla versione della libreria; cio' che si pinna e' che la
        // compressione sia AVVENUTA e che serva a qualcosa. Sui corpi veri di questo servizio il
        // rapporto misurato e' fra 4x e 6x.
        Assert.True(
            compressi * 2 < inChiaro,
            $"compressa {compressi} byte contro {inChiaro} in chiaro: non vale il lavoro");
    }

    [Fact]
    public async Task UnaRichiestaRifiutataNonHaNienteDaComprimere()
    {
        // La precondizione su cui poggia la decisione di sicurezza scritta in Program.cs: chi non
        // ha la credenziale non ottiene alcun corpo, quindi non ottiene nemmeno un corpo
        // COMPRESSO di cui misurare la lunghezza. E' la ragione per cui la compressione sta DOPO
        // il controllo d'accesso nella pipeline, e per cui BREACH qui non ha da dove cominciare.
        // Se un giorno un rifiuto imparasse a spiegarsi con un corpo, questa prova diventa rossa
        // e la decisione va riaperta.
        using X509Certificate2 certificato = Depositato();
        string impronta = MachineCertificate.Impronta(certificato);

        await using Banco banco = await Banco.AvviaAsync(certificato, conGuardia: true);

        using HttpClient client = Banco.ClientCheFissa(impronta);
        using HttpRequestMessage richiesta = new(HttpMethod.Get, new Uri(banco.Indirizzo, "storia"));
        richiesta.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");

        using HttpResponseMessage risposta = await client.SendAsync(richiesta);

        Assert.Equal(HttpStatusCode.Unauthorized, risposta.StatusCode);
        Assert.Null(risposta.Content.Headers.ContentEncoding.FirstOrDefault());
        Assert.Empty(await risposta.Content.ReadAsByteArrayAsync());
    }

    /// <summary>Kestrel vero su porta effimera, con la stessa registrazione del servizio.</summary>
    private sealed class Banco : IAsyncDisposable
    {
        private readonly WebApplication app;

        private Banco(WebApplication applicazione, Uri indirizzo)
        {
            app = applicazione;
            Indirizzo = indirizzo;
        }

        public Uri Indirizzo { get; }

        public static async Task<Banco> AvviaAsync(X509Certificate2 certificato, bool conGuardia = false)
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

            // Le sorgenti si svuotano per la ragione scritta in TrasportoHttpsTests: il progetto
            // di prova si porta in output l'appsettings.json del servizio, e senza questa riga il
            // banco proverebbe ad aprire la porta del servizio installato.
            builder.Configuration.Sources.Clear();
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Loopback, 0, porta => porta.UseHttps(certificato)));

            // La stessa identica registrazione di Program.cs, unica opzione compresa.
            builder.Services.AddResponseCompression(opzioni => opzioni.EnableForHttps = true);

            WebApplication applicazione = builder.Build();

            if (conGuardia)
            {
                // Un rifiuto come quello vero: corto circuito PRIMA della compressione, e senza
                // corpo. Non e' il middleware di produzione - quello vuole credenziali e
                // routing - ma riproduce l'unica cosa che questa prova deve osservare.
                applicazione.Use((HttpContext contesto, RequestDelegate avanti) =>
                {
                    contesto.Response.StatusCode = StatusCodes.Status401Unauthorized;

                    return Task.CompletedTask;
                });
            }

            applicazione.UseResponseCompression();
            applicazione.MapGet("/storia", () => Results.Content(Corpo, "application/json"));

            await applicazione.StartAsync();

            return new Banco(applicazione, new Uri(applicazione.Urls.First(), UriKind.Absolute));
        }

        public static HttpClient ClientCheFissa(string impronta)
        {
            SocketsHttpHandler handler = new();

            handler.SslOptions.RemoteCertificateValidationCallback = (_, presentato, _, _) =>
                presentato is X509Certificate2 certificato
                && CertificateFingerprint.Uguali(impronta, MachineCertificate.Impronta(certificato));

            return new HttpClient(handler, disposeHandler: true);
        }

        /// <summary>Legge, e riporta i byte CONTATI SUL FILO, non quelli del corpo decompresso.</summary>
        public async Task<(long SulFilo, string? Codifica, string Corpo)> LeggiAsync(string impronta, string? codifica)
        {
            // Niente AutomaticDecompression: l'handler non deve decomprimere da se', o i byte
            // misurati sarebbero quelli gia' espansi e la prova non direbbe niente.
            using HttpClient client = ClientCheFissa(impronta);
            using HttpRequestMessage richiesta = new(HttpMethod.Get, new Uri(Indirizzo, "storia"));

            if (codifica is not null)
            {
                richiesta.Headers.TryAddWithoutValidation("Accept-Encoding", codifica);
            }

            using HttpResponseMessage risposta = await client.SendAsync(richiesta);

            risposta.EnsureSuccessStatusCode();

            byte[] byteSulFilo = await risposta.Content.ReadAsByteArrayAsync();
            string? codificaRisposta = risposta.Content.Headers.ContentEncoding.FirstOrDefault();

            if (codificaRisposta is null)
            {
                return (byteSulFilo.Length, null, Encoding.UTF8.GetString(byteSulFilo));
            }

            using MemoryStream compresso = new(byteSulFilo);
            using GZipStream espansore = new(compresso, CompressionMode.Decompress);
            using StreamReader lettore = new(espansore);

            return (byteSulFilo.Length, codificaRisposta, await lettore.ReadToEndAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}