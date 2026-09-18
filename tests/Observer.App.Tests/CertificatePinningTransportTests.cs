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
/// Fingerprint pinning against a <b>real</b> TLS server.
/// </summary>
/// <remarks>
/// It is the only client test that touches a transport. The others examine
/// <see cref="CertificatePinning"/> by looking at the text of its messages, that is, at what
/// that type says about itself; here we look at what it does, which is another matter. The rule
/// it defends is the one the whole remote connection rests on: <b>the identity of a machine is
/// its fingerprint, and nothing else</b>. If one day somebody wrote
/// <c>return sslPolicyErrors == SslPolicyErrors.None</c> believing they were tightening the
/// check, no test would break — it would merely kill every connection to a self-signed
/// certificate, which is every certificate Observer presents.
/// <para>
/// The service-side twin is <c>HttpsTransportTests</c>, written after this very gap had hidden
/// a real defect: a certificate that loaded perfectly well and then could not carry the
/// handshake. A test that does not touch the wire cannot see that class of fault.
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
        // THE rule that makes it possible to query a machine by address. The certificate says
        // "un-altro-nome" and the client connects to 127.0.0.1: ordinary TLS validation would
        // reject that as a name mismatch, and the certificate carries no iPAddress SAN that
        // could rescue it. Here it passes, because what identifies the machine is the
        // fingerprint.
        using X509Certificate2 certificate = Generate("un-altro-nome");
        TestServer server = StartServer(certificate);

        CertificatePinning pinning = new(CertificateFingerprint.From(certificate.RawDataMemory.Span));

        using HttpClient client = new(pinning.Handler());

        Assert.Equal("ok", await client.GetStringAsync(server.Address, shutdown.Token));
    }

    [Fact]
    public async Task WithAnotherCertificateTheConnectionFailsAndTheTokenNeverLeaves()
    {
        // Whoever sits in the middle presents their own certificate, as valid as the other
        // one. What has to happen is not only that the connection fails: it must fail BEFORE
        // anything is sent, or the token would already have reached the wrong destination and
        // rejecting it would no longer be worth anything. The server counts the application
        // bytes it receives, and they must be zero.
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

        // And the fingerprint that arrived is kept: without it, after a legitimate
        // reinstallation the user would have nowhere to read the new value from to copy it.
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

        // Export and reload, always. On Windows a certificate that comes straight out of
        // CreateSelfSigned loads perfectly well, reports HasPrivateKey == true, and then
        // SChannel cannot serve it: the handshake dies with "Received an unexpected EOF or 0
        // bytes from the transport stream", an error that does not name its own cause. It is
        // the same reason why CertificateProvisioning, on the service side, never returns a
        // freshly generated certificate.
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
                    // The client rejected the certificate during the handshake: that is exactly
                    // the case one of the tests exercises, and for the server it is no fault.
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
            // End of the test.
        }
        catch (SocketException)
        {
            // The listener has been closed.
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>The test server: where it listens, and how much really reached it.</summary>
    private sealed class TestServer(Uri address)
    {
        private int received;

        /// <summary>The address the server answers on.</summary>
        public Uri Address { get; } = address;

        /// <summary>Bytes that arrived AFTER the handshake, that is, those of the HTTP request.</summary>
        public int ApplicationBytesReceived => Volatile.Read(ref received);

        /// <summary>Records how much arrived.</summary>
        public void RecordBytes(int count) => Interlocked.Add(ref received, count);
    }
}