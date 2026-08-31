using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Disk;
using Observer.Core.Metrics.Memory;
using Observer.Core.Units;

namespace Observer.Core.Platform.Linux;

/// <summary>
/// Adattatore /proc/stat. Contiene SOLO la lettura grezza: l'interpretazione sta in
/// <see cref="ProcStatParser"/> e il calcolo in <see cref="CpuUsage"/>. Non interroga il
/// sistema operativo, quindi si prova interamente dal runner Windows iniettando un lettore
/// finto — ed e' il comportamento del provider, non solo del parser, a risultare coperto.
/// </summary>
public sealed class LinuxCpuTimesProvider : ICpuTimesProvider
{
    /// <summary>Percorso del file dei contatori CPU.</summary>
    public const string StatPath = "/proc/stat";

    private readonly IFileTextReader reader;

    /// <summary>Crea l'adattatore sopra il lettore indicato.</summary>
    public LinuxCpuTimesProvider(IFileTextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        this.reader = reader;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sempre true: su Linux /proc/stat esiste per contratto del kernel. Se non e'
    /// leggibile ora e' un guasto momentaneo (Unavailable), non una mancanza di supporto.
    /// </remarks>
    public bool IsSupported => true;

    /// <inheritdoc />
    public string? UnsupportedReason => null;

    /// <inheritdoc />
    public bool TryRead(out CpuTimes times)
    {
        if (!reader.TryReadAllText(StatPath, out string content))
        {
            times = default;
            return false;
        }

        return ProcStatParser.TryParseAggregate(content, out times);
    }
}

/// <summary>Adattatore /proc/meminfo, con le stesse proprieta' di <see cref="LinuxCpuTimesProvider"/>.</summary>
public sealed class LinuxMemoryReadingProvider : IMemoryReadingProvider
{
    /// <summary>Percorso del file dei valori di memoria.</summary>
    public const string MeminfoPath = "/proc/meminfo";

    private readonly IFileTextReader reader;

    /// <summary>Crea l'adattatore sopra il lettore indicato.</summary>
    public LinuxMemoryReadingProvider(IFileTextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        this.reader = reader;
    }

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public string? UnsupportedReason => null;

    /// <inheritdoc />
    public bool TryRead(out MemoryReading value)
    {
        if (!reader.TryReadAllText(MeminfoPath, out string content))
        {
            value = default;
            return false;
        }

        return ProcMeminfoParser.TryParse(content, out value);
    }
}


/// <summary>Adattatore Linux dello spazio sui volumi, via /proc/self/mountinfo.</summary>
/// <remarks>
/// <b>NON si enumera con DriveInfo.GetDrives().</b> Su Linux restituisce ogni singolo
/// montaggio del kernel — misurate 34 voci in un container, di cui UNA sola era un
/// filesystem vero — e leggerne le proprieta' costa: 234 microsecondi a montaggio per il tipo
/// di unita' e 270 per il formato, cioe' 7,4 ms in tutto contro gli 0,59 della strada qui
/// sotto. A un campione al secondo, dodici volte tanto per buttare via il 97 per cento di
/// quello che si e' letto.
/// <para>
/// Si legge invece <c>/proc/self/mountinfo</c>, si sceglie in base al tipo di filesystem, e
/// solo sui montaggi scelti si chiede lo spazio. La scelta e' per <b>elenco di ammessi</b> e
/// non di esclusi: un filesystem che non conosciamo resta fuori invece di entrare, e un
/// montaggio in meno si nota e si aggiunge, mentre un <c>tmpfs</c> presentato come disco fa
/// credere di avere spazio che non esiste.
/// </para>
/// </remarks>
public sealed class LinuxDiskReadingProvider : IDiskReadingProvider
{
    /// <summary>I filesystem che rappresentano spazio vero su un supporto.</summary>
    private static readonly HashSet<string> Ammessi = new(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "f2fs", "jfs", "reiserfs",
        "zfs", "vfat", "exfat", "ntfs", "ntfs3", "fuseblk",
    };

    private readonly IFileTextReader reader;

