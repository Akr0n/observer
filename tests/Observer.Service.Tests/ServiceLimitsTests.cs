using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
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
    public void TheRefusalWarningIsKeptOutOfTheWindowsEventLog()
    {
        // Setting a connection budget installs Kestrel's connection-limit middleware, which logs
        // one Warning per refused connection with no throttle - measured, twenty refusals gave
        // twenty lines. UseWindowsService sends Warning and above to the Application log, so
        // without this filter a caller holding no token decides how often the machine writes to
        // a 20 MB log. This asserts the CONFIGURATION rather than the behaviour, and that is the
        // honest limit of what a test can do here: the EventLog provider does not exist on Linux
        // and is not registered under TestServer, so there is no sink to observe. What it does
        // catch is the line being deleted from appsettings.json, which is how it would be lost.
        Assert.Equal(
            "Error",
            service.Services.GetRequiredService<IConfiguration>()
                ["Logging:EventLog:LogLevel:Microsoft.AspNetCore.Server.Kestrel.Connections"]);
    }

    [Fact]
    public void TheConnectionBudgetLeavesRoomForManyDashboardsAndStaysFinite()
    {
        // The floor is DERIVED, not picked: a budget below what one busy dashboard holds would
        // start refusing connections to the third person who opens a window on a host with many
        // disks, and that reads as a network fault, not as a limit - the dashboard has no way to
        // tell a refusal from a cable. Eight times it is the margin, and naming the dashboard's
        // own figure is what makes the assertion mean something: an earlier version compared
        // against a bare 64, a number that followed from nothing, and stayed green through the
        // change it existed to catch.
        Assert.InRange(
            ServiceLimits.MaxConcurrentConnectionsPerEndpoint,
            8 * ServiceLimits.DashboardConnectionsWhenBusy,
            4096);
    }

    [Fact]
    public void EveryWayThisServiceOpensAnEndpointEndsUpSpeakingHttp11()
    {
        // What this pins is the endpoint default set in Apply, for every kind of endpoint this
        // service opens on the platform it is running on. It is the only place the default's
        // REACH can be observed: on the wire the restriction shows up only in the TLS handshake,
        // because a cleartext endpoint never offers HTTP/2 to begin with - see the test below.
        // It does NOT pin the protocol named at each Listen call in the service's own code; that
        // is the belt for this brace, and the reason both exist is on ServiceLimits.Protocol.
        //
        // Declaring an endpoint does not bind it - that happens when the server starts - so no
        // port is taken, no pipe is created and no socket file appears.
        KestrelServerOptions options = new();
        ServiceLimits.Apply(options);

        HttpProtocols? overTcp = null;
        options.Listen(IPAddress.Loopback, 0, listen => overTcp = listen.Protocols);
        Assert.Equal(ServiceLimits.Protocol, overTcp);

        HttpProtocols? locally = null;

        if (OperatingSystem.IsWindows())
        {
            options.ListenNamedPipe("observer-limits-test", listen => locally = listen.Protocols);
        }
        else
        {
            options.ListenUnixSocket("/tmp/observer-limits-test.sock", listen => locally = listen.Protocols);
        }

        Assert.Equal(ServiceLimits.Protocol, locally);
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
    public async Task OnCleartextHttp2IsNotOfferedWithOrWITHOUTTheRestriction()
    {
        // This test exists to keep a claim honest, and the claim it keeps honest is a NEGATIVE
        // one. The instinct - it is the one this branch was written with - is that a cleartext
        // endpoint is where the protocol restriction earns its keep, because there is no
        // handshake in which to decline HTTP/2 and a caller can reach it by prior knowledge,
        // writing the 24-byte preface and nothing else. That instinct is wrong here, and the
        // CONTROL below is what proves it: with Kestrel's own Http1AndHttp2 default, and no
        // restriction anywhere, a cleartext endpoint already answers the preface with GOAWAY and
        // HTTP_1_1_REQUIRED. h2c needs HttpProtocols.Http2 asked for explicitly; the mixed
        // default means "HTTP/2 if ALPN chooses it", and cleartext has no ALPN.
        //
        // So ServiceLimits.Protocol changes exactly one thing on the wire: it drops "h2" from
        // what the HTTPS endpoint advertises in the handshake, which is what the TLS test above
        // pins. On the local channel it is belt only. Writing that down cost one measurement and
        // saved three paragraphs of documentation that would have been false.
        Assert.Equal(Http2Preface.RefusedAsHttp1Required, await SpeakHttp2OverTcp(withLimits: false));
        Assert.Equal(Http2Preface.RefusedAsHttp1Required, await SpeakHttp2OverTcp(withLimits: true));
    }

    private static async Task<string> SpeakHttp2OverTcp(bool withLimits)
    {
        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(options =>
        {
            if (withLimits)
            {
                ServiceLimits.Apply(options);
            }

            options.Listen(IPAddress.Loopback, 0);
        });

        // A raw socket and not an HttpClient: there is no HTTP request here, only the preface,
        // and an HTTP client would never send one by itself over cleartext.
        using TcpClient raw = new();
        await raw.ConnectAsync(IPAddress.Loopback, new Uri(bench.Addresses.Single()).Port);

        return await Http2Preface.AskWhatItSpeaks(raw.GetStream());
    }

    [Theory]
    // Nothing configured, or the one value the service speaks: the endpoint is fine.
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Http1", false)]
    [InlineData("http1", false)]
    // Everything that would put the second protocol implementation back within reach of a caller
    // the access control has not admitted. Http1AndHttp2 is refused too, and deliberately: on the
    // HTTPS endpoint it is exactly what advertises h2 in the handshake.
    [InlineData("Http2", true)]
    [InlineData("Http1AndHttp2", true)]
    [InlineData("Http3", true)]
    [InlineData("Http1AndHttp2AndHttp3", true)]
    public void AConfiguredEndpointMayNotAskForAProtocolTheServiceDoesNotSpeak(string? configured, bool refused)
    {
        // A configured endpoint does not go through any Listen call in this repository, so the
        // protocol named at each of those does not reach it - and its own key beats the endpoint
        // default. This is the rule that closes that gap, and it is pure so the table can say
        // what it covers.
        string? problem = ServiceLimits.ProblemWithConfiguredProtocol(configured);

        Assert.Equal(refused, problem is not null);

        if (refused)
        {
            // The message has to name the value it refused: an operator who set it in
            // appsettings.Local.json and an operator who set it in the environment are reading
            // the same sentence and looking in two different places.
            Assert.Contains(configured!, problem!, StringComparison.Ordinal);
        }
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
