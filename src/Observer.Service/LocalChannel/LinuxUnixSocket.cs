using System.Net.Sockets;
using System.Runtime.Versioning;

namespace Observer.Service.LocalChannel;

/// <summary>Preparazione e bonifica del socket unix.</summary>
/// <remarks>
/// L'ordine e' vincolato: convalida, directory e bonifica PRIMA di costruire l'host; il modo
/// del file del socket DOPO l'avvio, perche' prima quel file non esiste.
/// </remarks>
[SupportedOSPlatform("linux")]
public static class LinuxUnixSocket
{
    // 0750: il proprietario entra e amministra, il gruppo attraversa. Non 0700, che chiuderebbe
    // fuori la GUI; non 0755, che aprirebbe a chiunque abbia un account sulla macchina.
    private const UnixFileMode ModoDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    // 0660: connect(2) su AF_UNIX richiede il bit di SCRITTURA, non di lettura. Un modo che
    // concedesse al gruppo la sola lettura chiuderebbe fuori esattamente chi deve entrare.
    private const UnixFileMode ModoSocket =
        UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    /// <summary>Crea la directory del socket e le impone il modo giusto.</summary>
    /// <param name="percorso">Il percorso completo del socket.</param>
    public static void PreparaPercorso(string percorso)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(percorso);

        string? cartella = Path.GetDirectoryName(percorso);

        if (string.IsNullOrEmpty(cartella))
        {
            return;
        }

        Directory.CreateDirectory(cartella, ModoDirectory);

        // La riga precedente NON applica il modo a una directory che esiste gia': misurato, e'
        // un no-op silenzioso. Senza questa seconda riga la protezione non esiste dal secondo
        // avvio in poi, ne' su una /run/observer creata da systemd con il suo 0755.
        File.SetUnixFileMode(cartella, ModoDirectory);
    }

    /// <summary>Cancella il file del socket SOLO se nessuno sta ascoltando.</summary>
    /// <param name="percorso">Il percorso del socket.</param>
    /// <param name="attesa">Quanto aspettare la risposta della sonda.</param>
    /// <returns>Vero se il file e' stato rimosso.</returns>
    public static async Task<bool> BonificaSocketOrfanoAsync(string percorso, TimeSpan attesa)
    {
        if (!File.Exists(percorso))
        {
            return false;
        }

        using Socket sonda = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using CancellationTokenSource scadenza = new(attesa);

        try
        {
            // ConnectAsync con timeout e NON Connect(): contro un listener vivo con la coda di
            // accept piena, connect(2) su AF_UNIX non rifiuta, aspetta. Misurato: oltre venti
            // secondi appeso senza decidere ne' vivo ne' morto, che sotto systemd diventa un
            // timeout di avvio senza alcuna diagnosi.
            await sonda.ConnectAsync(new UnixDomainSocketEndPoint(percorso), scadenza.Token)
                .ConfigureAwait(false);

            // Qualcuno ha risposto: il socket e' vivo, e cancellarlo lo scippirebbe a
            // un'istanza sana.
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            File.Delete(percorso);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Scaduta la sonda: non sappiamo se sia vivo. Nel dubbio NON si cancella.
            return false;
        }
    }

    /// <summary>Impone il modo del file del socket. Da chiamare DOPO l'avvio dell'host.</summary>
    /// <param name="percorso">Il percorso del socket.</param>
    public static void RestringiAlProprietario(string percorso) =>
        File.SetUnixFileMode(percorso, ModoSocket);
}