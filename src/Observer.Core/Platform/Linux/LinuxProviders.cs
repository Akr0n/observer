using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Memory;

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
