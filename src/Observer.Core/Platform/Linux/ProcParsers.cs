using System.Text;
using System.Globalization;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Memory;
using Observer.Core.Units;

namespace Observer.Core.Platform.Linux;

/// <summary>
/// Parser della riga aggregata di /proc/stat. Funzione pura: riceve il contenuto gia'
/// letto e non apre file. Per questo gira identico anche sul runner Windows della CI.
/// </summary>
public static class ProcStatParser
{
    // user, nice, system, idle, iowait, irq, softirq, steal. I due campi successivi
    // (guest, guest_nice) sono GIA' conteggiati dentro user e nice: risommarli gonfierebbe
    // il denominatore e farebbe sottostimare la CPU.
    private const int MaxCountedFields = 8;

    // Servono almeno user, nice, system, idle: il /proc emulato di MSYS2 si ferma qui.
    private const int MinRequiredFields = 4;

    /// <summary>
    /// Estrae i tempi cumulativi dalla riga "cpu" aggregata. Restituisce false su qualunque
    /// input che non contenga una riga aggregata leggibile, senza mai lanciare: un'eccezione
    /// qui abbatterebbe il campionamento di tutte le metriche, non solo della CPU.
    /// </summary>
    public static bool TryParseAggregate(string content, out CpuTimes times)
    {
        times = default;

        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        foreach (ReadOnlySpan<char> line in content.AsSpan().EnumerateLines())
        {
            // "cpu " con lo spazio finale: esclude le righe per-core "cpu0", "cpu1", ...
            // La riga aggregata di spazi ne ha due, ed e' per questo che lo split scarta i vuoti.
            if (!line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                continue;
            }

            return TryParseFields(line, out times);
        }

        return false;
    }

    private static bool TryParseFields(ReadOnlySpan<char> line, out CpuTimes times)
    {
        times = default;

        long total = 0L;
        long idle = 0L;
        int parsedFields = 0;
        bool labelSkipped = false;

        foreach (Range segment in line.Split(' '))
        {
            ReadOnlySpan<char> token = line[segment].Trim();

            if (token.IsEmpty)
            {
                continue;
            }

            if (!labelSkipped)
            {
                labelSkipped = true;
                continue;
            }

            if (parsedFields >= MaxCountedFields)
            {
                break;
            }

            if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            {
                return false;
            }

            total += value;

            // idle (indice 3) + iowait (indice 4): entrambi sono tempo non lavorato.
            if (parsedFields is 3 or 4)
            {
                idle += value;
            }

            parsedFields++;
        }

        if (parsedFields < MinRequiredFields)
        {
            return false;
        }

        times = new CpuTimes(idle, total);
        return true;
    }
}

/// <summary>
/// Parser di /proc/meminfo. Funzione pura, come <see cref="ProcStatParser"/>.
/// </summary>
public static class ProcMeminfoParser
{
    /// <summary>
    /// Estrae totale, disponibile e swap. Restituisce false se manca MemTotal, perche' un
    /// totale a zero renderebbe ogni percentuale una divisione per zero: meglio dichiarare
    /// il campione non credibile che pubblicarne uno inventato.
    /// </summary>
    public static bool TryParse(string content, out MemoryReading reading)
    {
        reading = default;

        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        long? total = null;
        long? available = null;
        long free = 0L;
        long buffers = 0L;
        long cached = 0L;
        long reclaimable = 0L;
        long shmem = 0L;
        long swapTotal = 0L;
        long swapFree = 0L;

        foreach (ReadOnlySpan<char> line in content.AsSpan().EnumerateLines())
        {
            if (!TryParseEntry(line, out ReadOnlySpan<char> key, out long kibibytes))
            {
                continue;
            }

            if (key.Equals("MemTotal", StringComparison.Ordinal))
            {
                total = kibibytes;
            }
            else if (key.Equals("MemAvailable", StringComparison.Ordinal))
            {
                available = kibibytes;
            }
            else if (key.Equals("MemFree", StringComparison.Ordinal))
            {
                free = kibibytes;
            }
            else if (key.Equals("Buffers", StringComparison.Ordinal))
            {
                buffers = kibibytes;
            }
            else if (key.Equals("Cached", StringComparison.Ordinal))
            {
                cached = kibibytes;
            }
            else if (key.Equals("SReclaimable", StringComparison.Ordinal))
            {
                reclaimable = kibibytes;
            }
            else if (key.Equals("Shmem", StringComparison.Ordinal))
            {
                shmem = kibibytes;
            }
            else if (key.Equals("SwapTotal", StringComparison.Ordinal))
            {
                swapTotal = kibibytes;
            }
            else if (key.Equals("SwapFree", StringComparison.Ordinal))
            {
                swapFree = kibibytes;
            }
        }

        if (total is null)
        {
            return false;
        }

        // Kernel < 3.14 e /proc parziali non espongono MemAvailable. Si stima, ma lo si
        // DICHIARA: la UI deve poter scrivere "approssimato" invece di mentire.
        bool estimated = available is null;
        long availableKib = available ?? (free + buffers + cached + reclaimable - shmem);

        if (availableKib < 0L)
        {
            availableKib = 0L;
        }

        reading = new MemoryReading(
            ByteSize.FromKibibytes(total.Value),
            ByteSize.FromKibibytes(availableKib),
            ByteSize.FromKibibytes(swapTotal),
            ByteSize.FromKibibytes(swapFree),
            estimated);

        return true;
    }

