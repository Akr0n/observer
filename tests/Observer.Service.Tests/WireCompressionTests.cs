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
/// The REAL bytes on the wire, with and without compression, on a real Kestrel and over real TLS.
/// </summary>
/// <remarks>
/// <para>
/// It has to live here and not among the <c>TestServer</c> tests: <c>WebApplicationFactory</c>
/// replaces Kestrel with an in-memory transport, and the size on the wire is the only thing this
/// feature exists to change.
/// </para>
/// <para>
/// The test that counts is the one over HTTPS. <c>ResponseCompressionOptions.EnableForHttps</c> is
/// <b>false</b> by default: without that single option the service would compress only the local
/// channel - where the bytes cross nothing - and would leave uncompressed the one path where they
/// actually cost. A test that measured on HTTP would stay green with that line deleted, which means it
/// would prove nothing.
/// </para>
/// </remarks>
// It belongs in the collection because it builds an InMemoryService, which writes PROCESS
// environment variables and deletes its own temp directory: without this line it runs in
// parallel with the collection and deletes the database out from under the shared service.
// Not declaring it was a defect that stayed green by luck - the class name decides xunit's
// ordering, and on main renaming it ALONE makes 4 tests fail.
[Collection(ProcessEnvironment.Name)]
public class WireCompressionTests
{
    /// <summary>A body with the real shape: repetitive like the history, which is the heavy one.</summary>
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

    /// <summary>A certificate EXPORTED AND RELOADED, which is the only one that can serve TLS.</summary>
    /// <remarks>
    /// It is not a pointless round trip: on Windows a certificate fresh out of
    /// <c>CertificateRequest.CreateSelfSigned</c> has its private key only in memory, and Kestrel
    /// accepts it, reports <c>HasPrivateKey</c> true, and then the handshake dies with
    /// "Received an unexpected EOF or 0 bytes from the transport stream". It is written in the
    /// service's code and this class fell for it all the same, so it is worth repeating here:
    /// the real path is always export-and-reload, which is also the path of every start after the
    /// first.
    /// </remarks>
    private static X509Certificate2 StoredCertificate()
    {
        using X509Certificate2 generated = MachineCertificate.Create("bench", DateTimeOffset.UtcNow);

        return MachineCertificate.Load(MachineCertificate.Export(generated));
    }

    [Fact]
    public async Task OverTlsTheResponseIsCompressedAndArrivesIntact()
    {
        using X509Certificate2 certificate = StoredCertificate();
        string fingerprint = MachineCertificate.Fingerprint(certificate);

        await using Bench bench = await Bench.StartAsync(certificate);

        // Without Accept-Encoding nothing is compressed: the encoding is NEGOTIATED, so an old
        // client keeps receiving exactly what it received before.
        (long plainBytes, string? noEncoding, string plainBody) = await bench.ReadAsync(fingerprint, null);

        Assert.Null(noEncoding);
        Assert.Equal(Body, plainBody);

        // With Accept-Encoding it does compress, AND OVER HTTPS: this is the assertion that turns
        // red if someone removes EnableForHttps thinking they are being cautious.
        (long compressedBytes, string? encoding, string compressedBody) = await bench.ReadAsync(fingerprint, "gzip");

        Assert.Equal("gzip", encoding);

        // Byte for byte identical once decompressed: compression must not be able to change a
        // number.
        Assert.Equal(Body, compressedBody);

        // And it is worth it: less than half. The threshold is deliberately loose, because the
        // exact size depends on the library version; what is pinned is that the compression
        // HAPPENED and that it buys something. On this service's real bodies the measured ratio
        // is between 4x and 6x.
        Assert.True(
            compressedBytes * 2 < plainBytes,
            $"compressed {compressedBytes} bytes against {plainBytes} uncompressed: not worth the work");
    }

