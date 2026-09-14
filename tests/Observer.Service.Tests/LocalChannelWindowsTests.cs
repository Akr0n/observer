using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using Observer.Service.Credentials;
using Observer.Service.LocalChannel;

namespace Observer.Service.Tests;

/// <summary>
/// The local channel on Windows: the pipe opens, coexists with TCP, and the DACL is the one intended.
/// </summary>
[Collection(ProcessEnvironment.Name)]
public class LocalChannelWindowsTests
{
    [WindowsOnly]
    [SupportedOSPlatform("windows")]
    public void PipeSecurityGrantsInteractiveNotAuthenticatedUsers()
    {
        // Authenticated Users covers EVERY authenticated principal that reaches the machine,
        // including over SMB on port 445. INTERACTIVE covers only those with a session here.
        string sddl = WindowsNamedPipe.SecurityDescriptor()
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        Assert.Contains(";;;IU)", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;AU)", sddl, StringComparison.Ordinal);
    }

    [WindowsOnly]
    [SupportedOSPlatform("windows")]
    public void CurrentUserOnlyStaysOffONLYWithAPipeSecurityDescriptor()
    {
        // Regression test for a fault that starts WITHOUT errors. Measured: CurrentUserOnly =
        // false on its own produces a pipe with DACL (A;;FR;;;WD)(A;;FR;;;AN), that is, readable
        // by Everyone and by ANONYMOUS LOGON, and the host starts normally. This test exists
        // precisely because that fault has no visible symptom at all.
        NamedPipeTransportOptions options = new();

        WindowsNamedPipe.ConfigureTransport(options);

        Assert.False(options.CurrentUserOnly);
        Assert.NotNull(options.PipeSecurity);
    }

