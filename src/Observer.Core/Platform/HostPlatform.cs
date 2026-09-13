using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Disk;
using Observer.Core.Metrics.Memory;

namespace Observer.Core.Platform;

/// <summary>
/// The platform to collect on. It is a PARAMETER and not a reading of the environment,
/// so the Linux branch can be exercised from CI's Windows runner: the point where the
/// degradation starts is exactly what has to be tested in both directions.
/// </summary>
public enum HostPlatform
{
    /// <summary>Unrecognized platform: no source is measurable.</summary>
    Unknown = 0,

    /// <summary>Windows.</summary>
    Windows = 1,

    /// <summary>Linux.</summary>
    Linux = 2,
}

/// <summary>Detects the current platform.</summary>
public static class HostPlatformDetector
{
    /// <summary>Platform the process is running on right now.</summary>
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
/// Port for reading text files. It exists to make the Linux providers testable without a
/// Linux machine: the test injects a fake reader and verifies what the provider really
/// does, not only what the parser does.
/// </summary>
public interface IFileTextReader
{
    /// <summary>Reads the whole content. False if the file does not exist or is not readable.</summary>
    bool TryReadAllText(string path, out string content);
}

/// <summary>Real reader, on the filesystem.</summary>
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
            // /proc can vanish or become unreadable: degrade, do not bring the service down.
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
/// CPU port for a platform where the measurement is not possible. It exists so that the
/// metric stays in the catalog with its explanation instead of disappearing: "it cannot be
/// measured here" and "I forgot about it" must be distinguishable in a dashboard.
/// </summary>
public sealed class UnsupportedCpuTimesProvider : ICpuTimesProvider
{
    /// <summary>Creates the port with the reason to show.</summary>
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

/// <summary>Equivalent of <see cref="UnsupportedCpuTimesProvider"/> for memory.</summary>
public sealed class UnsupportedMemoryReadingProvider : IMemoryReadingProvider
{
    /// <summary>Creates the port with the reason to show.</summary>
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

/// <summary>Disk port for a platform this program does not know how to measure.</summary>
public sealed class UnsupportedDiskReadingProvider : IDiskReadingProvider
{
    /// <summary>Creates the port with the reason to show.</summary>
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

/// <summary>Disk activity port for a platform this program does not know how to measure.</summary>
public sealed class UnsupportedDiskActivityProvider : IDiskActivityProvider
{
    /// <summary>Creates the port with the reason to show.</summary>
    /// <param name="reason">Why nothing is measured here.</param>
    public UnsupportedDiskActivityProvider(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        UnsupportedReason = reason;
    }

    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public string? UnsupportedReason { get; }

    /// <inheritdoc />
    public bool TryRead(out IReadOnlyList<DiskActivityReading> readings)
    {
        readings = [];

        return false;
    }
}