using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Observer.App.Services;
using Observer.Core.Security;

namespace Observer.App.Tests;

/// <summary>
/// Il fissaggio dell'impronta contro un server TLS <b>vero</b>.
/// </summary>
/// <remarks>
/// E' l'unico test del client che tocca un trasporto. Gli altri esaminano
/// <see cref="CertificatePinning"/> guardando il testo dei suoi messaggi, cioe' cio' che quel
/// tipo dice di se stesso; qui si guarda cosa fa, che e' un'altra cosa. La regola che difende
/// e' quella su cui poggia tutto il collegamento remoto: <b>l'identita' di una macchina e' la
/// sua impronta, e nient'altro</b>. Se un giorno qualcuno scrivesse
/// <c>return sslPolicyErrors == SslPolicyErrors.None</c> credendo di rafforzare il controllo,
/// non romperebbe nessun test — spegnerebbe soltanto ogni collegamento verso un certificato
/// autofirmato, che sono tutti quelli che Observer presenta.
/// <para>
/// Il gemello lato servizio e' <c>TrasportoHttpsTests</c>, nato dopo che questa stessa lacuna
/// aveva nascosto un difetto vero: un certificato che si caricava benissimo e poi non reggeva
/// l'handshake. Un test che non tocca il filo non vede quella classe di guasti.
/// </para>
/// </remarks>
public sealed class FissaggioSuTrasportoVeroTests : IDisposable
{
    private readonly CancellationTokenSource spegnimento = new();

    /// <inheritdoc />
    public void Dispose()
    {
        spegnimento.Cancel();
        spegnimento.Dispose();
    }

    [Fact]
    public async Task ConLImprontaGiustaIlCollegamentoRiesce()
    {
        using X509Certificate2 certificato = Genera("questa-macchina");
        Ascoltatore server = Avvia(certificato);

        CertificatePinning fissaggio = new(CertificateFingerprint.Da(certificato.RawDataMemory.Span));

        using HttpClient client = new(fissaggio.Handler());

        string risposta = await client.GetStringAsync(server.Indirizzo, spegnimento.Token);

        Assert.Equal("ok", risposta);
        Assert.False(fissaggio.HaRifiutato);
    }

    [Fact]
    public async Task UnNomeDiversoDaQuelloDelCertificatoNonBastaARifiutare()
    {
        // LA regola che rende possibile interrogare una macchina per indirizzo. Il certificato
        // dice "un-altro-nome" e il client si collega a 127.0.0.1: la validazione ordinaria di
        // TLS lo rifiuterebbe per nome non corrispondente, e nel certificato non c'e' nessun
        // SAN di tipo iPAddress che possa salvarlo. Qui passa, perche' cio' che identifica la
        // macchina e' l'impronta.
        using X509Certificate2 certificato = Genera("un-altro-nome");
        Ascoltatore server = Avvia(certificato);

        CertificatePinning fissaggio = new(CertificateFingerprint.Da(certificato.RawDataMemory.Span));

        using HttpClient client = new(fissaggio.Handler());

        Assert.Equal("ok", await client.GetStringAsync(server.Indirizzo, spegnimento.Token));
    }

    [Fact]
    public async Task ConUnAltroCertificatoIlCollegamentoCadeEIlTokenNonParte()
    {
        // Chi si mette in mezzo presenta il proprio certificato, valido quanto l'altro. Cio'
        // che deve succedere non e' soltanto che il collegamento fallisca: deve fallire PRIMA
        // che parta qualunque cosa, altrimenti il token sarebbe gia' arrivato a destinazione
        // sbagliata e rifiutare non servirebbe piu' a niente. Il server conta i byte
        // applicativi che riceve, e devono essere zero.
        using X509Certificate2 presentato = Genera("chi-sta-in-mezzo");
        using X509Certificate2 atteso = Genera("questa-macchina");

        Ascoltatore server = Avvia(presentato);

        CertificatePinning fissaggio = new(CertificateFingerprint.Da(atteso.RawDataMemory.Span));

        using HttpClient client = new(fissaggio.Handler());
        using HttpRequestMessage richiesta = new(HttpMethod.Get, server.Indirizzo);

        richiesta.Headers.Authorization = new("Bearer", "il-token-che-non-deve-uscire");

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.SendAsync(richiesta, spegnimento.Token));