    /// <summary>Crea il provider sopra il lettore indicato.</summary>
    /// <param name="reader">Da dove si legge /proc.</param>
    public LinuxDiskReadingProvider(IFileTextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        this.reader = reader;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sempre vero, come gli altri provider Linux, e NON <c>OperatingSystem.IsLinux()</c>:
    /// qui la piattaforma e' un parametro di composizione, non una lettura dell'ambiente. E'
    /// cio' che permette di provare il ramo Linux dal runner Windows della CI — che e' proprio
    /// il punto in cui nasce la degradazione, e quindi quello che va provato in entrambe le
    /// direzioni. Su un sistema che non e' Linux <c>/proc/self/mountinfo</c> semplicemente non
    /// si legge, e il collector dichiara Unavailable con il motivo.
    /// </remarks>
    public bool IsSupported => true;

    /// <inheritdoc />
    public string? UnsupportedReason => null;

    /// <inheritdoc />
    public bool TryRead(out IReadOnlyList<DiskReading> readings)
    {
        readings = [];

        if (!reader.TryReadAllText("/proc/self/mountinfo", out string content))
        {
            return false;
        }

        List<DiskReading> trovati = [];

        foreach (string punto in ProcMountInfoParser.MountPoints(content, Ammessi))
        {
            try
            {
                DriveInfo unita = new(punto);

                if (!unita.IsReady)
                {
                    continue;
                }

                trovati.Add(new DiskReading(
                    punto,
                    ByteSize.FromBytes(unita.TotalSize),
                    ByteSize.FromBytes(unita.AvailableFreeSpace)));
            }
            catch (IOException)
            {
                // Smontato fra la lettura di mountinfo e la domanda sullo spazio.
            }
            catch (UnauthorizedAccessException)
            {
                // Montato ma non attraversabile da questo utente.
            }
            catch (ArgumentException)
            {
                // Un percorso che DriveInfo non accetta: fuori, non fa cadere gli altri.
            }
        }

        readings = trovati;

        return true;
    }
}

/// <summary>
/// Adattatore /proc/diskstats per l'attivita' dei dischi.
/// </summary>
/// <remarks>
/// Il problema vero non e' leggere i contatori, e' decidere di CHI sono. /proc/diskstats
/// elenca insieme dischi interi, partizioni e dispositivi finti — <c>loop0</c>, <c>ram0</c>,
/// <c>dm-0</c>, <c>zram0</c> — e sommarli tutti conterebbe lo stesso byte due o tre volte.
/// <para>
/// Il filtro non e' un elenco di prefissi da indovinare, che sbaglierebbe in silenzio al
/// primo nome nuovo. E' una domanda sola al filesystem, attraverso
/// <see cref="IFileTextReader"/> e quindi provabile dal runner Windows: esiste
/// <c>/sys/block/NOME/device/uevent</c>?
/// </para>
/// <para>
/// Quella domanda sola copre tutti e due i casi, e la prima stesura non se n'era accorta:
/// aveva anche un controllo su <c>/sys/block/NOME/stat</c> per escludere le partizioni. E'
/// stata una mutazione a mostrare che non serviva — toglierlo non faceva fallire niente —
/// perche' una partizione sotto <c>/sys/block</c> non compare affatto: sta piu' in basso,
/// dentro la cartella del disco che la contiene. Chi non ha un dispositivo fisico dietro
/// (loop, ram, zram, i volumi logici, un array md) resta fuori per lo stesso motivo.
/// </para>
/// </remarks>
public sealed class LinuxDiskActivityProvider : IDiskActivityProvider
{
    private const string DiskStatsPath = "/proc/diskstats";

    private readonly IFileTextReader reader;

    /// <summary>Crea l'adattatore sopra il lettore indicato.</summary>
    /// <param name="reader">Da dove leggere i file di sistema.</param>
    public LinuxDiskActivityProvider(IFileTextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        this.reader = reader;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Vero sempre, anche fuori da Linux: la piattaforma e' un parametro della composizione e
    /// non una lettura dell'ambiente, ed e' cosi' che questo ramo si prova dal runner Windows.
    /// </remarks>
    public bool IsSupported => true;

    /// <inheritdoc />
    public string? UnsupportedReason => null;

    /// <inheritdoc />
    public bool TryRead(out IReadOnlyList<DiskActivityReading> readings)
    {
        if (!reader.TryReadAllText(DiskStatsPath, out string contenuto))
        {
            readings = [];

            return false;
        }

        List<DiskActivityReading> trovati = [];

        foreach (DiskStatsLine riga in ProcDiskStatsParser.Read(contenuto))
        {
            if (!HaUnDispositivoDietro(riga.Device))
            {
                continue;
            }

            trovati.Add(DiskActivityReading.ConTempoOccupato(
                riga.Device, riga.BytesRead, riga.BytesWritten, riga.Busy));
        }

        readings = trovati;

        return true;
    }

    private bool HaUnDispositivoDietro(string nome) =>
        reader.TryReadAllText($"/sys/block/{nome}/device/uevent", out _);
}