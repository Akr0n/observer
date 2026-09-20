using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;

namespace Observer.Cli;

/// <summary>What the local channel answered, or why it did not.</summary>
/// <param name="Status">The HTTP status, or null when nothing was reached.</param>
/// <param name="Body">The body, empty when there is none.</param>
/// <param name="Silent">True when nothing is listening on the local channel at all.</param>
public sealed record LocalChannelAnswer(HttpStatusCode? Status, string Body, bool Silent);

/// <summary>
/// One request to the service on THIS machine, through the channel that needs no token.
/// </summary>
/// <remarks>
/// It is deliberately separate from <see cref="LocalChannelProbe"/>, which only asks whether
/// anything is there: this one carries a request and brings an answer back, and the two failure
/// vocabularies are different. A probe that cannot connect is a line in <c>doctor</c>; a request
/// that cannot connect is the difference between "the leaked key is dead" and "it is still being
/// accepted and nobody has told you".
/// </remarks>
public static class LocalChannelRequest
{
    /// <summary>Sends a POST with no body and reads what comes back.</summary>
    /// <param name="route">The route, without a leading slash.</param>
    /// <param name="pipeName">The pipe name, used on Windows.</param>
    /// <param name="socketPath">The socket path, used elsewhere.</param>
    /// <param name="timeout">How long to wait, for the connection and for the answer.</param>
    /// <returns>The answer, or a silent one.</returns>
    public static LocalChannelAnswer Post(string route, string pipeName, string socketPath, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);

        using SocketsHttpHandler handler = new()
        {
            ConnectCallback = (_, cancel) => Connect(pipeName, socketPath, timeout, cancel),
        };

        using HttpClient client = new(handler, disposeHandler: false)
        {
            // The host is arbitrary and never resolved - it reaches only the Host header. A name
            // under .invalid makes it explicit that it must not be looked up.
            BaseAddress = new Uri("http://local-channel.invalid/"),
            Timeout = timeout,
        };

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, route);
            using HttpResponseMessage response = client.Send(request);

            using StreamReader reader = new(response.Content.ReadAsStream());

            return new LocalChannelAnswer(response.StatusCode, reader.ReadToEnd(), Silent: false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            // Nothing distinguishable came back. Whether that means the service is stopped or
            // merely unreachable on this channel is NOT decided here: the caller has another
            // question to ask before it can say, and answering it from this side would be a
            // guess dressed as a verdict.
            return new LocalChannelAnswer(null, string.Empty, Silent: true);
        }
    }

    /// <summary>Whether something is listening on the machine's HTTPS port.</summary>
    /// <param name="port">The port from the service's configuration.</param>
    /// <param name="timeout">How long to wait for the connection.</param>
    /// <returns>True if the connection was accepted.</returns>
    /// <remarks>
    /// The cheapest check that answers the one question a silent local channel leaves open: is
    /// there a service running here at all? No TLS, no certificate, no HTTP - the answer only
    /// chooses which sentence to print, so a false positive costs a needless "restart it" and
    /// nothing more. It exists because the two causes of a silent local channel mean opposite
    /// things: a stopped service accepts nothing, while a service with the local channel
    /// switched off is still answering the network with the key that has just leaked.
    /// </remarks>
    public static bool SomethingIsListeningOn(int port, TimeSpan timeout)
    {
        using TcpClient probe = new();

        try
        {
            return probe.ConnectAsync(IPAddress.Loopback, port).Wait(timeout);
        }
        catch (Exception error) when (error is SocketException or AggregateException)
        {
            return false;
        }
    }

    private static ValueTask<Stream> Connect(
        string pipeName, string socketPath, TimeSpan timeout, CancellationToken cancel) =>
        OperatingSystem.IsWindows()
            ? ConnectPipe(pipeName, timeout, cancel)
            : ConnectSocket(socketPath, cancel);

    private static async ValueTask<Stream> ConnectPipe(
        string pipeName, TimeSpan timeout, CancellationToken cancel)
    {
        // "." and NOT "localhost": localhost reaches the same pipe over SMB, and the service
        // classifies that as a caller from the network - which on a local-only endpoint means a
        // 404, indistinguishable from a service too old to have the route at all.
        //
        // Identification and not Impersonation: it is the least the service needs in order to
        // read who is calling and whether their token carries the administrators group, which is
        // what the reload asks for. An impersonation token would let the service act AS the
        // caller, and nothing here needs that.
        NamedPipeClientStream pipe = new(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);

        await pipe.ConnectAsync(timeout, cancel).ConfigureAwait(false);

        return pipe;
    }

    private static async ValueTask<Stream> ConnectSocket(string socketPath, CancellationToken cancel)
    {
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancel).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new NetworkStream(socket, ownsSocket: true);
    }
}