        Assert.True(fissaggio.HaRifiutato);
        Assert.Equal(0, server.ByteApplicativiRicevuti);

        // E l'impronta arrivata viene conservata: senza, dopo una reinstallazione legittima
        // l'utente non avrebbe da nessuna parte il valore nuovo da ricopiare.
        Assert.Equal(
            CertificateFingerprint.Da(presentato.RawDataMemory.Span),
            fissaggio.UltimaVista);
    }

    private static X509Certificate2 Genera(string nome)
    {
        using RSA chiave = RSA.Create(2048);

        CertificateRequest richiesta = new(
            "CN=" + nome,
            chiave,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        richiesta.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")],
            critical: false));

        SubjectAlternativeNameBuilder nomi = new();
        nomi.AddDnsName(nome);
        richiesta.CertificateExtensions.Add(nomi.Build());

        using X509Certificate2 appena = richiesta.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        // Esporta e ricarica, sempre. Su Windows un certificato che esce diritto da
        // CreateSelfSigned si carica benissimo, dice HasPrivateKey == true, e poi SChannel non
        // riesce a servirlo: l'handshake muore con "Received an unexpected EOF or 0 bytes from
        // the transport stream", un errore che non nomina la propria causa. E' la stessa
        // ragione per cui CertificateProvisioning, lato servizio, non restituisce mai un
        // certificato appena generato.
        byte[] pkcs12 = appena.Export(X509ContentType.Pkcs12);

        X509KeyStorageFlags flag = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;

        return X509CertificateLoader.LoadPkcs12(pkcs12, null, flag);
    }

    private Ascoltatore Avvia(X509Certificate2 certificato)
    {
        TcpListener ascoltatore = new(IPAddress.Loopback, 0);

        ascoltatore.Start();

        Ascoltatore stato = new(
            new Uri($"https://127.0.0.1:{((IPEndPoint)ascoltatore.LocalEndpoint).Port}/"));

        _ = Task.Run(() => Servi(ascoltatore, certificato, stato, spegnimento.Token));

        return stato;
    }

    private static async Task Servi(
        TcpListener ascoltatore,
        X509Certificate2 certificato,
        Ascoltatore stato,
        CancellationToken fine)
    {
        try
        {
            while (!fine.IsCancellationRequested)
            {
                using TcpClient connessione = await ascoltatore.AcceptTcpClientAsync(fine);
                using SslStream tls = new(connessione.GetStream(), leaveInnerStreamOpen: false);

                try
                {
                    await tls.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions { ServerCertificate = certificato },
                        fine);
                }
                catch (Exception) when (!fine.IsCancellationRequested)
                {
                    // Il client ha rifiutato il certificato durante l'handshake: e' proprio il
                    // caso che uno dei test esercita, e per il server non e' un guasto.
                    continue;
                }

                byte[] cesto = new byte[4096];
                int letti = await tls.ReadAsync(cesto, fine);

                stato.Conta(letti);

                await tls.WriteAsync(
                    Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"),
                    fine);

                await tls.FlushAsync(fine);
            }
        }
        catch (OperationCanceledException)
        {
            // Fine del test.
        }
        catch (SocketException)
        {
            // L'ascoltatore e' stato chiuso.
        }
        finally
        {
            ascoltatore.Stop();
        }
    }

    /// <summary>Il server di prova: dove ascolta, e quanto gli e' davvero arrivato.</summary>
    private sealed class Ascoltatore(Uri indirizzo)
    {
        private int ricevuti;

        /// <summary>L'indirizzo su cui il server risponde.</summary>
        public Uri Indirizzo { get; } = indirizzo;

        /// <summary>Byte arrivati DOPO l'handshake, cioe' quelli della richiesta HTTP.</summary>
        public int ByteApplicativiRicevuti => Volatile.Read(ref ricevuti);

        /// <summary>Registra quanto e' arrivato.</summary>
        public void Conta(int quanti) => Interlocked.Add(ref ricevuti, quanti);
    }
}