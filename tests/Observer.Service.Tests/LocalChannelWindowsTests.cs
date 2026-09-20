using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using Microsoft.Extensions.DependencyInjection;
using Observer.Core.Processes;
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
    public async Task AFloodOnTheNetworkEndpointDoesNotCloseTheLocalChannel()
    {
        // The assumption ServiceLimits rests on, pinned where it can be seen. A connection budget
        // that were shared by the whole server would turn the limit into the cheapest denial of
        // service there is: fill the port the other machines use, and the person sitting at this
        // machine can no longer open their own dashboard - nor, since 0.23.0, stop a process on
        // it. Measured here instead of assumed, because the option is spelled
        // MaxConcurrentConnections on the SERVER and reads as if it belonged to the server.
        //
        // TWO and not ServiceLimits' own 512: the number under test is not the budget, it is
        // whether one endpoint can spend another's. Filling 512 connections would only make the
        // same test slower and give the accept loop room to be raced.
        string pipe = UniquePipeName();

        int arrived = 0;
        TaskCompletionSource bothHolding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options =>
            {
                options.Limits.MaxConcurrentConnections = 2;
                options.Listen(IPAddress.Loopback, 0);
                options.ListenNamedPipe(pipe);
            },
            app => app.MapGet("/hold", async () =>
            {
                // A held request, not a raw socket and a sleep: this way the budget is known to
                // be full when the test moves on, instead of probably full after a delay.
                if (Interlocked.Increment(ref arrived) == 2)
                {
                    bothHolding.TrySetResult();
                }

                await release.Task;

                return "released";
            }));

        string tcp = bench.Addresses.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));

        using HttpClient first = new() { BaseAddress = new Uri(tcp), Timeout = TimeSpan.FromSeconds(30) };
        using HttpClient second = new() { BaseAddress = new Uri(tcp), Timeout = TimeSpan.FromSeconds(30) };

        Task<string> holdingOne = first.GetStringAsync("hold", CancellationToken.None);
        Task<string> holdingTwo = second.GetStringAsync("hold", CancellationToken.None);

        try
        {
            await bothHolding.Task.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);

            // The budget for the network endpoint is now spent: one more there is refused.
            using HttpClient third = new() { BaseAddress = new Uri(tcp), Timeout = TimeSpan.FromSeconds(10) };
            await Assert.ThrowsAsync<HttpRequestException>(
                () => third.GetStringAsync("ping", CancellationToken.None));

            // And the local channel, which is a different endpoint, is untouched.
            using HttpClient overPipe = RealKestrelBench.ClientOn(PipeHandler(pipe));
            Assert.Equal("pong", await overPipe.GetStringAsync("ping", CancellationToken.None));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(holdingOne, holdingTwo);
        }
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
    [SupportedOSPlatform("windows")]
    public async Task TheElevationReportedIsTheCallersOwn()
    {
        // What this test CANNOT prove, said plainly because it matters: both ends live in this
        // one process, so it cannot show the service reads the CLIENT's token rather than its
        // own - the two are the same token here. That is why the elevation is read inside the
        // impersonated callback and why the comment there carries the argument. What this pins
        // is that a value arrives at all, that Windows never answers NotApplicable (which would
        // wave every caller through the one write), and that it agrees with what this process
        // really is.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenNamedPipe(pipe),
            app => app.MapGet("/who", (HttpContext context) => LocalCaller.Classify(context).Elevation.ToString()));

        using HttpClient client = RealKestrelBench.ClientOn(PipeHandler(pipe));
        string reported = await client.GetStringAsync("who", CancellationToken.None);

        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);

        Assert.Equal(elevated ? nameof(CallerElevation.Yes) : nameof(CallerElevation.No), reported);
        Assert.NotEqual(nameof(CallerElevation.NotApplicable), reported);
    }

    [WindowsOnly]
    [SupportedOSPlatform("windows")]
    public async Task AKillIsRefusedToACallerWhoseTokenIsNotElevated()
    {
        // The real row, on a machine where somebody can actually be non-elevated. The pid
        // cannot exist on either system, so this is safe to ask for whatever the answer is, and
        // the two answers are what tells the gate from the lookup: not elevated gives 403,
        // refused before the request was even examined, elevated gives 404 because the gate let
        // it through and the pid is simply not there.
        //
        // WHAT THIS ONE DOES NOT DO, and why the test below it exists: the expectation is
        // computed from what THIS PROCESS is, and the Windows CI runner is elevated - so on the
        // machine that gates every merge this degrades to asserting the 404 that the endpoint
        // gave before the gate existed. On its own it would let the guard be deleted and stay
        // green there. The LocalIdentified+No row still has no home on an elevated runner; what
        // the next test pins on every host is that the endpoint ASKS the rule at all.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenNamedPipe(pipe),
            app => app.MapProcessEndpoints(),
            middleware: null,
            // /processes needs the ranking, and without it NO route on this host works at
            // all - see the remark on the parameter. The lister it wraps is the real one.
            services => services.AddSingleton<ProcessRanking>()
                .AddSingleton<IProcessLister>(new SystemProcessLister()));

        using HttpClient client = RealKestrelBench.ClientOn(PipeHandler(pipe));

        using HttpResponseMessage response = await client.PostAsync(
            new Uri("processes/2147483646/kill?name=anything", UriKind.Relative),
            content: null,
            CancellationToken.None);

        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);

        Assert.Equal(elevated ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, response.StatusCode);

        if (elevated)
        {
            return;
        }

        // And the refusal says which 403 it is. On this route the same status also means "the
        // operating system protects that process", and the two have opposite remedies: one is
        // "pick another process", the other is "start the dashboard as an administrator".
        string body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Contains("may not stop processes", body, StringComparison.Ordinal);
    }

    [WindowsOnly]
    public async Task AKillFromACallerWhoCannotBeIdentifiedIsRefusedOnAnyHost()
    {
        // The same wiring, pinned WITHOUT depending on what this machine's own token carries.
        // The impersonation level is the CLIENT's to choose, and Anonymous makes the caller
        // unreadable: the classification is then Unidentified, whose row in the table is false
        // for every elevation there is. So the endpoint must answer 403 on a developer's
        // unelevated session and on the elevated CI runner alike - and it answers 404 the moment
        // the endpoint stops asking the rule, which is the mutation the test above cannot catch
        // where it matters.
        //
        // Note what makes this a check on the ENDPOINT and not on the middleware: this bench
        // mounts no access control, so the request really does reach Terminate. In the running
        // service Decide would have refused it one layer earlier, which is the belt to this
        // pair of braces.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenNamedPipe(pipe),
            app => app.MapProcessEndpoints(),
            middleware: null,
            services => services.AddSingleton<ProcessRanking>()
                .AddSingleton<IProcessLister>(new SystemProcessLister()));

        using HttpClient client = RealKestrelBench.ClientOn(
            PipeHandler(pipe, TokenImpersonationLevel.Anonymous));

        using HttpResponseMessage response = await client.PostAsync(
            new Uri("processes/2147483646/kill?name=anything", UriKind.Relative),
            content: null,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Contains("may not stop processes", body, StringComparison.Ordinal);
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