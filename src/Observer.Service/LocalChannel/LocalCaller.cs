using System.Net.Sockets;
using Microsoft.AspNetCore.Connections.Features;

namespace Observer.Service.LocalChannel;

/// <summary>Chi ha mandato questa richiesta, visto da codice che non sa su quale sistema gira.</summary>
public static class LocalCaller
{
    /// <summary>Classify il chiamante della richiesta in corso.</summary>
    /// <param name="context">La richiesta in corso.</param>
    /// <returns>L'origine del chiamante.</returns>
    public static CallerOrigin Classify(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Le due feature sono mutuamente esclusive e affidabili come INSTRADAMENTO: misurato,
        // sulla pipe c'e' solo la prima e sul socket unix solo la seconda. Ma dicono da DOVE e'
        // entrata la richiesta, NON se il chiamante sia ammesso. Confondere le due cose e' il
        // difetto documentato nella specifica, e non va scritto qui.
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