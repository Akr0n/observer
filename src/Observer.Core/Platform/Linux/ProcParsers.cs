using System.Text;
using System.Globalization;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Memory;
using Observer.Core.Units;

namespace Observer.Core.Platform.Linux;

/// <summary>
/// Parser for the aggregate line of /proc/stat. Pure function: it receives the content
/// already read and opens no file. That is why it runs identically on CI's Windows runner.
/// </summary>
public static class ProcStatParser
{
    // user, nice, system, idle, iowait, irq, softirq, steal. The next two fields
    // (guest, guest_nice) are ALREADY counted inside user and nice: adding them again would
    // inflate the denominator and make the CPU read low.
    private const int MaxCountedFields = 8;

    // At least user, nice, system, idle are needed: MSYS2's emulated /proc stops here.
    private const int MinRequiredFields = 4;

    /// <summary>
    /// Extracts the cumulative times from the aggregate "cpu" line. Returns false for any
    /// input that does not contain a readable aggregate line, and never throws: an exception
    /// here would bring down the sampling of every metric, not only of the CPU.
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
            // "cpu " with the trailing space: it excludes the per-core lines "cpu0", "cpu1", ...
            // The aggregate line has two spaces, and that is why the split discards empty entries.
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

            // idle (index 3) + iowait (index 4): both are time not worked.
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
/// Parser for /proc/meminfo. Pure function, like <see cref="ProcStatParser"/>.
/// </summary>
public static class ProcMeminfoParser
{
    /// <summary>
    /// Extracts total, available and swap. Returns false when MemTotal is missing, because a
    /// total of zero would turn every percentage into a division by zero: better to declare
    /// the sample not credible than to publish an invented one.
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

        // Kernels < 3.14 and partial /proc do not expose MemAvailable. It is estimated, but it
        // is DECLARED: the UI must be able to write "approximate" instead of lying.
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

        // "524288 kB" -> stop at the first space. The unit is always labelled "kB" but it is
        // worth 1024 bytes, and that is why it goes through ByteSize.FromKibibytes.
        int space = rest.IndexOf(' ');
        ReadOnlySpan<char> number = space < 0 ? rest : rest[..space];

        return long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out kibibytes);
    }
}

/// <summary>Reading of /proc/self/mountinfo: which filesystems are mounted and where.</summary>
public static class ProcMountInfoParser
{
    /// <summary>The mount points of the allowed filesystems, read from /proc/self/mountinfo.</summary>
    /// <param name="content">The content of the file.</param>
    /// <param name="allowed">The filesystem types to keep.</param>
    /// <returns>The mount points, without repetitions, in the order in which they appear.</returns>
    /// <remarks>
    /// The format has a VARIABLE number of fields: between the sixth and the <c>-</c>
    /// separator there are zero or more optional fields, and the filesystem type sits right
    /// AFTER that separator. Counting the fields from the start works until there is a shared
    /// mount, and then it stops — so the type is looked for starting from the separator, which
    /// is the only fixed point of the line.
    /// <para>
    /// The same filesystem can be mounted more than once (bind mount): without removing the
    /// repetitions it would appear on screen several times, every time with the same numbers,
    /// as if they were different disks.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> MountPoints(string content, ISet<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(allowed);

        List<string> points = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            int separator = Array.LastIndexOf(fields, "-");

            // The field after the separator (the type) and the fifth from the start (the mount
            // point) are both needed: below these sizes the line is not a mount.
            if (separator < 4 || separator + 1 >= fields.Length)
            {
                continue;
            }

            if (!allowed.Contains(fields[separator + 1]))
            {
                continue;
            }

            string point = DecodeOctal(fields[4]);