    [WindowsOnly]
    public async Task PipeAndTcpCoexistInTheSameHostAndServeTheSameEndpoints()
    {
        // The two transports coexisting is the premise of the whole project: if ListenNamedPipe
        // replaced the socket transport instead of sitting alongside it, two hosts would be
        // needed, and the plan would change shape.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.ListenNamedPipe(pipe);
        });

        Assert.Equal(2, bench.Addresses.Count);

        string tcp = bench.Addresses.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient overTcp = new() { BaseAddress = new Uri(tcp) };
        Assert.Equal("pong", await overTcp.GetStringAsync("ping", CancellationToken.None));

        using HttpClient overPipe = RealKestrelBench.ClientOn(PipeHandler(pipe));
        Assert.Equal("pong", await overPipe.GetStringAsync("ping", CancellationToken.None));
    }

    [WindowsOnly]
    public async Task ThePipeAcceptsMoreThanOneConnection()
    {
        // The FIRST instance is always created: it is from the SECOND on that
        // FILE_CREATE_PIPE_INSTANCE is needed, and Kestrel opens more than one. A DACL that
        // grants too little makes the bind fail with the misleading "address already in use",
        // so this is exactly the case to test.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenNamedPipe(pipe));

        for (int i = 0; i < 3; i++)
        {
            using HttpClient client = RealKestrelBench.ClientOn(PipeHandler(pipe));
            Assert.Equal("pong", await client.GetStringAsync("ping", CancellationToken.None));
        }
    }

    [WindowsOnly]
    public async Task ACallerConnectingThroughDotIsLocalAndIdentified()
    {
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenNamedPipe(pipe),
            app => app.MapGet("/who", (HttpContext context) =>
            {
                CallerOrigin origin = LocalCaller.Classify(context);
                return origin.Kind + "|" + (origin.Sid ?? "(none)");
            }));

        using HttpClient client = RealKestrelBench.ClientOn(PipeHandler(pipe));
        string outcome = await client.GetStringAsync("who", CancellationToken.None);

        Assert.StartsWith(nameof(CallerKind.LocalIdentified) + "|S-1-", outcome, StringComparison.Ordinal);
    }

    [WindowsOnly]
    public async Task AnonymousImpersonationIsUnidentifiedAndDoesNotCauseA500()
    {
        // The impersonation level is chosen by the CLIENT: with Anonymous the request arrives all
        // the same but the server cannot read the token. This is the ATTACK case, not an edge
        // case. Measured: the exception is SecurityException with HRESULT 0x80070543, NOT
        // IOException. A guard that caught only IOException would let a 500 escape on exactly
        // the path being closed, and a 500 is the signal that tells whoever is probing that they
        // have touched something.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenNamedPipe(pipe),
            app => app.MapGet("/who", (HttpContext context) => LocalCaller.Classify(context).Kind.ToString()));

        using HttpClient client = RealKestrelBench.ClientOn(
            PipeHandler(pipe, TokenImpersonationLevel.Anonymous));

        using HttpResponseMessage response = await client.GetAsync("who", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            nameof(CallerKind.Unidentified),
            await response.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [WindowsOnly]
    public async Task LocalhostIsNotALocalRoute()
    {
        // Measured: with serverName "localhost" GetNamedPipeClientComputerName SUCCEEDS and
        // returns "[::1]", which means the connection came in over SMB. Only "." is local.
        // This is the trap that would cost whoever writes the client hours.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenNamedPipe(pipe),
            app => app.MapGet("/who", (HttpContext context) => LocalCaller.Classify(context).Kind.ToString()));

        using HttpClient client = RealKestrelBench.ClientOn(
            PipeHandler(pipe, TokenImpersonationLevel.Identification, server: "localhost"));

        Assert.Equal(
            nameof(CallerKind.FromNetwork),
            await client.GetStringAsync("who", CancellationToken.None));
    }

    [WindowsOnly]
    public async Task ACallerOnTcpIsNeverLocal()
    {
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                options.ListenNamedPipe(pipe);
            },
            app => app.MapGet("/who", (HttpContext context) => LocalCaller.Classify(context).Kind.ToString()));

        string tcp = bench.Addresses.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient client = new() { BaseAddress = new Uri(tcp) };

        Assert.Equal(
            nameof(CallerKind.FromNetwork),
            await client.GetStringAsync("who", CancellationToken.None));
    }

    [WindowsOnly]
    public async Task TheLocalChannelNoLongerNeedsTheToken()
    {
        // This is the goal of the whole project, and the first visible change in behaviour:
        // on the machine the operating system already knows who is calling.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                options.ListenNamedPipe(pipe);
            },
            middleware: app => app.UseObserverAccessControl(Token));

        using HttpClient overPipe = RealKestrelBench.ClientOn(PipeHandler(pipe));
        Assert.Equal("pong", await overPipe.GetStringAsync("ping", CancellationToken.None));

        // On TCP nothing changes: making the token optional locally does not make it
        // optional on the network.
        string tcp = bench.Addresses.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient overTcp = new() { BaseAddress = new Uri(tcp) };
        using HttpResponseMessage withoutToken = await overTcp.GetAsync("ping", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, withoutToken.StatusCode);
    }

    [WindowsOnly]
    public async Task AnAnonymousCallerIsRefusedEVENWithTheRightToken()
    {
        // The rule "an unreadable identity is denied" must not have a loophole. The
        // impersonation level is chosen by the CLIENT: with Anonymous a caller unilaterally
        // makes itself unidentifiable while still being able to present the token. If the token
        // were enough, the rule would mean nothing.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenNamedPipe(pipe),
            middleware: app => app.UseObserverAccessControl(Token));

        using HttpClient client = RealKestrelBench.ClientOn(
            PipeHandler(pipe, TokenImpersonationLevel.Anonymous));

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TokenText);

        using HttpResponseMessage response = await client.GetAsync("ping", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [WindowsOnly]
    public async Task ALocalOnlyEndpointDoesNotExistForACallerFromTheNetwork()
    {
        // 404 and not 403: the pairing endpoints will rotate the keys, and whoever stole the
        // token must not even be able to confirm that they exist.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                options.ListenNamedPipe(pipe);
            },
            app => app.MapGet("/restricted", () => "secret").LocalOnly(),
            middleware: app => app.UseObserverAccessControl(Token));

        string tcp = bench.Addresses.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient overTcp = new() { BaseAddress = new Uri(tcp) };
        overTcp.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TokenText);

        using HttpResponseMessage fromNetwork = await overTcp.GetAsync("restricted", CancellationToken.None);

        // With the RIGHT token, it is still a 404.
        Assert.Equal(HttpStatusCode.NotFound, fromNetwork.StatusCode);

        using HttpClient overPipe = RealKestrelBench.ClientOn(PipeHandler(pipe));
        Assert.Equal("secret", await overPipe.GetStringAsync("restricted", CancellationToken.None));
    }

    /// <summary>The bench's token as TEXT, the shape a caller puts in an Authorization header.</summary>
    internal const string TokenText = "bench-token";

    /// <summary>The same token as CREDENTIALS, the shape the service's access control takes.</summary>
    internal static MachineCredentials Token =>
        new(TokenText, null, null);

    internal static string UniquePipeName() =>
        "observer-test-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    internal static SocketsHttpHandler PipeHandler(
        string name,
        TokenImpersonationLevel level = TokenImpersonationLevel.Identification,
        string server = ".") =>
        new()
        {
            ConnectCallback = async (_, cancel) =>
            {
                // "." and NOT "localhost": measured, localhost goes through SMB and would be
                // classified as a remote caller.
                NamedPipeClientStream stream = new(
                    server, name, PipeDirection.InOut, PipeOptions.Asynchronous, level);

                await stream.ConnectAsync(cancel).ConfigureAwait(false);
                return stream;
            },
        };
}