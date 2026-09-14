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
/// Real TLS, on real Kestrel, with the certificate the service generates for itself.
/// </summary>
/// <remarks>
/// This class covers the oldest gap in the suite: WebApplicationFactory replaces Kestrel with an
/// in-memory TestServer, so until now NO test had ever touched a real transport. A certificate
/// that cannot be reloaded, a private key lost on the way to the store, a fingerprint computed on
/// bytes other than the ones that end up on the wire: none of that would have been seen.
/// <para>
/// The certificate is not used straight after being generated but <b>exported and read back</b>,
/// because that is the path of the SECOND start, that is, of every start but the first.
/// </para>
/// </remarks>
public class HttpsTransportTests
{
    private const string ProbeBody = "observer";

    [Fact]
    public async Task TheCertificateReloadedFromTheStoreCanServeTls()
    {
        using CertificateRoundTrip certificate = CertificateRoundTrip.GenerateAndReload();

        Assert.True(certificate.Reloaded.HasPrivateKey, "without the private key Kestrel cannot serve it");
        Assert.Equal(certificate.Fingerprint, MachineCertificate.Fingerprint(certificate.Generated));

        await using KestrelHost host = await KestrelHost.StartAsync(certificate.Reloaded);

        using HttpClient client = PinningClient(certificate.Fingerprint);
        string body = await client.GetStringAsync(new Uri(host.Address, "test"));

        Assert.Equal(ProbeBody, body);
    }

    [Fact]
    public async Task AWrongFingerprintFailsTheConnection()
    {
        // The case that matters: encrypted is not enough. Without this check whoever sits in the
        // middle presents their OWN certificate, the connection succeeds, and the token reaches
        // them.
        using CertificateRoundTrip certificate = CertificateRoundTrip.GenerateAndReload();
        using X509Certificate2 foreign = MachineCertificate.Create("another-machine", DateTimeOffset.UtcNow);

        await using KestrelHost host = await KestrelHost.StartAsync(certificate.Reloaded);

        using HttpClient client = PinningClient(MachineCertificate.Fingerprint(foreign));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetStringAsync(new Uri(host.Address, "test")));
    }

    [Fact]
    public async Task WithoutFingerprintPinningTheSelfSignedCertificateIsRejected()
    {
        // The converse check, that the fingerprint is the ONLY thing holding the connection up:
        // with ordinary validation a self-signed certificate does not pass. If this test ever
        // started failing it would mean the certificate had ended up in one of the machine's
        // trust stores, that is, that it counts for far more than it should.
        using CertificateRoundTrip certificate = CertificateRoundTrip.GenerateAndReload();

        await using KestrelHost host = await KestrelHost.StartAsync(certificate.Reloaded);

        using HttpClient client = new();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetStringAsync(new Uri(host.Address, "test")));
    }

    [Fact]
    public async Task TheComputedFingerprintIsTheOneThatArrivesOnTheWire()
    {
        // Not a tautology: the fingerprint is computed on the DER bytes of the certificate in
        // memory, and what the client sees is what Kestrel sent it. If the two sets of bytes
        // diverged, pinning would protect nothing and no other test would notice.
        using CertificateRoundTrip certificate = CertificateRoundTrip.GenerateAndReload();

        await using KestrelHost host = await KestrelHost.StartAsync(certificate.Reloaded);

        string? seenByTheClient = null;

        using SocketsHttpHandler handler = new();
#pragma warning disable CA5359 // Deliberately accepts ANY certificate: this test exists to
        handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
        {                      // watch what arrives on the wire, not to decide whether to trust it.
            seenByTheClient = presented is X509Certificate2 arrived
                ? CertificateFingerprint.From(arrived.RawDataMemory.Span)
                : null;        // Comparing here would turn a divergence between the bytes in
                               // memory and the ones sent into an obscure network error, instead
            return true;       // of a readable comparison with a clear message.
        };
#pragma warning restore CA5359

        using HttpClient client = new(handler);
        await client.GetStringAsync(new Uri(host.Address, "test"));

        Assert.Equal(certificate.Fingerprint, seenByTheClient);
    }

    [Fact]
    public async Task TheFIRSTStartCertificateCanServeTls()
    {
        // The missing twin, and its absence was expensive: the first start does NOT read back
        // from the store, it uses the object just generated. On Windows that private key lives
        // only in memory, and SChannel cannot serve it: the handshake dies with the same
        // "unexpected EOF" already measured for EphemeralKeySet.
        //
        // The symptom was worse than the fault. On the client side the inner exception is an
        // IOException and not an AuthenticationException, so the dashboard said
        // "Service unreachable - check that the machine is on"; and at the first restart of the
        // service it all went away, because from the second start on the store is used.
        // A fault that looks like a network problem and repairs itself.
        string folder = Path.Combine(
            Path.GetTempPath(),
            "observer-first-start-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(folder);

        try
        {
            ProvisionedCertificate provisioned = CertificateProvisioning.Provision(
                Path.Combine(folder, CredentialDirectory.FileName),
                "first-start",
                DateTimeOffset.UtcNow,
                runningAsService: false);

            Assert.Equal(CertificateOrigin.CreatedAndStored, provisioned.Origin);

            try
            {
                await using KestrelHost host = await KestrelHost.StartAsync(provisioned.Certificate);

                using HttpClient client = PinningClient(provisioned.Fingerprint);

                Assert.Equal(ProbeBody, await client.GetStringAsync(new Uri(host.Address, "test")));
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

    /// <summary>The certificate generated, stored and read back: the second start's path.</summary>
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
            X509Certificate2 generated = MachineCertificate.Create("this-machine", DateTimeOffset.UtcNow);

            return new CertificateRoundTrip(generated, MachineCertificate.Load(MachineCertificate.Export(generated)));
        }

        public void Dispose()
        {
            Generated.Dispose();
            Reloaded.Dispose();
        }
    }

    /// <summary>A REAL Kestrel, on an ephemeral localhost port.</summary>
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

            // The configuration sources are CLEARED, and this is not tidying up: the test
            // project copies the service's appsettings.json into its output, so without
            // this line the builder reads the real Kestrel section and tries to open
            // 0.0.0.0:5057 - that is, the port of the service installed on this machine.
            // Measured: address already in use, on all four tests.
            builder.Configuration.Sources.Clear();

            // Port 0: the system picks it. A fixed port would make this class fail on the
            // machine of anyone who already has something listening there.
            //
            // Listen(IPAddress.Loopback) and NOT ListenLocalhost: with a dynamic port the
            // latter refuses to start with "Dynamic port binding is not supported when
            // binding to localhost", because localhost is TWO addresses and the system
            // would pick a different port for each.
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Loopback, 0, port => port.UseHttps(certificate)));

            WebApplication application = builder.Build();

            application.MapGet("/test", () => ProbeBody);

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
