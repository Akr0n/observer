using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Principal;

namespace Observer.Cli;

/// <summary>Whether the service answers on the local channel.</summary>
/// <remarks>
/// It is the only genuinely useful answer a NON-administrator user can get, and that is why
/// <c>doctor</c> puts it next to the store diagnosis: the store's permissions cannot be read
/// from there, but if the pipe answers the service is running and the dashboard will be able
/// to connect.
/// </remarks>
public static class LocalChannelProbe
{
    /// <summary>The default pipe name, the same one as in appsettings.json.</summary>
    public const string DefaultPipeName = "Observer";

    /// <summary>The default path of the unix socket, the same one as in appsettings.json.</summary>
    public const string DefaultSocketPath = "/run/observer/observer.sock";

    /// <summary>Tries to open the default local channel and reports what happens.</summary>
    /// <param name="pipeName">The pipe name.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>The line to print.</returns>
    public static string Probe(string pipeName, TimeSpan timeout) =>
        Probe(pipeName, DefaultSocketPath, timeout);

    /// <summary>Tries to open a given local channel and reports what happens.</summary>
    /// <param name="pipeName">The pipe name, used on Windows.</param>
    /// <param name="socketPath">The socket path, used everywhere else.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>The line to print.</returns>
    /// <remarks>
    /// The path is a parameter and not a constant because otherwise the test of the
    /// "nobody answers" case would not be deterministic: on a Linux machine where Observer is
    /// really installed, the default socket exists and answers.
    /// </remarks>
    public static string Probe(string pipeName, string socketPath, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);

        if (!OperatingSystem.IsWindows())
        {
            return ProbeUnixSocket(socketPath);
        }

        // "." and not "localhost": localhost would go through SMB, and the service would classify
        // the connection as coming from the network instead of from this machine.
        using NamedPipeClientStream pipe = new(
            ".", pipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);

        try
        {
            pipe.Connect((int)timeout.TotalMilliseconds);

            return "ANSWERING - the dashboard can reach this machine without any token.";
        }
        catch (TimeoutException)
        {
            return
                "SILENT - nothing is listening on the local channel. The service may be stopped, " +
                "or the local channel may be disabled in appsettings.json.";
        }
        catch (UnauthorizedAccessException)
        {
            return
                "REFUSED - the pipe exists but this account can't open it. The dashboard would " +
                "not connect either. The service's pipe grants access only to interactive users.";
        }
        catch (IOException error)
        {
            return "ERROR - " + error.Message;
        }
    }

    /// <summary>The Linux counterpart: the local channel there is a unix socket.</summary>
    /// <param name="socketPath">The socket path.</param>
    /// <returns>The line to print.</returns>
    /// <remarks>
    /// Before, nothing was probed here and the answer was "not checked", that is, the most useful
    /// line of the doctor output stayed empty on precisely the system where the local channel does exist.
    /// A refusal here has a precise meaning, and a different one from Windows: the socket is created
    /// owned by the service's group, so "access denied" means the user is not in that group.
    /// </remarks>
    private static string ProbeUnixSocket(string socketPath)
    {
        using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));

            return "ANSWERING - the dashboard can reach this machine without any token.";
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AccessDenied)
        {
            return
                "REFUSED - the socket exists but this account can't open it. Add yourself to " +
                "the observer group (sudo usermod -aG observer $USER), then log out and back in.";
        }
        catch (SocketException)
        {
            return
                "SILENT - nothing is listening on " + socketPath + ". The service " +
                "may be stopped (systemctl status observer), or the local channel disabled.";
        }
    }
}