            if (seen.Add(point))
            {
                points.Add(point);
            }
        }

        return points;
    }

    /// <summary>Puts back the characters that mountinfo writes in octal.</summary>
    /// <remarks>
    /// A space in a mount point arrives as <c> </c>, and without translating it the path
    /// does not exist: the volume would disappear from the list without an error. It happens
    /// with external disks, which often have spaces in their name.
    /// </remarks>
    private static string DecodeOctal(string path)
    {
        if (!path.Contains('\\', StringComparison.Ordinal))
        {
            return path;
        }

        StringBuilder built = new(path.Length);

        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '\\'
                && i + 3 < path.Length
                && int.TryParse(
                    path.AsSpan(i + 1, 3),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int octal))
            {
                built.Append((char)Convert.ToInt32(octal.ToString(CultureInfo.InvariantCulture), 8));
                i += 3;

                continue;
            }

            built.Append(path[i]);
        }

        return built.ToString();
    }
}

/// <summary>One line of /proc/diskstats, already converted into bytes and time.</summary>
/// <param name="Device">Name of the device, for example <c>sda</c> or <c>nvme0n1</c>.</param>
/// <param name="BytesRead">Bytes read since power-on.</param>
/// <param name="BytesWritten">Bytes written since power-on.</param>
/// <param name="Busy">Cumulative time with at least one request in flight.</param>
public readonly record struct DiskStatsLine(
    string Device,
    ulong BytesRead,
    ulong BytesWritten,
    TimeSpan Busy);

/// <summary>
/// Parser for /proc/diskstats. Pure function like the others: it receives the content already
/// read, so it runs identically on the Windows runner.
/// </summary>
/// <remarks>
/// Two things have to be known and neither of them can be guessed.
/// <para>
/// The first: sectors here are <b>always</b> 512 bytes, by documented contract of the kernel,
/// and have nothing to do with the physical block size. A "4K native" disk counts them as 512
/// all the same, and multiplying by the real size of the sector would publish numbers eight
/// times larger than the truth.
/// </para>
/// <para>
/// The second: busy time is field 13 (<c>io_ticks</c>), which counts the milliseconds in which
/// the queue was NOT empty. It is not the sum of the read and the write milliseconds, which are
/// fields 7 and 11: those overlap, and summing them has already given 843% on one and the same
/// window.
/// </para>
/// </remarks>
public static class ProcDiskStatsParser
{
    private const ulong SectorBytes = 512UL;

    // Indices counting from zero after the split. The later fields — discards and flushes —
    // exist only on recent kernels, and are not needed here: that is why a line is accepted
    // at 14 fields.
    private const int NameIndex = 2;
    private const int SectorsReadIndex = 5;
    private const int SectorsWrittenIndex = 9;
    private const int BusyMillisecondsIndex = 12;
    private const int MinFields = 14;

    /// <summary>Reads the usable lines, skipping the ones that are not.</summary>
    /// <param name="content">Content of /proc/diskstats.</param>
    /// <returns>One line per recognized device.</returns>
    public static IReadOnlyList<DiskStatsLine> Read(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        List<DiskStatsLine> lines = [];

        foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (fields.Length < MinFields)
            {
                continue;
            }

            if (!TryNumber(fields[SectorsReadIndex], out ulong sectorsRead)
                || !TryNumber(fields[SectorsWrittenIndex], out ulong sectorsWritten)
                || !TryNumber(fields[BusyMillisecondsIndex], out ulong milliseconds))
            {
                continue;
            }

            // A product that overflows would come back around in silence, and the collector
            // would read it as a counter gone backwards instead of as a line to throw away.
            // It takes more room than a real disk can have written, but it costs one
            // comparison.
            if (sectorsRead > ulong.MaxValue / SectorBytes
                || sectorsWritten > ulong.MaxValue / SectorBytes)
            {
                continue;
            }

            lines.Add(new DiskStatsLine(
                fields[NameIndex],
                sectorsRead * SectorBytes,
                sectorsWritten * SectorBytes,
                TimeSpan.FromMilliseconds(milliseconds)));
        }

        return lines;
    }

    private static bool TryNumber(string text, out ulong value) =>
        ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}