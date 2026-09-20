using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Observer.Cli;

namespace Observer.Cli.Tests;

/// <summary>
/// The two questions the rotation asks the machine, and the direction each one fails in.
/// </summary>
/// <remarks>
/// The port probe decides which of two opposite sentences an operator is shown when the local
/// channel says nothing, so its failure direction is the thing worth pinning: answering "nothing
/// there" for a port that IS open turns "still accepts the old key" into "nothing is running
/// here", which is reassurance printed over an open door.
/// </remarks>
public class LocalChannelRequestTests
{
    [Fact]
    public void AnOpenPortIsSeen()
    {
        // Bound and LISTENING, without accepting: a service busy with other work looks like this,
        // and it must still read as present.
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.True(LocalChannelRequest.SomethingIsListeningOn(port, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            listener.Stop();
            listener.Dispose();
        }
    }

    [Fact]
    public void AClosedPortIsNotSeen()
    {
        // The port is taken and released, so it is one nothing is listening on at this instant -
        // more honest than picking a number and hoping it is free.
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        listener.Dispose();

        Assert.False(LocalChannelRequest.SomethingIsListeningOn(port, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ANonsensePortIsNotSeenAndDoesNotThrow()
    {
        // The port comes from the service's own options, which are validated - but this runs
        // during an incident, and a probe that throws would replace a verdict with a stack trace.
        Assert.False(LocalChannelRequest.SomethingIsListeningOn(0, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void AChannelNobodyIsServingComesBackSilentRatherThanThrowing()
    {
        // Silent is a VALUE here, not an exception, because the caller has a second question to
        // ask before it can say what silence means - and the answer to that question is what
        // decides between exit 0 and exit 1.
        string nowhere = "observer-absent-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        LocalChannelAnswer answer = LocalChannelRequest.Post(
            "credentials/reload",
            nowhere,
            Path.Combine(Path.GetTempPath(), nowhere + ".sock"),
            TimeSpan.FromSeconds(2));

        Assert.True(answer.Silent);
        Assert.Null(answer.Status);
        Assert.Equal(string.Empty, answer.Body);
    }
}
