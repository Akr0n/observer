using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
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

    [OnLinuxFact]
    public async Task ASocketWhoseQueueIsFullIsReportedStuckAndTheProbeDoesNotWaitForIt()
    {
        // The socket sits in a folder the service account owns, so the account can leave one that
        // is listening and never accepts. A blocking connect to it waits for ever, and "doctor" is
        // the command run as root to find out why nothing works.
        string path = SocketPath();
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(0);
        List<Socket> waiting = [];

        try
        {
            Assert.True(FillTheQueue(path, waiting), "the accept queue could not be filled");

            string line = await Patience.Patiently(
                () => LocalChannelProbe.Probe("Observer", path, TimeSpan.FromMilliseconds(500)));

            Assert.StartsWith("STUCK", line, StringComparison.Ordinal);
        }
        finally
        {
            foreach (Socket client in waiting)
            {
                client.Dispose();
            }

            File.Delete(path);
        }
    }

    [OnLinuxFact]
    public async Task AnAnswerThatIsNotAnAnswerOfThisServiceIsNotReadWhole()
    {
        // What the service says is a few hundred bytes. Whatever is behind the socket can say
        // gigabytes, and it is read into memory before anyone looks at it.
        string path = SocketPath();
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);

        try
        {
            Task server = ServeOnce(listener, 8L * 1024 * 1024);

            LocalChannelAnswer answer = await Patience.Patiently(
                () => LocalChannelRequest.Post("credentials/reload", "unused", path, TimeSpan.FromSeconds(10)));

            Assert.True(answer.Silent);
            Assert.Equal(string.Empty, answer.Body);

            // Bounded: a server that never got its connection would wait for it for ever.
            await server.WaitAsync(Patience.Limit);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [OnLinuxFact]
    public async Task AnOrdinaryAnswerStillComesBackWhole()
    {
        // The other side of the cap: it is above anything the service really sends.
        string path = SocketPath();
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);

        try
        {
            Task server = ServeOnce(listener, 2000);

            LocalChannelAnswer answer = await Patience.Patiently(
                () => LocalChannelRequest.Post("credentials/reload", "unused", path, TimeSpan.FromSeconds(10)));

            Assert.False(answer.Silent);
            Assert.Equal(HttpStatusCode.OK, answer.Status);
            Assert.Equal(2000, answer.Body.Length);

            // Bounded: a server that never got its connection would wait for it for ever.
            await server.WaitAsync(Patience.Limit);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string SocketPath() =>
        Path.Combine(Path.GetTempPath(), "obs-" + Guid.NewGuid().ToString("N")[..10] + ".sock");

    /// <summary>Connects without accepting until one more connection would have to wait.</summary>
    private static bool FillTheQueue(string path, List<Socket> waiting)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { Blocking = false };

            try
            {
                client.Connect(new UnixDomainSocketEndPoint(path));
                waiting.Add(client);
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.WouldBlock)
            {
                client.Dispose();

                return true;
            }
        }

        return false;
    }

    /// <summary>Accepts one connection, reads its request and answers 200 with a body of this length.</summary>
    private static Task ServeOnce(Socket listener, long length) =>
        Task.Run(() =>
        {
            try
            {
                using Socket peer = listener.Accept();
                byte[] buffer = new byte[4096];
                StringBuilder request = new();

                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    int read = peer.Receive(buffer);

                    if (read == 0)
                    {
                        return;
                    }

                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                peer.Send(Encoding.ASCII.GetBytes(string.Create(
                    CultureInfo.InvariantCulture,
                    $"HTTP/1.1 200 OK\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n")));

                byte[] chunk = new byte[64 * 1024];
                Array.Fill(chunk, (byte)'a');

                for (long sent = 0; sent < length; sent += chunk.Length)
                {
                    peer.Send(chunk, 0, (int)Math.Min(chunk.Length, length - sent), SocketFlags.None);
                }
            }
            catch (SocketException)
            {
                // The client gave up on an answer that was too long, which is what is being tested.
            }
        });
}
