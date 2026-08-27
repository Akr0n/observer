using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Principal;

namespace Observer.Cli;

/// <summary>Se il servizio risponde sul canale locale.</summary>
/// <remarks>
/// E' l'unica risposta davvero utile che un utente NON amministratore possa ottenere, e per
/// questo <c>doctor</c> la mette accanto alla diagnosi del deposito: i permessi del deposito da
/// li' non si leggono, ma se la pipe risponde il servizio sta funzionando e la dashboard
/// entrera'.
/// </remarks>
public static class CanaleLocale
{
    /// <summary>Il nome predefinito della pipe, uguale a quello in appsettings.json.</summary>
    public const string NomePredefinito = "Observer";

    /// <summary>Il percorso predefinito del socket unix, uguale a quello in appsettings.json.</summary>
    public const string PercorsoSocketPredefinito = "/run/observer/observer.sock";

    /// <summary>Prova ad aprire il canale locale predefinito e racconta cosa succede.</summary>
    /// <param name="nomePipe">Il nome della pipe.</param>
    /// <param name="attesa">Quanto aspettare.</param>
    /// <returns>La frase da mostrare.</returns>
    public static string Prova(string nomePipe, TimeSpan attesa) =>
        Prova(nomePipe, PercorsoSocketPredefinito, attesa);

    /// <summary>Prova ad aprire un canale locale indicato e racconta cosa succede.</summary>
    /// <param name="nomePipe">Il nome della pipe, usato su Windows.</param>
    /// <param name="percorsoSocket">Il percorso del socket, usato altrove.</param>
    /// <param name="attesa">Quanto aspettare.</param>
    /// <returns>La frase da mostrare.</returns>
    /// <remarks>
    /// Il percorso e' un parametro e non una costante perche' altrimenti il test del caso
    /// "nessuno risponde" non sarebbe deterministico: su una macchina Linux dove Observer e'
    /// davvero installato, il socket predefinito esiste e risponde.
    /// </remarks>
    public static string Prova(string nomePipe, string percorsoSocket, TimeSpan attesa)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nomePipe);
        ArgumentException.ThrowIfNullOrWhiteSpace(percorsoSocket);

        if (!OperatingSystem.IsWindows())
        {
            return ProvaSocket(percorsoSocket);
        }

        // "." e non "localhost": localhost passerebbe da SMB, e il servizio classificherebbe la
        // connessione come proveniente dalla rete invece che da questa macchina.
        using NamedPipeClientStream tubo = new(
            ".", nomePipe, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);

        try
        {
            tubo.Connect((int)attesa.TotalMilliseconds);

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
                "not connect either. The service's pipe permissions grant only interactive users.";
        }
        catch (IOException errore)
        {
            return "ERROR - " + errore.Message;
        }
    }

    /// <summary>La controparte Linux: il canale locale li' e' un socket unix.</summary>
    /// <param name="percorso">Il percorso del socket.</param>
    /// <returns>La frase da mostrare.</returns>
    /// <remarks>
    /// Prima qui non si sondava niente e si rispondeva "not checked", cioe' la riga piu' utile
    /// del verbo restava vuota proprio sul sistema dove il canale locale esiste eccome.
    /// Il rifiuto qui ha un significato preciso e diverso da Windows: il socket nasce con il
    /// gruppo del servizio, quindi "accesso negato" vuol dire che l'utente non e' in quel gruppo.
    /// </remarks>
    private static string ProvaSocket(string percorso)
    {
        using Socket presa = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            presa.Connect(new UnixDomainSocketEndPoint(percorso));

            return "ANSWERING - the dashboard can reach this machine without any token.";
        }
        catch (SocketException errore) when (errore.SocketErrorCode == SocketError.AccessDenied)
        {
            return
                "REFUSED - the socket exists but this account can't open it. Add yourself to " +
                "the observer group (sudo usermod -aG observer $USER), then log out and back in.";
        }
        catch (SocketException)
        {
            return
                "SILENT - nothing is listening on " + percorso + ". The service " +
                "may be stopped (systemctl status observer), or the local channel disabled.";
        }
    }
}