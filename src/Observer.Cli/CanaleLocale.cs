using System.IO.Pipes;
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

    /// <summary>Prova ad aprire il canale locale e racconta cosa succede.</summary>
    /// <param name="nomePipe">Il nome della pipe.</param>
    /// <param name="attesa">Quanto aspettare.</param>
    /// <returns>La frase da mostrare.</returns>
    public static string Prova(string nomePipe, TimeSpan attesa)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nomePipe);

        if (!OperatingSystem.IsWindows())
        {
            return "not checked - this build only probes the Windows named pipe.";
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
}