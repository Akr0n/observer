using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Disk;
using Observer.Core.Metrics.Memory;
using Observer.Core.Units;

namespace Observer.Core.Platform.Linux;

/// <summary>
/// /proc/stat adapter. It holds ONLY the raw read: the interpretation lives in
/// <see cref="ProcStatParser"/> and the arithmetic in <see cref="CpuUsage"/>. It does not query
/// the operating system, so it can be tested entirely from the Windows runner by injecting a
/// fake reader — and what ends up covered is the provider's behaviour, not just the parser's.
/// </summary>
public sealed class LinuxCpuTimesProvider : ICpuTimesProvider
{
    /// <summary>Path of the file holding the CPU counters.</summary>
    public const string StatPath = "/proc/stat";

    private readonly IFileTextReader reader;

    /// <summary>Creates the adapter over the given reader.</summary>
    public LinuxCpuTimesProvider(IFileTextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        this.reader = reader;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always true: on Linux /proc/stat exists by kernel contract. If it cannot be read right
    /// now that is a momentary fault (Unavailable), not a lack of support.
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

/// <summary>/proc/meminfo adapter, with the same properties as <see cref="LinuxCpuTimesProvider"/>.</summary>
public sealed class LinuxMemoryReadingProvider : IMemoryReadingProvider
{
    /// <summary>Path of the file holding the memory values.</summary>
    public const string MeminfoPath = "/proc/meminfo";

    private readonly IFileTextReader reader;

    /// <summary>Creates the adapter over the given reader.</summary>
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


/// <summary>Linux adapter for the space on the volumes, through /proc/self/mountinfo.</summary>
/// <remarks>
/// <b>Enumeration does NOT go through DriveInfo.GetDrives().</b> On Linux that returns every
/// single kernel mount — 34 entries measured in a container, of which ONE alone was a real
/// filesystem — and reading their properties costs: 234 microseconds per mount for the drive
/// type and 270 for the format, that is 7.4 ms in total against the 0.59 of the route below. At
/// one sample a second, twelve times as much to throw away 97 per cent of what was read.
/// <para>
/// What is read instead is <c>/proc/self/mountinfo</c>, the choice is made on the filesystem
/// type, and space is asked for only on the chosen mounts. The choice is by <b>allow list</b>
/// and not by deny list: a filesystem we do not know stays out instead of getting in, and a
/// missing mount gets noticed and added, while a <c>tmpfs</c> presented as a disk makes you
/// believe you have space that does not exist.
/// </para>
/// </remarks>
public sealed class LinuxDiskReadingProvider : IDiskReadingProvider
{
    /// <summary>The filesystems that stand for real space on a medium.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "f2fs", "jfs", "reiserfs",
        "zfs", "vfat", "exfat", "ntfs", "ntfs3", "fuseblk",
    };

    private readonly IFileTextReader reader;

    /// <summary>Creates the provider over the given reader.</summary>
    /// <param name="reader">Where /proc is read from.</param>
    public LinuxDiskReadingProvider(IFileTextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        this.reader = reader;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always true, like the other Linux providers, and NOT <c>OperatingSystem.IsLinux()</c>:
    /// here the platform is a composition parameter, not a reading of the environment. That is
    /// what makes it possible to test the Linux branch from CI's Windows runner — which is
    /// exactly where the degradation is born, and therefore what has to be tested in both
    /// directions. On a system that is not Linux <c>/proc/self/mountinfo</c> simply does not
    /// read, and the collector declares Unavailable with the reason.
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

        List<DiskReading> found = [];

        foreach (string mountPoint in ProcMountInfoParser.MountPoints(content, Allowed))
        {
            try
            {
                DriveInfo drive = new(mountPoint);

                if (!drive.IsReady)
                {
                    continue;
                }

                found.Add(new DiskReading(
                    mountPoint,
                    ByteSize.FromBytes(drive.TotalSize),
                    ByteSize.FromBytes(drive.AvailableFreeSpace)));
            }
            catch (IOException)
            {
                // Unmounted between reading mountinfo and asking about the space.
            }
            catch (UnauthorizedAccessException)
            {
                // Mounted but not traversable by this user.
            }
            catch (ArgumentException)
            {
                // A path DriveInfo does not accept: out, and it does not bring the others down.
            }
        }

        readings = found;

        return true;
    }
}

/// <summary>
/// /proc/diskstats adapter for disk activity.
/// </summary>
/// <remarks>
/// The real problem is not reading the counters, it is deciding WHOSE they are. /proc/diskstats
/// lists whole disks, partitions and fake devices together — <c>loop0</c>, <c>ram0</c>,
/// <c>dm-0</c>, <c>zram0</c> — and summing them all would count the same byte two or three times.
/// <para>
/// The filter is not a list of prefixes to be guessed, which would go wrong silently on the
/// first new name. It is one single question to the filesystem, through
/// <see cref="IFileTextReader"/> and therefore testable from the Windows runner: does
/// <c>/sys/block/NAME/device/uevent</c> exist?
/// </para>
/// <para>
/// That one question covers both cases, and the first version had not noticed: it also had a
/// check on <c>/sys/block/NAME/stat</c> to exclude partitions. It was a mutation that showed it
/// was not needed — removing it made nothing fail — because a partition does not appear under
/// <c>/sys/block</c> at all: it lives lower down, inside the directory of the disk that contains
/// it. Whatever has no physical device behind it (loop, ram, zram, logical volumes, an md array)
/// stays out for the same reason.
/// </para>
/// </remarks>
public sealed class LinuxDiskActivityProvider : IDiskActivityProvider
{
    private const string DiskStatsPath = "/proc/diskstats";

    private readonly IFileTextReader reader;

    /// <summary>Creates the adapter over the given reader.</summary>
    /// <param name="reader">Where the system files are read from.</param>
    public LinuxDiskActivityProvider(IFileTextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        this.reader = reader;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always true, even outside Linux: the platform is a parameter of the composition and not a
    /// reading of the environment, and that is how this branch is tested from the Windows runner.
    /// </remarks>
    public bool IsSupported => true;

    /// <inheritdoc />
    public string? UnsupportedReason => null;

    /// <inheritdoc />
    public bool TryRead(out IReadOnlyList<DiskActivityReading> readings)
    {
        if (!reader.TryReadAllText(DiskStatsPath, out string content))
        {
            readings = [];

            return false;
        }

        List<DiskActivityReading> found = [];

        foreach (DiskStatsLine line in ProcDiskStatsParser.Read(content))
        {
            if (!HasDeviceBehind(line.Device))
            {
                continue;
            }

            found.Add(DiskActivityReading.WithBusyTime(
                line.Device, line.BytesRead, line.BytesWritten, line.Busy));
        }

        readings = found;

        return true;
    }

    private bool HasDeviceBehind(string name) =>
        reader.TryReadAllText($"/sys/block/{name}/device/uevent", out _);
}