using System.Net.Sockets;
using Microsoft.AspNetCore.Connections.Features;

namespace Observer.Service.LocalChannel;

/// <summary>Who sent this request, seen from code that does not know which system it runs on.</summary>
public static class LocalCaller
{
    /// <summary>Classifies the caller of the request in progress.</summary>
    /// <param name="context">The request in progress.</param>
    /// <returns>The caller's origin.</returns>
    public static CallerOrigin Classify(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The two features are mutually exclusive and reliable as ROUTING: measured, on the pipe
        // there is only the first and on the unix socket only the second. But they say from WHERE
        // the request came in, NOT whether the caller is admitted. Confusing the two is the
        // defect documented in the specification, and it must not be written here.
        if (OperatingSystem.IsWindows()
            && context.Features.Get<IConnectionNamedPipeFeature>() is { } pipe)
        {
            return WindowsCallerIdentity.Classify(pipe.NamedPipe);
        }

        if (OperatingSystem.IsLinux()
            && context.Features.Get<IConnectionSocketFeature>() is { } socket
            && socket.Socket.AddressFamily == AddressFamily.Unix)
        {
            return LinuxCallerIdentity.Classify(socket.Socket);
        }

        return new CallerOrigin(CallerKind.FromNetwork, null, "the request arrived over TCP");
    }
}