    private static bool TryParseEntry(ReadOnlySpan<char> line, out ReadOnlySpan<char> key, out long kibibytes)
    {
        key = default;
        kibibytes = 0L;

        int colon = line.IndexOf(':');

        if (colon <= 0)
        {
            return false;
        }

        key = line[..colon].Trim();
        ReadOnlySpan<char> rest = line[(colon + 1)..].Trim();

        // "524288 kB" -> ci si ferma al primo spazio. L'unita' e' sempre etichettata "kB"
        // ma vale 1024 byte, ed e' per questo che si passa da ByteSize.FromKibibytes.
        int space = rest.IndexOf(' ');
        ReadOnlySpan<char> number = space < 0 ? rest : rest[..space];

        return long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out kibibytes);
    }
}

/// <summary>Lettura di /proc/self/mountinfo: quali filesystem sono innestati e dove.</summary>
public static class ProcMountInfoParser
{
    /// <summary>I punti di innesto dei filesystem ammessi, letti da /proc/self/mountinfo.</summary>
    /// <param name="content">Il contenuto del file.</param>
    /// <param name="ammessi">I tipi di filesystem da tenere.</param>
    /// <returns>I punti di innesto, senza ripetizioni, nell'ordine in cui compaiono.</returns>
    /// <remarks>
    /// Il formato ha un numero VARIABILE di campi: fra il sesto e il separatore <c>-</c> ci
    /// sono zero o piu' campi facoltativi, e il tipo di filesystem sta subito DOPO quel
    /// separatore. Contare i campi dall'inizio funziona finche' non c'e' un montaggio
    /// condiviso, e allora smette — quindi il tipo si cerca a partire dal separatore, che e'
    /// l'unico punto fermo della riga.
    /// <para>
    /// Lo stesso filesystem puo' essere innestato piu' volte (bind mount): senza togliere le
    /// ripetizioni comparirebbe piu' volte a schermo, ogni volta con gli stessi numeri, come
    /// se fossero dischi diversi.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> MountPoints(string content, ISet<string> ammessi)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(ammessi);

        List<string> punti = [];
        HashSet<string> visti = new(StringComparer.Ordinal);

        foreach (string riga in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pezzi = riga.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            int separatore = Array.LastIndexOf(pezzi, "-");

            // Serve il campo dopo il separatore (il tipo) e il quinto dall'inizio (il punto
            // di innesto): sotto queste misure la riga non e' un montaggio.
            if (separatore < 4 || separatore + 1 >= pezzi.Length)
            {
                continue;
            }

            if (!ammessi.Contains(pezzi[separatore + 1]))
            {
                continue;
            }

            string punto = Ottali(pezzi[4]);

            if (visti.Add(punto))
            {
                punti.Add(punto);
            }
        }

        return punti;
    }

    /// <summary>Rimette i caratteri che mountinfo scrive in ottale.</summary>
    /// <remarks>
    /// Uno spazio in un punto di innesto arriva come <c> </c>, e senza tradurlo il
    /// percorso non esiste: il volume sparirebbe dall'elenco senza un errore. Succede con i
    /// dischi esterni, che spesso hanno spazi nel nome.
    /// </remarks>
    private static string Ottali(string percorso)
    {
        if (!percorso.Contains('\\', StringComparison.Ordinal))
        {
            return percorso;
        }

        StringBuilder costruito = new(percorso.Length);

        for (int i = 0; i < percorso.Length; i++)
        {
            if (percorso[i] == '\\'
                && i + 3 < percorso.Length
                && int.TryParse(
                    percorso.AsSpan(i + 1, 3),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int ottale))
            {
                costruito.Append((char)Convert.ToInt32(ottale.ToString(CultureInfo.InvariantCulture), 8));
                i += 3;

                continue;
            }

            costruito.Append(percorso[i]);
        }

        return costruito.ToString();
    }
}

