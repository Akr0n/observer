using System.Net;
using System.Net.Sockets;
using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// A real refusal, on a real socket.
/// </summary>
/// <remarks>
/// <see cref="TransportFailureTests"/> tests the RULE by building the exceptions by hand; this
/// one tests the only thing that cannot be known on paper, namely that .NET really delivers
/// what that rule expects, and that it gets there within the time allowed.
/// <para>
/// The second half is the one that was needed. Measured on Windows with .NET 10, six runs per
/// address: a refusal costs 2018-2104 ms, on loopback just as on the network address. A
/// dual-stack name pays it twice, because the addresses are tried one after another, and with
/// no cap it costs 4035-4121 ms. With the 3 seconds of budget the client used to have,
/// "localhost" on a closed port never got as far as saying "refused": it timed out first, and
/// the window advised checking the firewall for a service that was simply down. A test built on
/// the exceptions alone would have stayed green the whole time.
/// <para>
/// It is also the test that failed the first budget. Six seconds passed on an idle machine and
/// fell over on a busy one, because less than two seconds of margin on 4.1 is not a margin.
/// Eight gives nearly twice the measured cost.
/// </para>
/// </para>
/// </remarks>
public class ConnectionRefusedTests
{
    [Fact]
    public async Task AClosedPortOnALiteralAddressComesBackAsARefusal()
    {
        using MetricsClient client = new(EndpointFor($"http://127.0.0.1:{ClosedPort()}/"));

        SnapshotFetch fetch = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.ConnectionRefused, fetch.Outcome);
    }

    [Fact]
    public async Task AClosedPortOnADualStackNameComesBackAsARefusalAndDoesNotBlameTheFirewall()
    {
        // The case the previous budget did not cover. If one day somebody lowered
        // RequestTimeout again, this test would go red — and it is the only place where that
        // number is tied to what it protects.
        using MetricsClient client = new(EndpointFor($"http://localhost:{ClosedPort()}/"));

        SnapshotFetch fetch = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.ConnectionRefused, fetch.Outcome);

        // The real damage was not the label: it was the advice. Sending someone hunting for a
        // firewall while the service is down costs them an afternoon.
        Assert.DoesNotContain("dropping the packets", fetch.Problem, StringComparison.Ordinal);
    }

    private static ObserverEndpoint EndpointFor(string address) =>
        ObserverEndpoint.Remote(new Uri(address), "the-token", "from the test");

    /// <summary>A port that is guaranteed to have nobody listening on it.</summary>
    /// <remarks>
    /// The system is asked to open an ephemeral port and it is closed straight away: it is the
    /// only way to get a free number without picking one at random and hoping.
    /// </remarks>
    private static int ClosedPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}