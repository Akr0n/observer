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
