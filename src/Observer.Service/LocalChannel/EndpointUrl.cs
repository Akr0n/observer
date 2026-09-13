using System.Globalization;
using System.Text;

namespace Observer.Service.LocalChannel;

/// <summary>
/// Dice se un URL di endpoint di Kestrel e' utilizzabile, prima che Kestrel ci provi.
/// </summary>
/// <remarks>
/// Funzione PURA: nessuna I/O, nessun ambiente, quindi verificabile con una tabella su
/// entrambi i runner invece che avviando un host.
/// <para>
/// Esiste perche' i modi di sbagliare non sono equivalenti. Un path di socket relativo fa
/// fallire l'avvio, ed e' il caso buono. Un path in stile Windows dentro "http://unix:"
/// non fallisce affatto: Kestrel lega [::]:80 su TUTTE le interfacce, senza eccezione e senza
/// warning, e ci mette dietro la telemetria della macchina.
/// </para>
/// </remarks>
public static class EndpointUrl
{
    /// <summary>Byte utili nel path di un socket unix. <b>107, non 108.</b></summary>
    /// <remarks>
    /// La struct sockaddr_un ha 108 byte di sun_path, ma uno serve al terminatore. Il
    /// messaggio di .NET dice "must be between 1 and 108 characters, inclusive" ed e' falso su
    /// due punti: il limite vero e' 107, e il conteggio e' in BYTE UTF-8, non in caratteri.
    /// Verificato per bisezione: 107 accettato, 108 rifiutato.
    /// </remarks>
    public const int MaxUnixSocketPathBytes = 107;

    private const string UnixPrefix = "unix:";
    private const string PipePrefix = "pipe:";

    /// <summary>Il problema dell'URL, in inglese, oppure null se non ce ne sono.</summary>
    /// <param name="url">L'URL cosi' come sta in configurazione.</param>
    /// <returns>La frase da mostrare, oppure null se l'URL e' utilizzabile.</returns>
    public static string? Problem(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "An empty endpoint URL was configured. Remove the entry or give it a value.";
        }

        int separatorIndex = url.IndexOf("://", StringComparison.Ordinal);

        if (separatorIndex <= 0)
        {
            return UnusableUrl(url, "it has no scheme, so it isn't a URL at all");
        }

        string rest = url[(separatorIndex + 3)..];

        if (rest.StartsWith(UnixPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return UnixProblem(url, rest[UnixPrefix.Length..]);
        }

        if (rest.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return PipeProblem(url, rest[PipePrefix.Length..]);
        }

        return Uri.TryCreate(url, UriKind.Absolute, out _)
            ? null
            : UnusableUrl(url, "it isn't a well-formed absolute URL");
    }

    private static string? UnixProblem(string url, string path)
    {
        if (!path.StartsWith('/'))
        {
            // Il caso pericoloso: qui finisce anche un path in stile Windows. Senza questo
            // controllo Kestrel non protesta e apre la porta 80 su tutte le interfacce.
            return UnusableUrl(
                url,
                "the unix socket path must be absolute and start with '/'. A Windows-style " +
                "path here does NOT fail: Kestrel silently listens on port 80 on every " +
                "network interface instead");
        }

        int pathBytes = Encoding.UTF8.GetByteCount(path);

        return pathBytes > MaxUnixSocketPathBytes
            ? UnusableUrl(
                url,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the unix socket path is {pathBytes} bytes long and the limit is {MaxUnixSocketPathBytes}. The limit counts UTF-8 bytes, not characters"))
            : null;
    }

    private static string? PipeProblem(string url, string name)
    {
        if (!name.StartsWith('/'))
        {
            return UnusableUrl(url, "a named pipe endpoint must be written as http://pipe:/<name>");
        }

        return name.Length > 1
            ? null
            : UnusableUrl(url, "the pipe name is missing after http://pipe:/");
    }

    private static string UnusableUrl(string url, string reason) =>
        string.Create(CultureInfo.InvariantCulture, $"The endpoint URL \"{url}\" can't be used: {reason}.");
}