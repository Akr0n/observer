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
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    // 0660: connect(2) su AF_UNIX richiede il bit di SCRITTURA, non di lettura. Un modo che
    // concedesse al gruppo la sola lettura chiuderebbe fuori esattamente chi deve entrare.
    private const UnixFileMode SocketMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    /// <summary>Crea la directory del socket e le impone il modo giusto.</summary>
    /// <param name="path">Il path completo del socket.</param>
    public static void PreparePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directoryPath = Path.GetDirectoryName(path);

        if (string.IsNullOrEmpty(directoryPath))
        {
            return;
        }

        Directory.CreateDirectory(directoryPath, DirectoryMode);

        // La riga precedente NON applica il modo a una directory che esiste gia': misurato, e'
        // un no-op silenzioso. Senza questa seconda riga la protezione non esiste dal secondo
        // avvio in poi, ne' su una /run/observer creata da systemd con il suo 0755.
        File.SetUnixFileMode(directoryPath, DirectoryMode);
    }

    /// <summary>Cancella il file del socket SOLO se nessuno sta ascoltando.</summary>
    /// <param name="path">Il path del socket.</param>
    /// <param name="timeout">Quanto aspettare la risposta della probe.</param>
    /// <returns>Vero se il file e' stato rimosso.</returns>
    public static async Task<bool> RemoveStaleSocketAsync(string path, TimeSpan timeout)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using Socket probe = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using CancellationTokenSource deadline = new(timeout);

        try
        {
            // ConnectAsync con timeout e NON Connect(): contro un listener vivo con la coda di
            // accept piena, connect(2) su AF_UNIX non rifiuta, aspetta. Misurato: oltre venti
            // secondi appeso senza decidere ne' vivo ne' morto, che sotto systemd diventa un
            // timeout di avvio senza alcuna diagnosi.
            await probe.ConnectAsync(new UnixDomainSocketEndPoint(path), deadline.Token)
                .ConfigureAwait(false);

            // Qualcuno ha risposto: il socket e' vivo, e cancellarlo lo scippirebbe a
            // un'istanza sana.
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            File.Delete(path);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Scaduta la probe: non sappiamo se sia vivo. Nel dubbio NON si cancella.
            return false;
        }
    }

    /// <summary>Impone il modo del file del socket. Da chiamare DOPO l'avvio dell'host.</summary>
    /// <param name="path">Il path del socket.</param>
    public static void RestrictToOwner(string path) =>
        File.SetUnixFileMode(path, SocketMode);

    /// <summary>Registra la restrizione del modo per quando l'host sara' partito.</summary>
    /// <param name="lifetime">Il ciclo di lifetime dell'applicazione.</param>
    /// <param name="path">Il path del socket.</param>
    /// <remarks>
    /// Sta QUI e non in Program.cs perche' il corpo della lambda deve stare dentro una classe
    /// annotata: [SupportedOSPlatform] non copre il corpo di una lambda, e CA1416 con
    /// TreatWarningsAsErrors farebbe fallire la build su entrambi i runner.
    /// Prima dell'avvio il file del socket non esiste ancora, quindi il chmod non puo' stare
    /// accanto alla creazione della directory.
    /// </remarks>
    public static void RestrictAfterStart(IHostApplicationLifetime lifetime, string path)
    {
        ArgumentNullException.ThrowIfNull(lifetime);

        lifetime.ApplicationStarted.Register(() => RestrictToOwner(path));
    }
}