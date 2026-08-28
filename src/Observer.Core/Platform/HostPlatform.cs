using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Disk;
using Observer.Core.Metrics.Memory;

namespace Observer.Core.Platform;

/// <summary>
/// La piattaforma su cui raccogliere. E' un PARAMETRO e non una lettura dell'ambiente,
/// cosi' il ramo Linux si puo' provare dal runner Windows della CI: il punto da cui nasce
/// la degradazione e' esattamente quello che va testato in entrambe le direzioni.
/// </summary>
public enum HostPlatform
{
    /// <summary>Piattaforma non riconosciuta: nessuna sorgente e' misurabile.</summary>
    Unknown = 0,

    /// <summary>Windows.</summary>
    Windows = 1,

    /// <summary>Linux.</summary>
    Linux = 2,
}

/// <summary>Rileva la piattaforma corrente.</summary>
public static class HostPlatformDetector
{
    /// <summary>Piattaforma su cui il processo sta girando ora.</summary>
    public static HostPlatform Current
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return HostPlatform.Windows;
            }

            return OperatingSystem.IsLinux() ? HostPlatform.Linux : HostPlatform.Unknown;
        }
    }
}

/// <summary>
/// Porta di lettura di file di testo. Esiste per rendere provabili i provider Linux senza
/// una macchina Linux: il test inietta un lettore finto e verifica il comportamento reale
/// del provider, non solo quello del parser.
/// </summary>
public interface IFileTextReader
{
    /// <summary>Legge tutto il contenuto. False se il file non esiste o non e' leggibile.</summary>
    bool TryReadAllText(string path, out string content);
}

/// <summary>Lettore reale, sul filesystem.</summary>
public sealed class FileTextReader : IFileTextReader
{
    /// <inheritdoc />
    public bool TryReadAllText(string path, out string content)
    {
        try
        {
            content = File.ReadAllText(path);
            return true;
        }
        catch (IOException)
        {
            // /proc puo' sparire o essere illeggibile: degradare, non abbattere il servizio.
            content = string.Empty;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            content = string.Empty;
            return false;
        }
    }
}

/// <summary>
/// Porta CPU per una piattaforma su cui la misura non e' possibile. Esiste perche' la
/// metrica resti nel catalogo con la sua spiegazione invece di sparire: "non si puo'
/// misurare qui" e "me la sono dimenticata" devono essere distinguibili in dashboard.
/// </summary>
public sealed class UnsupportedCpuTimesProvider : ICpuTimesProvider
{
    /// <summary>Crea la porta con il motivo da mostrare.</summary>
    public UnsupportedCpuTimesProvider(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        UnsupportedReason = reason;
    }

    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public string? UnsupportedReason { get; }

    /// <inheritdoc />
    public bool TryRead(out CpuTimes times)
    {
        times = default;
        return false;
    }
}

/// <summary>Equivalente di <see cref="UnsupportedCpuTimesProvider"/> per la memoria.</summary>
public sealed class UnsupportedMemoryReadingProvider : IMemoryReadingProvider
{
    /// <summary>Crea la porta con il motivo da mostrare.</summary>
    public UnsupportedMemoryReadingProvider(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        UnsupportedReason = reason;
    }

    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public string? UnsupportedReason { get; }

    /// <inheritdoc />
    public bool TryRead(out MemoryReading value)
    {
        value = default;
        return false;
    }
}

/// <summary>Porta dei dischi per una piattaforma che non si sa misurare.</summary>
public sealed class UnsupportedDiskReadingProvider : IDiskReadingProvider
{
    /// <summary>Crea la porta con il motivo da mostrare.</summary>
    public UnsupportedDiskReadingProvider(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        UnsupportedReason = reason;
    }

    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public string? UnsupportedReason { get; }

    /// <inheritdoc />
    public bool TryRead(out IReadOnlyList<DiskReading> readings)
    {
        readings = [];

        return false;
    }
}
