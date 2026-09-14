using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Observer.Service.LocalChannel;

namespace Observer.Service.Tests;

/// <summary>Il canale locale su Linux.</summary>
/// <remarks>
/// L'attributo sulla CLASSE e non sui singoli metodi: CA1416 guarda il sito di chiamata, e
/// [SoloSuLinux] e' un salto a RUNTIME che l'analyzer non vede. Tutti i test qui dentro sono
/// comunque Linux, quindi annotare la classe e' la forma piu' onesta.
/// </remarks>
[Collection(ProcessEnvironment.Name)]
[SupportedOSPlatform("linux")]
public class LocalChannelLinuxTests
{
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
        // Contro l'idea diffusa che su Linux il file sopravviva sempre: .NET fa l'unlink
        // esplicito, perche' UnixDomainSocketEndPoint porta un boundFileName. La bonifica serve
        // SOLO dopo una morte violenta.
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
        // "Se il file esiste, cancellalo" permette a una seconda istanza di scippare il socket
        // a una prima istanza sana. La bonifica deve sondare, e sondare con un TIMEOUT.
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

        // Simula la morte violenta: il file resta a terra senza nessuno in ascolto.
        await File.WriteAllTextAsync(path, string.Empty, CancellationToken.None);

        Assert.True(await LinuxUnixSocket.RemoveStaleSocketAsync(path, TimeSpan.FromSeconds(2)));
        Assert.False(File.Exists(path));
    }

    [LinuxOnly]
    public void TheDirectoryModeIsSetEvenWhenTheDirectoryAlreadyExists()
    {
        // Directory.CreateDirectory(percorso, modo) NON applica il modo a una directory che
        // esiste gia': misurato, e' un no-op silenzioso. Quindi la protezione non esisterebbe
        // dal secondo avvio in poi, ne' su una /run/observer creata da systemd col suo 0755.
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
            app => app.MapGet("/chi", (HttpContext context) =>
            {
                CallerOrigin origin = LocalCaller.Classify(context);
                return origin.Kind + "|" + (origin.Sid ?? "(nessuno)");
            }));

        using HttpClient client = RealKestrelBench.ClientOn(SocketHandler(path));
        string outcome = await client.GetStringAsync("chi", CancellationToken.None);

        // Su un socket unix il chiamante e' SEMPRE sulla stessa macchina: non esiste la via SMB
        // che c'e' su Windows. L'unica domanda e' se l'uid sia leggibile.
        string[] parts = outcome.Split('|');

        Assert.Equal(nameof(CallerKind.LocalIdentified), parts[0]);
        Assert.True(uint.TryParse(parts[1], out _), "uid non numerico: " + parts[1]);
    }

    [LinuxOnly]
    public async Task TheUnixSocketNoLongerNeedsTheTokenButTcpStillDoes()
    {
        // La controparte Linux del cambiamento: sul canale locale il chiamante e' identificato
        // dal suo uid, quindi il token non serve. Sulla rete resta obbligatorio.
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
            app => app.MapGet("/riservato", () => "segreto").LocalOnly(),
            middleware: app => app.UseObserverAccessControl(LocalChannelWindowsTests.Token));

        string tcp = bench.Addresses.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient overTcp = new() { BaseAddress = new Uri(tcp) };
        overTcp.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", LocalChannelWindowsTests.TestToken);

        // Col token GIUSTO, e comunque 404.
        using HttpResponseMessage fromNetwork = await overTcp.GetAsync("riservato", CancellationToken.None);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, fromNetwork.StatusCode);

        using HttpClient overSocket = RealKestrelBench.ClientOn(SocketHandler(path));
        Assert.Equal("segreto", await overSocket.GetStringAsync("riservato", CancellationToken.None));
    }

    internal static string ShortSocketPath()
    {
        // Il limite e' 107 BYTE per l'intero percorso, e il temp di un runner di CI puo' essere
        // lungo: il percorso viene verificato, non sperato.
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