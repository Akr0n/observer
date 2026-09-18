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
public sealed class CertificatePinningTransportTests : IDisposable
{
    private readonly CancellationTokenSource shutdown = new();

    /// <inheritdoc />
    public void Dispose()
    {
        shutdown.Cancel();
        shutdown.Dispose();
    }

    [Fact]
    public async Task WithTheRightFingerprintTheConnectionSucceeds()
    {
        using X509Certificate2 certificate = Generate("questa-macchina");
        TestServer server = StartServer(certificate);

        CertificatePinning pinning = new(CertificateFingerprint.From(certificate.RawDataMemory.Span));

        using HttpClient client = new(pinning.Handler());

        string response = await client.GetStringAsync(server.Address, shutdown.Token);

        Assert.Equal("ok", response);
        Assert.False(pinning.HasRejected);
    }

    [Fact]
    public async Task TheConnectionSucceedsWhenTheHostNameDoesNotMatchTheCertificate()
    {
        // LA regola che rende possibile interrogare una macchina per indirizzo. Il certificato
        // dice "un-altro-nome" e il client si collega a 127.0.0.1: la validazione ordinaria di
        // TLS lo rifiuterebbe per nome non corrispondente, e nel certificato non c'e' nessun
        // SAN di tipo iPAddress che possa salvarlo. Qui passa, perche' cio' che identifica la
        // macchina e' l'impronta.
        using X509Certificate2 certificate = Generate("un-altro-nome");
        TestServer server = StartServer(certificate);

        CertificatePinning pinning = new(CertificateFingerprint.From(certificate.RawDataMemory.Span));

        using HttpClient client = new(pinning.Handler());

        Assert.Equal("ok", await client.GetStringAsync(server.Address, shutdown.Token));
    }

    [Fact]
    public async Task WithAnotherCertificateTheConnectionFailsAndTheTokenNeverLeaves()
    {
        // Chi si mette in mezzo presenta il proprio certificato, valido quanto l'altro. Cio'
        // che deve succedere non e' soltanto che il collegamento fallisca: deve fallire PRIMA
        // che parta qualunque cosa, altrimenti il token sarebbe gia' arrivato a destinazione
        // sbagliata e rifiutare non servirebbe piu' a niente. Il server conta i byte
        // applicativi che riceve, e devono essere zero.
        using X509Certificate2 presented = Generate("chi-sta-in-mezzo");
        using X509Certificate2 expected = Generate("questa-macchina");

        TestServer server = StartServer(presented);

        CertificatePinning pinning = new(CertificateFingerprint.From(expected.RawDataMemory.Span));

        using HttpClient client = new(pinning.Handler());
        using HttpRequestMessage request = new(HttpMethod.Get, server.Address);

        request.Headers.Authorization = new("Bearer", "il-token-che-non-deve-uscire");

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.SendAsync(request, shutdown.Token));

        Assert.True(pinning.HasRejected);
        Assert.Equal(0, server.ApplicationBytesReceived);

        // E l'impronta arrivata viene conservata: senza, dopo una reinstallazione legittima
        // l'utente non avrebbe da nessuna parte il valore nuovo da ricopiare.
        Assert.Equal(
            CertificateFingerprint.From(presented.RawDataMemory.Span),
            pinning.LastSeenFingerprint);
    }

    private static X509Certificate2 Generate(string name)
    {
        using RSA key = RSA.Create(2048);

        CertificateRequest request = new(
            "CN=" + name,
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")],
            critical: false));

        SubjectAlternativeNameBuilder subjectAltNames = new();
        subjectAltNames.AddDnsName(name);
        request.CertificateExtensions.Add(subjectAltNames.Build());

        using X509Certificate2 fresh = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        // Esporta e ricarica, sempre. Su Windows un certificato che esce diritto da
        // CreateSelfSigned si carica benissimo, dice HasPrivateKey == true, e poi SChannel non
        // riesce a servirlo: l'handshake muore con "Received an unexpected EOF or 0 bytes from
        // the transport stream", un errore che non nomina la propria causa. E' la stessa
        // ragione per cui CertificateProvisioning, lato servizio, non restituisce mai un
        // certificato appena generato.
        byte[] pkcs12 = fresh.Export(X509ContentType.Pkcs12);

        X509KeyStorageFlags storageFlags = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;

        return X509CertificateLoader.LoadPkcs12(pkcs12, null, storageFlags);
    }

    private TestServer StartServer(X509Certificate2 certificate)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);

        listener.Start();

        TestServer server = new(
            new Uri($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"));

        _ = Task.Run(() => Serve(listener, certificate, server, shutdown.Token));

        return server;
    }

    private static async Task Serve(
        TcpListener listener,
        X509Certificate2 certificate,
        TestServer server,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken);
                using SslStream tls = new(connection.GetStream(), leaveInnerStreamOpen: false);

                try
                {
                    await tls.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions { ServerCertificate = certificate },
                        cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Il client ha rifiutato il certificato durante l'handshake: e' proprio il
                    // caso che uno dei test esercita, e per il server non e' un guasto.
                    continue;
                }

                byte[] buffer = new byte[4096];
                int bytesRead = await tls.ReadAsync(buffer, cancellationToken);

                server.RecordBytes(bytesRead);

                await tls.WriteAsync(
                    Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"),
                    cancellationToken);

                await tls.FlushAsync(cancellationToken);
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
            listener.Stop();
        }
    }

    /// <summary>Il server di prova: dove ascolta, e quanto gli e' davvero arrivato.</summary>
    private sealed class TestServer(Uri address)
    {
        private int received;

        /// <summary>L'indirizzo su cui il server risponde.</summary>
        public Uri Address { get; } = address;

        /// <summary>Byte arrivati DOPO l'handshake, cioe' quelli della richiesta HTTP.</summary>
        public int ApplicationBytesReceived => Volatile.Read(ref received);

        /// <summary>Registra quanto e' arrivato.</summary>
        public void RecordBytes(int count) => Interlocked.Add(ref received, count);
    }
}