/// <summary>Una riga di /proc/diskstats, gia' convertita in byte e tempo.</summary>
/// <param name="Device">Nome del dispositivo, per esempio <c>sda</c> o <c>nvme0n1</c>.</param>
/// <param name="BytesRead">Byte letti dall'accensione.</param>
/// <param name="BytesWritten">Byte scritti dall'accensione.</param>
/// <param name="Busy">Tempo cumulativo con almeno una richiesta in corso.</param>
public readonly record struct DiskStatsLine(
    string Device,
    ulong BytesRead,
    ulong BytesWritten,
    TimeSpan Busy);

/// <summary>
/// Parser di /proc/diskstats. Funzione pura come gli altri: riceve il contenuto gia' letto,
/// quindi gira identico sul runner Windows.
/// </summary>
/// <remarks>
/// Due cose vanno sapute e nessuna delle due si indovina.
/// <para>
/// La prima: i settori qui sono <b>sempre</b> da 512 byte, per contratto documentato del
/// kernel, e non hanno niente a che vedere con la dimensione fisica del blocco. Un disco
/// "4K native" li conta comunque da 512, e chi moltiplicasse per la dimensione vera del
/// settore pubblicherebbe numeri otto volte piu' grandi del vero.
/// </para>
/// <para>
/// La seconda: il tempo di occupazione e' il campo 13 (<c>io_ticks</c>), che conta i
/// millisecondi in cui la coda NON era vuota. Non e' la somma dei millisecondi di lettura e
/// di scrittura, che sono i campi 7 e 11: quelli si sovrappongono, e sommarli ha gia' dato
/// 843% su una stessa finestra.
/// </para>
/// </remarks>
public static class ProcDiskStatsParser
{
    private const ulong ByteDelSettore = 512UL;

    // Indici contando da zero dopo lo split. I campi successivi — scarti e flush — esistono
    // solo sui kernel recenti, e non servono qui: per questo la riga si accetta a 14 campi.
    private const int IndiceNome = 2;
    private const int IndiceSettoriLetti = 5;
    private const int IndiceSettoriScritti = 9;
    private const int IndiceMillisecondiOccupato = 12;
    private const int CampiMinimi = 14;

    /// <summary>Legge le righe utilizzabili, saltando quelle che non lo sono.</summary>
    /// <param name="content">Contenuto di /proc/diskstats.</param>
    /// <returns>Una riga per dispositivo riconosciuto.</returns>
    public static IReadOnlyList<DiskStatsLine> Read(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        List<DiskStatsLine> righe = [];

        foreach (string riga in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pezzi = riga.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (pezzi.Length < CampiMinimi)
            {
                continue;
            }

            if (!Numero(pezzi[IndiceSettoriLetti], out ulong settoriLetti)
                || !Numero(pezzi[IndiceSettoriScritti], out ulong settoriScritti)
                || !Numero(pezzi[IndiceMillisecondiOccupato], out ulong millisecondi))
            {
                continue;
            }

            // Un prodotto che trabocca tornerebbe indietro in silenzio, e il collector lo
            // leggerebbe come un contatore andato all'indietro invece che come una riga da
            // buttare. Serve piu' spazio di quanto un disco vero possa avere scritto, ma
            // costa un confronto.
            if (settoriLetti > ulong.MaxValue / ByteDelSettore
                || settoriScritti > ulong.MaxValue / ByteDelSettore)
            {
                continue;
            }

            righe.Add(new DiskStatsLine(
                pezzi[IndiceNome],
                settoriLetti * ByteDelSettore,
                settoriScritti * ByteDelSettore,
                TimeSpan.FromMilliseconds(millisecondi)));
        }

        return righe;
    }

    private static bool Numero(string testo, out ulong valore) =>
        ulong.TryParse(testo, NumberStyles.None, CultureInfo.InvariantCulture, out valore);
}
