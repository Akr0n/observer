using System.Net.Sockets;
using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Observer.Service.Credentials;
using Observer.Service.LocalChannel;

namespace Observer.Service.Tests;

/// <summary>The local channel on Linux.</summary>
/// <remarks>
/// The attribute goes on the CLASS and not on the individual methods: CA1416 looks at the call
/// site, and [LinuxOnly] is a RUNTIME jump the analyzer cannot see. Every test in here is Linux
/// anyway, so annotating the class is the most honest form.
/// </remarks>
[Collection(ProcessEnvironment.Name)]
[SupportedOSPlatform("linux")]
public class LocalChannelLinuxTests
{
    /// <summary>The shape the service really serialises with: camelCase, as ASP.NET Core does.</summary>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    [LinuxOnly]
    public async Task TheUnixSocketServesTheSameEndpointsAsTcp()
    {
        string path = ShortSocketPath();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenUnixSocket(path));

        using HttpClient client = RealKestrelBench.ClientOn(SocketHandler(path));

        Assert.Equal("pong", await client.GetStringAsync("ping", CancellationToken.None));
    }

    [LinuxOnly]
    public async Task ACleanShutdownDeletesTheSocketFile()
    {
        // Against the widespread belief that on Linux the file always survives: .NET does the
        // unlink explicitly, because UnixDomainSocketEndPoint carries a boundFileName. Cleanup is
        // needed ONLY after a violent death.
        string path = ShortSocketPath();

        await using (RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenUnixSocket(path)))
        {
            Assert.True(File.Exists(path));
        }

        Assert.False(File.Exists(path));
    }

    [LinuxOnly]
    public async Task CleanupDoesNotStealTheSocketFromALiveInstance()
    {
        // "If the file exists, delete it" lets a second instance steal the socket from a first,
        // healthy instance. Cleanup has to probe, and probe with a TIMEOUT.
        string path = ShortSocketPath();

        await using RealKestrelBench alive = await RealKestrelBench.StartAsync(
            options => options.ListenUnixSocket(path));

        bool removed = await LinuxUnixSocket.RemoveStaleSocketAsync(
            path, TimeSpan.FromMilliseconds(500));

        Assert.False(removed);
        Assert.True(File.Exists(path));
    }

    [LinuxOnly]
    public async Task CleanupRemovesAnOrphanedSocket()
    {
        string path = ShortSocketPath();

        await using (RealKestrelBench dead = await RealKestrelBench.StartAsync(
            options => options.ListenUnixSocket(path)))
        {
            Assert.True(File.Exists(path));
        }

        // Simulate the violent death: the file is left behind with nobody listening.
        await File.WriteAllTextAsync(path, string.Empty, CancellationToken.None);

        Assert.True(await LinuxUnixSocket.RemoveStaleSocketAsync(path, TimeSpan.FromSeconds(2)));
        Assert.False(File.Exists(path));
    }

    [LinuxOnly]
    public void TheDirectoryModeIsSetEvenWhenTheDirectoryAlreadyExists()
    {
        // Directory.CreateDirectory(path, mode) does NOT apply the mode to a directory that
        // already exists: measured, it is a silent no-op. So the protection would not exist from
        // the second start onwards, nor on a /run/observer created by systemd with its own 0755.
        string folder = Path.Combine(Path.GetTempPath(), "obs-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(
            folder,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        try
        {
            LinuxUnixSocket.PreparePath(Path.Combine(folder, "o.sock"));

            UnixFileMode mode = File.GetUnixFileMode(folder);

            Assert.Equal(UnixFileMode.None, mode & UnixFileMode.OtherRead);
            Assert.Equal(UnixFileMode.None, mode & UnixFileMode.OtherWrite);
            Assert.Equal(UnixFileMode.None, mode & UnixFileMode.OtherExecute);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [LinuxOnly]
    public async Task ACallerOnTheUnixSocketIsIdentifiedByItsUid()
    {
        string path = ShortSocketPath();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenUnixSocket(path),
            app => app.MapGet("/who", (HttpContext context) =>
            {
                CallerOrigin origin = LocalCaller.Classify(context);
                return origin.Kind + "|" + (origin.Sid ?? "(none)");
            }));

        using HttpClient client = RealKestrelBench.ClientOn(SocketHandler(path));
        string outcome = await client.GetStringAsync("who", CancellationToken.None);

        // On a unix socket the caller is ALWAYS on the same machine: there is no SMB route like
        // the one Windows has. The only question is whether the uid can be read.
        string[] parts = outcome.Split('|');

        Assert.Equal(nameof(CallerKind.LocalIdentified), parts[0]);
        Assert.True(uint.TryParse(parts[1], out _), "non-numeric uid: " + parts[1]);
    }

    [LinuxOnly]
    public async Task ACallerOnTheUnixSocketIsNotAskedAboutElevation()
    {
        // NotApplicable is a DECISION here, not an absence, and it is the only value that lets
        // a kill through on this platform: the socket is 0660 inside a 0750 directory, both
        // owned by the service's user and group, so the kernel has already turned away anyone
        // outside that group. Windows has no equivalent - its pipe admits every interactive
        // user on purpose - which is why the question is asked there and not here.
        //
        // Without this test, dropping that argument would leave every test on both runners
        // green while EVERY kill from a Linux dashboard started answering 403: the default is
        // No, and No refuses. A total functional break, invisible to 800 tests.
        string path = ShortSocketPath();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenUnixSocket(path),
            app => app.MapGet("/who", (HttpContext context) =>
                LocalCaller.Classify(context).Elevation.ToString()));

        using HttpClient client = RealKestrelBench.ClientOn(SocketHandler(path));
        string reported = await client.GetStringAsync("who", CancellationToken.None);

        Assert.Equal(nameof(CallerElevation.NotApplicable), reported);
    }

    [LinuxOnly]
    public async Task ReloadingTheCredentialsOverTheSocketAdoptsTheStoreAndSaysWhichFile()
    {
        // THE END-TO-END OF THE WHOLE FEATURE, and it lives on this runner rather than the other
        // one for a reason that is not convenience. The reload asks for an elevated caller on
        // Windows, and a test cannot choose its own process's elevation - so a Windows twin would
        // have to branch on the host, which is exactly the mistake #95 shipped and then fixed.
        // Here the question does not arise: the socket's own mode has already turned away anyone
        // outside the service's group, so the elevation is NotApplicable on every host.
        //
        // What it proves is the claim the CLI makes to the operator: the running service read
        // THAT file, identified by the stamp, and now refuses the key that was in it before.
        string directory = Path.Combine(Path.GetTempPath(), "obs-reload-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            string store = Path.Combine(directory, CredentialDirectory.FileName);
            CredentialStore.Write(store, new MachineCredentials("leaked-key", null, null));

            CredentialSource source = new(new ProvisionedCredentials(
                CredentialStore.Read(store)!, CredentialOrigin.Stored, store));

            Assert.True(source.IsTokenValid("Bearer leaked-key", DateTimeOffset.UtcNow));

            string path = ShortSocketPath();

            await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
                options => options.ListenUnixSocket(path),
                app => app.MapCredentialEndpoints(),
                middleware: app => app.UseObserverAccessControl(source),
                services: services => services.AddSingleton(source));

            // The rotation, exactly as the verb does it: a new key and NO previous one, so the
            // leaked secret is not left on disk at all.
            CredentialStore.Write(store, MachineCredentials.Create());
            DateTimeOffset written = File.GetLastWriteTimeUtc(store);

            using HttpClient client = RealKestrelBench.ClientOn(SocketHandler(path));
            using HttpResponseMessage answer =
                await client.PostAsync("credentials/reload", content: null, CancellationToken.None);

            Assert.Equal(System.Net.HttpStatusCode.OK, answer.StatusCode);

            string body = await answer.Content.ReadAsStringAsync(CancellationToken.None);

            // Deserialised through the service's OWN wire record, so a field renamed on one side
            // and not the other turns this red instead of quietly reading null.
            CredentialReloadResponse? reported = JsonSerializer.Deserialize<CredentialReloadResponse>(
                body, WireOptions);

            Assert.Equal(written, reported!.StoreWrittenAt);
            Assert.Equal(store, reported.StorePath);
            Assert.Contains("no previous key", body, StringComparison.Ordinal);
            Assert.DoesNotContain("leaked-key", body, StringComparison.Ordinal);

            // And the part that is the point: the key that was in force is refused now.
            Assert.False(source.IsTokenValid("Bearer leaked-key", DateTimeOffset.UtcNow));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxOnly]
    public async Task ListenUnixSocketNamesTheProtocolItselfAndDoesNotInheritIt()
    {
        // The Linux twin of the Windows test on the same line, and it pins the fix for the first
        // review's blocking finding: the protocol used to come from ServiceLimits.Apply's
        // endpoint DEFAULT, which reaches only endpoints declared after it, so the restriction
        // was an invariant of the order Program.cs registers its callbacks in.
        //
        // ConfigureEndpointDefaults REPLACES rather than accumulates, so the default registered
        // here - before LocalChannelSetup declares anything - is the one in force: it captures
        // the ListenOptions and sets nothing. Whatever the protocol reads afterwards came from
        // the ListenUnixSocket call itself.
        List<ListenOptions> declared = [];

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureEndpointDefaults(declared.Add));

        LocalChannelOptions options = new() { SocketPath = ShortSocketPath() };

        Assert.NotNull(await LocalChannelSetup.ConfigureAsync(builder, options));

        using WebApplication app = builder.Build();

        // Resolving the options runs the registered callbacks. Nothing is bound: that happens on
        // StartAsync, which is deliberately not called, so no socket is left behind.
        _ = app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        Assert.Equal(ServiceLimits.Protocol, Assert.Single(declared).Protocols);
    }

    // There is deliberately no HTTP/2-preface test here, and the absence is the finding: on Linux
    // the unix socket and the TCP endpoint are bound by the SAME transport, so the cross-platform
    // test in ServiceLimitsTests - which measures that a cleartext endpoint refuses the preface
    // with or WITHOUT the restriction - already covers this socket. Windows keeps its own twin
    // because the named pipe is a transport of its own.

    [LinuxOnly]
    public async Task AFloodOnTheNetworkEndpointDoesNotCloseTheLocalChannel()
    {
        // The Linux half of the assumption ServiceLimits rests on. What it pins HERE is narrower
        // than on the Windows twin, and saying so is the point: on Linux both endpoints are bound
        // by the SAME transport - SocketTransportFactory takes a UnixDomainSocketEndPoint as
        // readily as a TCP one - so this proves the budget is per ENDPOINT rather than per server.
        // The cross-TRANSPORT half, where a leak would be a denial of service on one runner only,
        // is the Windows twin's alone, because only Windows has a second transport.
        string path = ShortSocketPath();

        int arrived = 0;
        TaskCompletionSource bothHolding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options =>
            {
                options.Limits.MaxConcurrentConnections = 2;
                options.Listen(System.Net.IPAddress.Loopback, 0);
                options.ListenUnixSocket(path);
            },
            app => app.MapGet("/hold", async () =>
            {
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

            using HttpClient third = new() { BaseAddress = new Uri(tcp), Timeout = TimeSpan.FromSeconds(10) };
            await Assert.ThrowsAsync<HttpRequestException>(
                () => third.GetStringAsync("ping", CancellationToken.None));

            using HttpClient overSocket = RealKestrelBench.ClientOn(SocketHandler(path));
            Assert.Equal("pong", await overSocket.GetStringAsync("ping", CancellationToken.None));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(holdingOne, holdingTwo);
        }
    }

    [LinuxOnly]
    public async Task TheUnixSocketNoLongerNeedsTheTokenButTcpStillDoes()
    {
        // The Linux counterpart of the change: on the local channel the caller is identified by
        // its uid, so the token is not needed. On the network it stays mandatory.
        string path = ShortSocketPath();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options =>
            {
                options.Listen(System.Net.IPAddress.Loopback, 0);
                options.ListenUnixSocket(path);
            },
            middleware: app => app.UseObserverAccessControl(LocalChannelWindowsTests.Token));

        using HttpClient overSocket = RealKestrelBench.ClientOn(SocketHandler(path));
        Assert.Equal("pong", await overSocket.GetStringAsync("ping", CancellationToken.None));

        string tcp = bench.Addresses.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient overTcp = new() { BaseAddress = new Uri(tcp) };
        using HttpResponseMessage withoutToken = await overTcp.GetAsync("ping", CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, withoutToken.StatusCode);
    }

    [LinuxOnly]
    public async Task ALocalOnlyEndpointDoesNotExistForACallerFromTheNetwork()
    {
        string path = ShortSocketPath();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options =>
            {
                options.Listen(System.Net.IPAddress.Loopback, 0);
                options.ListenUnixSocket(path);
            },
            app => app.MapGet("/restricted", () => "secret").LocalOnly(),
            middleware: app => app.UseObserverAccessControl(LocalChannelWindowsTests.Token));

        string tcp = bench.Addresses.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient overTcp = new() { BaseAddress = new Uri(tcp) };
        overTcp.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", LocalChannelWindowsTests.TokenText);

        // With the RIGHT token, it is still a 404.
        using HttpResponseMessage fromNetwork = await overTcp.GetAsync("restricted", CancellationToken.None);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, fromNetwork.StatusCode);

        using HttpClient overSocket = RealKestrelBench.ClientOn(SocketHandler(path));
        Assert.Equal("secret", await overSocket.GetStringAsync("restricted", CancellationToken.None));
    }

    internal static string ShortSocketPath()
    {
        // The limit is 107 BYTES for the whole path, and a CI runner's temp directory can be
        // long: the path is checked, not assumed.
        string path = Path.Combine(
            Path.GetTempPath(),
            "o-" + Guid.NewGuid().ToString("N")[..8] + ".sock");

        Assert.Null(EndpointUrl.Problem("http://unix:" + path));

        return path;
    }

    internal static SocketsHttpHandler SocketHandler(string path) =>
        new()
        {
            ConnectCallback = async (_, cancel) =>
            {
                Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancel).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
}