    [Fact]
    public async Task OfferingEveryEncodingYieldsTheSmallestBodyOnTheWire()
    {
        // The client offers "gzip, deflate, br" and, at equal preference, the service picks the
        // FIRST registered provider. It looks like a detail and it is not: the service
        // SERIALISES, that is, the JSON leaves the writer in chunks, one flush per segment,
        // and flushes punish Brotli far more than Gzip. Measured HERE, on the real wire, not on a
        // buffer compressed in one go - which is exactly the mistake that had led to preferring
        // Brotli.
        using X509Certificate2 certificate = StoredCertificate();
        string fingerprint = MachineCertificate.Fingerprint(certificate);

        await using Bench bench = await Bench.StartAsync(certificate);

        (long withEveryEncoding, string? chosen, string body) = await bench.ReadAsync(fingerprint, "gzip, deflate, br");
        (long brotliOnly, _, _) = await bench.ReadAsync(fingerprint, "br");
        (long gzipOnly, _, _) = await bench.ReadAsync(fingerprint, "gzip");

        Assert.Equal(Body, body);

        // The choice is not a matter of taste: it must be the SMALLEST of the ones available, and
        // the test MEASURES it instead of trusting the encoder's name. Comparing against a single
        // encoder would prove nothing - if the service picks that one, it is comparing with
        // itself - so it is compared against the minimum of the two.
        long best = Math.Min(gzipOnly, brotliOnly);

        Assert.True(
            withEveryEncoding <= best,
            $"offering every encoding yields {withEveryEncoding} bytes (chosen: {chosen}), but the best "
            + $"available does {best} — gzip {gzipOnly}, br {brotliOnly}");
    }

    [Fact]
    public async Task TheRealPipelineCompressesNotJustTheBench()
    {
        // The tests above build a COPY of Program.cs's registration inside their own host: they
        // prove that compression works, not that the service HAS it. Deleting the two lines from
        // Program.cs would leave them all green, and the branch would lose the feature silently
        // while leaving thirty lines of comment standing to justify it. This test mounts the REAL
        // service - InMemoryService is WebApplicationFactory<Program> - and looks at two things
        // that can only be seen there.
        using InMemoryService service = new();
        using HttpClient client = service.CreateAuthorizedClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "metrics/catalog");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");

        using HttpResponseMessage response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        // Under TestServer the request is http, so it goes through the compressor anyway: this
        // assertion pins the PRESENCE of the two lines and their useful position, not the option.
        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.FirstOrDefault());

        // And the option is read from the real service's container, which is the only place where
        // EnableForHttps can be observed without a TLS transport.
        Assert.True(
            service.Services.GetRequiredService<IOptions<ResponseCompressionOptions>>().Value.EnableForHttps,
            "EnableForHttps is back to its default: nothing would be compressed on the network any more");
    }

    [Fact]
    public async Task ARejectedRequestHasNothingToCompress()
    {
        // The precondition the security decision written in Program.cs rests on: whoever does not
        // hold the credential gets no body at all, so does not get a COMPRESSED body whose length
        // could be measured either. It is the reason compression sits AFTER the access control in
        // the pipeline, and why BREACH has nowhere to start here. If one day a refusal learned to
        // explain itself with a body, this test turns red and the decision has to be reopened.
        using X509Certificate2 certificate = StoredCertificate();
        string fingerprint = MachineCertificate.Fingerprint(certificate);

        await using Bench bench = await Bench.StartAsync(certificate, withAccessControl: true);

        using HttpClient client = Bench.PinningClient(fingerprint);
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(bench.Address, "history"));
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentEncoding.FirstOrDefault());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>A real Kestrel on an ephemeral port, with the same registration as the service.</summary>
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

            // The sources are cleared for the reason written in HttpsTransportTests: the test
            // project copies the service's appsettings.json into its output, and without this
            // line the bench would be born with the installed service's settings instead of
            // the ephemeral port it asks for.
            builder.Configuration.Sources.Clear();
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Loopback, 0, port => port.UseHttps(certificate)));

            // The exact same registration as Program.cs, the one option included.
            builder.Services.AddResponseCompression(options =>
            {
                options.EnableForHttps = true;
                options.Providers.Add<GzipCompressionProvider>();
                options.Providers.Add<BrotliCompressionProvider>();
            });

            WebApplication application = builder.Build();

            if (withAccessControl)
            {
                // The REAL middleware, not a copy written into the bench: its own class exists
                // so that tests can mount it instead of rewriting it, and rewriting it here
                // would make the test circular - it would measure the stub, and a 401 that one
                // day learned to carry a body would stay green. The credentials are new and the
                // test sends no header: it falls into the real refusal branch.
                application.UseObserverAccessControl(MachineCredentials.Create());
            }

            application.UseResponseCompression();
            application.MapGet("/history", () => Results.Content(Body, "application/json"));

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

        /// <summary>Reads, and reports the bytes COUNTED ON THE WIRE, not those of the decompressed body.</summary>
        public async Task<(long WireBytes, string? ContentEncoding, string Body)> ReadAsync(string fingerprint, string? encoding)
        {
            // No AutomaticDecompression: the handler must not decompress on its own, or the bytes
            // measured would be the already expanded ones and the test would say nothing.
            using HttpClient client = PinningClient(fingerprint);
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(Address, "history"));

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
                _ => throw new InvalidOperationException($"unexpected encoding: {responseEncoding}"),
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