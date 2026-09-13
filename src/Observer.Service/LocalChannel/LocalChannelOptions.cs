namespace Observer.Service.LocalChannel;

/// <summary>Where the local channel sits on this machine.</summary>
/// <remarks>
/// The pipe name and the socket path are CONFIGURABLE, and that is not a convenience: an
/// endpoint that fails to bind brings down the WHOLE host, the TCP endpoint included. With
/// fixed values, starting the service by hand on a machine where the installed one is running
/// would no longer fail "on the port alone": it would not start at all.
/// </remarks>
public sealed class LocalChannelOptions
{
    /// <summary>The path of the section in configuration.</summary>
    public const string SectionName = "Observer:LocalChannel";

    /// <summary>Whether to open the local channel.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The name of the named pipe on Windows, without the prefix.</summary>
    public string PipeName { get; set; } = "Observer";

    /// <summary>The path of the unix socket on Linux.</summary>
    public string SocketPath { get; set; } = "/run/observer/observer.sock";

    /// <summary>It refuses to start with unusable values.</summary>
    /// <remarks>
    /// It validates BOTH values on every system, not only the one of the current platform:
    /// the configuration file is the same on Windows and on Linux, and a typo in the other
    /// system's field is to be found by whoever writes it, not by whoever gets there later.
    /// </remarks>
    public void Validate()
    {
        if (!Enabled)
        {
            // A machine that does not want the local channel must not invent valid values
            // just to be able to start.
            return;
        }

        if (string.IsNullOrWhiteSpace(PipeName))
        {
            throw new InvalidOperationException(
                $"{SectionName}:PipeName is empty. Give the pipe a name, or set Enabled to false.");
        }

        if (EndpointUrl.Problem("http://unix:" + SocketPath) is { } problem)
        {
            throw new InvalidOperationException($"{SectionName}:SocketPath can't be used. {problem}");
        }
    }
}