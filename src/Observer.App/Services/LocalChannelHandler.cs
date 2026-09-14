using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Principal;

namespace Observer.App.Services;

/// <summary>Opens the local channel to the service on this machine.</summary>
/// <remarks>
/// No second protocol appears here: this transport carries the same HTTP/1.1 as the network
/// path, and the rest of the client does not even know which of the two it is using.
/// </remarks>
public static class LocalChannelHandler
{
    /// <summary>How long to wait for the connection to the local channel.</summary>
    /// <remarks>
    /// Short and SEPARATE from the request timeout, for a measured reason: over TCP a stopped
    /// service fails in milliseconds, but on a missing pipe the connect burns the whole
    /// request timeout. With the request's three seconds, the window would go from one update
    /// a second to one every four as soon as the service stops.
    /// </remarks>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>Builds the handler for this machine's local channel.</summary>
    /// <returns>The handler, to pass to an HTTP client.</returns>
    public static SocketsHttpHandler Create() =>
        new()
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                using CancellationTokenSource deadline = new(ConnectTimeout);
                using CancellationTokenSource linkedCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

                return OperatingSystem.IsWindows()
                    ? await OpenPipeAsync(linkedCancellation.Token).ConfigureAwait(false)
                    : await OpenSocketAsync(linkedCancellation.Token).ConfigureAwait(false);
            },
        };

    private static async Task<Stream> OpenPipeAsync(CancellationToken cancellationToken)
    {
        // The dot, and NOT "localhost". Measured: with "localhost" the connection goes through
        // SMB and the service classifies it as coming from the NETWORK, so it would demand the
        // token we do not have here. Only the dot is the local route.
        NamedPipeClientStream pipe = new(
            ".",
            ObserverEndpoint.LocalChannelName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            // Identification and not Impersonation: the service only needs to KNOW who we are,
            // it does not need to be able to act on our behalf. Grant the minimum that works.
            TokenImpersonationLevel.Identification);

        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);

        return pipe;
    }

    private static async Task<Stream> OpenSocketAsync(CancellationToken cancellationToken)
    {
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            await socket.ConnectAsync(
                new UnixDomainSocketEndPoint(ObserverEndpoint.LocalSocketPath), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new NetworkStream(socket, ownsSocket: true);
    }
}