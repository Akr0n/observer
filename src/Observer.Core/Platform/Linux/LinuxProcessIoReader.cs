using System.Globalization;
using Observer.Core.Processes;

namespace Observer.Core.Platform.Linux;

/// <summary>
/// Adattatore Linux del contatore di I/O per processo, sopra <c>/proc/PID/io</c>.
/// </summary>
/// <remarks>
/// Si sommano <c>rchar</c> e <c>wchar</c>, NON <c>read_bytes</c> e <c>write_bytes</c>, e la
/// scelta va spiegata perche' la seconda coppia sembra quella giusta: conta i byte arrivati
/// davvero al disco, la prima conta ogni lettura e scrittura chiesta dal processo, cache
/// compresa. Ma su Windows l'unico contatore per processo e' del secondo tipo — <c>IO_COUNTERS</c>
/// conta le chiamate, non i settori — e un elenco che dicesse "byte su disco" su una macchina e
/// "byte chiesti" sull'altra confronterebbe due cose diverse sotto lo stesso titolo. Il pannello
/// si chiama "I/O", e dice la stessa cosa dappertutto.
/// <para>
/// <c>/proc/PID/io</c> si legge solo con il permesso di <i>ptrace</i> su quel processo: i propri
/// si', quelli di un altro utente no, a meno di <c>CAP_SYS_PTRACE</c>. Il servizio gira come
/// utente <c>observer</c> e quella capability non ce l'ha, di proposito — permetterebbe di
/// leggere la memoria di qualunque processo — quindi su Linux la colonna resta un trattino per
/// tutto cio' che non e' suo. E' una limitazione dichiarata, non un guasto: chi la vuole
/// togliere aggiunge <c>AmbientCapabilities=CAP_SYS_PTRACE</c> alla unit, sapendo cosa concede.
/// </para>
/// </remarks>
public sealed class LinuxProcessIoReader : IProcessIoReader
{
    private readonly IFileTextReader reader;

    /// <summary>Crea l'adattatore sopra il lettore indicato.</summary>
    /// <param name="reader">Da dove leggere i file di sistema.</param>
    public LinuxProcessIoReader(IFileTextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        this.reader = reader;
    }

    /// <inheritdoc />
    public bool TryRead(int pid, out ulong bytes)
    {
        bytes = 0;

        string percorso = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/io";

        return reader.TryReadAllText(percorso, out string contenuto) && TryParse(contenuto, out bytes);
    }

    /// <summary>Legge <c>rchar</c> e <c>wchar</c> dal contenuto di <c>/proc/PID/io</c>.</summary>
    /// <param name="contenuto">Il file, una coppia <c>chiave: valore</c> per riga.</param>
    /// <param name="bytes">La somma dei due.</param>
    /// <returns>False se uno dei due manca o non e' un intero.</returns>
    public static bool TryParse(string contenuto, out ulong bytes)
    {
        bytes = 0;

        ulong? letti = null;
        ulong? scritti = null;

        foreach (ReadOnlySpan<char> riga in contenuto.AsSpan().EnumerateLines())
        {
            if (Valore(riga, "rchar:", out ulong valore))
            {
                letti = valore;
            }
            else if (Valore(riga, "wchar:", out valore))
            {
                scritti = valore;
            }
        }

        if (letti is not { } r || scritti is not { } w)
        {
            return false;
        }

        ulong somma = r + w;

        // Un giro completo dei 64 bit non e' un totale: e' un totale sbagliato.
        if (somma < r)
        {
            return false;
        }

        bytes = somma;

        return true;
    }

    private static bool Valore(ReadOnlySpan<char> riga, string chiave, out ulong valore)
    {
        valore = 0;

        return riga.StartsWith(chiave, StringComparison.Ordinal)
            && ulong.TryParse(
                riga[chiave.Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out valore);
    }
}