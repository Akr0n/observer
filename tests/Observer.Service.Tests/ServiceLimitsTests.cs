using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Observer.Core.Composition;
using Observer.Core.Platform;
using Observer.Core.Processes;
using Observer.Core.Security;
using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// What a caller may cost before the access control has decided what they may read.
/// </summary>
/// <remarks>
/// These limits are the only defence on the wrong side of the 401, so a test that proves the bench
/// can be configured proves nothing: the first test here reads the options out of the REAL
/// service's container, and it is the one that turns red if <c>Program.cs</c> stops applying them.
/// The others exercise the behaviour on a real Kestrel, because none of it exists under
/// <c>TestServer</c>.
/// </remarks>
[Collection(ProcessEnvironment.Name)]
public class ServiceLimitsTests(InMemoryService service)
{
    [Fact]
    public void TheRealServiceAppliesTheLimitsAndNotOnlyTheBench()
    {
        // Every other test in this file builds its own host, so all of them would stay green with
        // the one line deleted from Program.cs, and the branch would lose the limits in silence
        // while leaving their justification standing in a comment. This reads what the real
        // service was actually configured with.
        KestrelServerOptions kestrel =
            service.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        Assert.Equal(
            ServiceLimits.MaxConcurrentConnectionsPerEndpoint,
            kestrel.Limits.MaxConcurrentConnections);

        Assert.Equal(ServiceLimits.MaxRequestBodySizeInBytes, kestrel.Limits.MaxRequestBodySize);
    }

    [Fact]
    public void TheConnectionBudgetIsFiniteAndWellClearOfWhatADashboardNeeds()
    {
        // Both ends of the range are failures, and neither one fails loudly on its own. Too low
        // and a dashboard that refreshes every history strip at once - one connection per gauge
        // row, plus the reading, the process list and the away summary - starts being refused on a
        // machine with many disks, which looks like a network fault. Unbounded, and the limit is
        // decoration: whoever reaches the port decides how much memory the measured machine gives
        // up. The assertion is a range and not the constant itself on purpose: pinning 512 would
        // only prove the number had not been retyped.
        Assert.InRange(ServiceLimits.MaxConcurrentConnectionsPerEndpoint, 64, 4096);
    }

    [Fact]
    public async Task AClientThatWouldTakeHttp2IsServedHttp11Anyway()
    {
        // IT HAS TO BE REAL TLS, and the first version of this test proved it the hard way: over
        // cleartext it asked for HTTP/2 and got an exception, which looked like the server
        // refusing. It was not. .NET's HttpClient will not speak h2c without prior knowledge
        // being switched on in the process, so the exception arrived before the server was
        // consulted - and removing ConfigureEndpointDefaults from ServiceLimits left the test
        // green. The protocol is chosen in the TLS handshake, through ALPN, so that is the only
        // place where "HTTP/2 is not offered" can be observed at all.
        using X509Certificate2 generated = MachineCertificate.Create("limits-bench", DateTimeOffset.UtcNow);
        using X509Certificate2 certificate = MachineCertificate.Load(MachineCertificate.Export(generated));

        string fingerprint = MachineCertificate.Fingerprint(certificate);

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options =>
            {
                ServiceLimits.Apply(options);
                options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate));
            },
            app => app.MapGet("/spoken", (HttpContext context) => context.Request.Protocol));

        using SocketsHttpHandler handler = new();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
            presented is X509Certificate2 arrived
            && CertificateFingerprint.From(arrived.RawDataMemory.Span) == fingerprint;

        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri(bench.Addresses.Single()),
            Timeout = TimeSpan.FromSeconds(20),
        };

        // "Or LOWER", and the policy is the whole test. It makes the client offer BOTH protocols
        // in the handshake and let the server choose, so the answer reports the server's choice:
        // against Kestrel's own default this comes back HTTP/2. With "or higher" the client
        // offers h2 ALONE, and against this endpoint the handshake fails before any HTTP exists
        // - "no common application protocol" - which is a true refusal but tells you nothing
        // about what the server would have preferred.
        using HttpRequestMessage wouldTakeTwo = new(HttpMethod.Get, "spoken")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        using HttpResponseMessage answer = await client.SendAsync(wouldTakeTwo, CancellationToken.None);

        Assert.Equal("HTTP/1.1", await answer.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheOneWriteTheServiceHasCarriesNoBodyAndIsServedAnyway()
    {
        // The kill takes its arguments in the route and the query string, so a body limit of zero
        // must not touch it. Mounting the REAL endpoint group rather than a lambda is what makes
        // this worth running - and it needs the process services registered, or the whole lazy
        // endpoint build fails and every route answers 500, /ping included.
        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options =>
            {
                ServiceLimits.Apply(options);
                options.Listen(IPAddress.Loopback, 0);
            },
            app => app.MapProcessEndpoints(),
            services: services =>
            {
                services.AddObserverMetrics();
                services.AddSingleton<IProcessLister>(sp => new SystemProcessLister(
                    ProcessIoReaders.For(HostPlatformDetector.Current, sp.GetRequiredService<IFileTextReader>())));
                services.AddSingleton<ProcessRanking>();
            });

        using HttpClient client = new()
        {
            BaseAddress = new Uri(bench.Addresses.Single()),
            Timeout = TimeSpan.FromSeconds(10),
        };

        // A pid that cannot be running, so nothing is killed: reaching the endpoint at all is the
        // whole assertion, and a 404 can only come from inside it.
        using HttpRequestMessage kill = new(HttpMethod.Post, "processes/2147483646/kill?name=nothing");
        using HttpResponseMessage answer = await client.SendAsync(kill, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }

    [Fact]
    public async Task AnEndpointThatREADSABodyIsRefusedTheFirstByte()
    {
        // The other half of the body limit, and the reason its comment is as long as it is: the
        // limit fires when the body is READ, not when it is announced. This is the shape that
        // draws the clean refusal, and it is what a future endpoint taking a body would meet.
        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options =>
            {
                ServiceLimits.Apply(options);
                options.Listen(IPAddress.Loopback, 0);
            },
            app => app.MapPost("/reads", async (HttpContext context) =>
            {
                using StreamReader reader = new(context.Request.Body);
                return await reader.ReadToEndAsync();
            }));

        using HttpClient client = new()
        {
            BaseAddress = new Uri(bench.Addresses.Single()),
            Timeout = TimeSpan.FromSeconds(10),
        };

        using StringContent oneByte = new("x");
        using HttpResponseMessage refused =
            await client.PostAsync("reads", oneByte, CancellationToken.None);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
    }
}
