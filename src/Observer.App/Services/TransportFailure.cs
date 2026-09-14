using System.Net.Sockets;

namespace Observer.App.Services;

/// <summary>
/// Why the transport failed, when it failed.
/// </summary>
/// <remarks>
/// A pure function, and it stands on its own for the same reason as
/// <see cref="StatusEscalation"/>: the rule is tested by building the exceptions by hand,
/// without opening a socket. What CANNOT be known on paper is whether .NET really delivers what
/// this table expects, and that is why there is also a test on a real transport.
/// <para>
/// The distinction this type exists to make: a <b>refused</b> connection is the most
/// informative answer a fault can give — the packet arrived, the machine answered, and all that
/// is missing is somebody listening on that port. A <b>timeout</b> says the opposite: nobody
/// answered. The two fixes are nothing alike.
/// </para>
/// </remarks>
public static class TransportFailure
{
    // The exception chain has no guaranteed depth: .NET wraps the SocketException inside an
    // IOException and that inside an HttpRequestException, but that is an implementation
    // detail. Walk down until it is found, with a limit so a chain that loops back on itself
    // cannot leave us hanging.
    private const int MaxDepth = 8;

    /// <summary>Translates a transport fault into the outcome to show.</summary>
    /// <param name="error">The exception that came from the HTTP client.</param>
    /// <returns>The matching outcome.</returns>
    public static ServiceOutcome Classify(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        // The client timeout never goes through the socket: it is HttpClient cancelling its own
        // request, and what you see is a cancellation. Looking only for SocketError.TimedOut
        // would never find the most frequent case of all.
        if (error is OperationCanceledException)
        {
            return ServiceOutcome.TimedOut;
        }

        return FindSocketError(error) switch
        {
            SocketError.ConnectionRefused => ServiceOutcome.ConnectionRefused,
            SocketError.TimedOut => ServiceOutcome.TimedOut,

            // Everything else stays generic on purpose. A name that does not resolve, an
            // unreachable network and a failed TLS handshake are different faults, and
            // inventing a title for each one that nobody knows how to word well would be
            // worse than an honestly generic title.
            _ => ServiceOutcome.Unreachable,
        };
    }

    private static SocketError? FindSocketError(Exception error)
    {
        Exception? current = error;

        for (int depth = 0; current is not null && depth < MaxDepth; depth++)
        {
            if (current is SocketException socket)
            {
                return socket.SocketErrorCode;
            }

            current = current.InnerException;
        }

        return null;
    }
}