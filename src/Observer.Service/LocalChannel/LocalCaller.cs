using System.Net.Sockets;
using Microsoft.AspNetCore.Connections.Features;

namespace Observer.Service.LocalChannel;

/// <summary>Chi ha mandato questa richiesta, visto da codice che non sa su quale sistema gira.</summary>
public static class LocalCaller
{
    /// <summary>Classifica il chiamante della richiesta in corso.</summary>
    /// <param name="contesto">La richiesta in corso.</param>
    /// <returns>L'origine del chiamante.</returns>
    public static CallerOrigin Classifica(HttpContext contesto)
    {
        ArgumentNullException.ThrowIfNull(contesto);

        // Le due feature sono mutuamente esclusive e affidabili come INSTRADAMENTO: misurato,
        // sulla pipe c'e' solo la prima e sul socket unix solo la seconda. Ma dicono da DOVE e'
        // entrata la richiesta, NON se il chiamante sia ammesso. Confondere le due cose e' il
        // difetto documentato nella specifica, e non va scritto qui.
        if (OperatingSystem.IsWindows()
            && contesto.Features.Get<IConnectionNamedPipeFeature>() is { } pipe)
        {
            return WindowsCallerIdentity.Classifica(pipe.NamedPipe);
        }

        if (OperatingSystem.IsLinux()
            && contesto.Features.Get<IConnectionSocketFeature>() is { } presa
            && presa.Socket.AddressFamily == AddressFamily.Unix)
        {
            return LinuxCallerIdentity.Classifica(presa.Socket);
        }

        return new CallerOrigin(CallerKind.ArrivatoDallaRete, null, "the request arrived over TCP");
    }
}