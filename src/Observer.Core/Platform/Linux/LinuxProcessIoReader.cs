using System.Globalization;
using Observer.Core.Processes;

namespace Observer.Core.Platform.Linux;

/// <summary>
/// Linux adapter of the per-process I/O counter, on top of <c>/proc/PID/io</c>.
/// </summary>
/// <remarks>
/// <c>rchar</c> and <c>wchar</c> are summed, NOT <c>read_bytes</c> and <c>write_bytes</c>, and the
/// choice needs explaining because the second pair looks like the right one: it counts the bytes
/// that really reached the disk, the first counts every read and write the process asked for, cache
/// included. But on Windows the only per-process counter is of the second kind — <c>IO_COUNTERS</c>
/// counts the calls, not the sectors — and a list that said "bytes on disk" on one machine and
/// "bytes asked for" on the other would compare two different things under the same title. The panel
/// is called "I/O", and it says the same thing everywhere.
/// <para>
/// <c>/proc/PID/io</c> can only be read with <i>ptrace</i> permission on that process: your own
/// yes, another user's no, short of <c>CAP_SYS_PTRACE</c>. The service runs as the <c>observer</c>
/// user and does not have that capability, deliberately — it would allow reading any process's
/// memory — so on Linux the column stays a dash for everything that is not its own. It is a
/// declared limitation, not a fault: whoever wants it removed adds
/// <c>AmbientCapabilities=CAP_SYS_PTRACE</c> to the unit, knowing what that grants.
/// </para>
/// </remarks>
public sealed class LinuxProcessIoReader : IProcessIoReader
{
    private readonly IFileTextReader reader;

    /// <summary>Creates the adapter on top of the given reader.</summary>
    /// <param name="reader">Where to read the system files from.</param>
    public LinuxProcessIoReader(IFileTextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        this.reader = reader;
    }

    /// <inheritdoc />
    public bool TryRead(int pid, out ulong bytes)
    {
        bytes = 0;

        string path = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/io";

        return reader.TryReadAllText(path, out string content) && TryParse(content, out bytes);
    }

    /// <summary>Reads <c>rchar</c> and <c>wchar</c> from the contents of <c>/proc/PID/io</c>.</summary>
    /// <param name="content">The file, one <c>key: value</c> pair per line.</param>
    /// <param name="bytes">The sum of the two.</param>
    /// <returns>False if either one is missing or is not an integer.</returns>
    public static bool TryParse(string content, out ulong bytes)
    {
        bytes = 0;

        ulong? read = null;
        ulong? written = null;

        foreach (ReadOnlySpan<char> line in content.AsSpan().EnumerateLines())
        {
            if (Value(line, "rchar:", out ulong value))
            {
                read = value;
            }
            else if (Value(line, "wchar:", out value))
            {
                written = value;
            }
        }

        if (read is not { } r || written is not { } w)
        {
            return false;
        }

        ulong sum = r + w;

        // A full wrap of the 64 bits is not a total: it is a wrong total.
        if (sum < r)
        {
            return false;
        }

        bytes = sum;

        return true;
    }

    private static bool Value(ReadOnlySpan<char> line, string key, out ulong value)
    {
        value = 0;

        return line.StartsWith(key, StringComparison.Ordinal)
            && ulong.TryParse(
                line[key.Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}