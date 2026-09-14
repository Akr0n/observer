using System.Globalization;

namespace Observer.App.Services;

/// <summary>One row of the process list, as it arrives from the service.</summary>
/// <param name="Pid">The process identifier.</param>
/// <param name="Name">The executable's name.</param>
/// <param name="CpuPercent">Percentage of the whole machine, or null if not known yet.</param>
/// <param name="WorkingSetBytes">Physical memory in use.</param>
/// <param name="IoBytesPerSecond">
/// Bytes read and written per second, or null if not known. It is missing altogether from the
/// responses of an older service, and so it is null all the same.
/// </param>
public sealed record ProcessWire(
    int Pid, string Name, double? CpuPercent, long WorkingSetBytes, double? IoBytesPerSecond = null);

/// <summary>The response of <c>/processes</c>.</summary>
/// <param name="CapturedAt">When the list was read.</param>
/// <param name="By">
/// The criterion the service actually applied. Null from an older service, which does not echo
/// it back: that is how the client notices it asked for a criterion the service does not
/// know.
/// </param>
/// <param name="Processes">The processes, already sorted by the service.</param>
public sealed record ProcessListWire(
    DateTimeOffset CapturedAt, string? By, IReadOnlyList<ProcessWire> Processes);

/// <summary>A row ready for the screen.</summary>
/// <param name="Pid">The process identifier, needed to kill it.</param>
/// <param name="Name">The executable's name.</param>
/// <param name="Cpu">The CPU already formatted, or a dash if it is not known yet.</param>
/// <param name="Memory">The memory already formatted with the binary prefixes.</param>
/// <param name="Io">The bytes per second already formatted, or a dash if not known.</param>
public sealed record ProcessRowState(int Pid, string Name, string Cpu, string Memory, string Io = "—")
{
    /// <summary>The whole row read out, for whoever cannot see it: the name and the three
    /// columns with their heading.</summary>
    /// <remarks>
    /// A screen reader reading four separate TextBlocks says "claude, 15.1 %, 228.5 MiB, 1.1
    /// MiB/s" without saying what they are: the column headings, which the eye keeps in mind,
    /// do not exist for the ear.
    /// </remarks>
    public string AccessibleName => $"{Name}, CPU {Cpu}, memory {Memory}, I/O {Io}";

    /// <summary>The row for the clipboard: like <see cref="AccessibleName"/>, but with the PID.</summary>
    /// <remarks>
    /// Two almost identical sentences, and the difference is deliberate. The PID is for whoever
    /// pastes the row somewhere — into a search, into a message, next to a command — because
    /// it is the only thing that identifies the process unambiguously: there are a dozen
    /// "chrome"s. It does not belong in <see cref="AccessibleName"/>, because a screen reader
    /// pronounces that one at EVERY arrow key down the list, and a five-digit number read digit
    /// by digit on every row is noise between whoever is scrolling and what they are after.
    /// </remarks>
    public string ForClipboard =>
        $"{Name} (pid {Pid.ToString(CultureInfo.InvariantCulture)}), CPU {Cpu}, memory {Memory}, I/O {Io}";
    /// <summary>Turns a row that came off the wire into a row to show.</summary>
    /// <param name="row">The row that arrived.</param>
    /// <returns>The row to show.</returns>
    /// <remarks>
    /// An unknown CPU becomes a DASH and not a zero. They are two different statements -
    /// "I do not know yet" versus "this process is idle" - and the second, on a list sorted
    /// by consumption, would move attention to the wrong program.
    /// </remarks>
    public static ProcessRowState From(ProcessWire row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new ProcessRowState(
            row.Pid,
            row.Name,
            row.CpuPercent is { } percent
                ? percent.ToString("F1", CultureInfo.InvariantCulture) + " %"
                : "—",
            MetricFormatting.DescribeBytes(row.WorkingSetBytes),
            row.IoBytesPerSecond is { } rate
                ? MetricFormatting.DescribeBytes(rate) + "/s"
                : "—");
    }
}

/// <summary>Outcome of reading the process list.</summary>
/// <param name="Outcome">How it went.</param>
/// <param name="Problem">Sentence ready for the screen, empty when the outcome is Ok.</param>
/// <param name="Processes">The rows, empty when the outcome is not Ok.</param>
public sealed record ProcessFetch(
    ServiceOutcome Outcome,
    string Problem,
    IReadOnlyList<ProcessRowState> Processes);

/// <summary>Outcome of an attempt to kill a process.</summary>
/// <param name="Outcome">How it went.</param>
/// <param name="Problem">Sentence ready for the screen, empty when it worked.</param>
/// <remarks>
/// An outcome of its own and not a plain boolean: "the process is gone" and "the system
/// refused to kill it" call for two different sentences. The first happens often and is not a
/// fault — a process can end on its own between the list and the click — while the second
/// means that program cannot be touched from here.
/// </remarks>
public sealed record KillFetch(ServiceOutcome Outcome, string Problem);

/// <summary>Which resource sits behind a gauge.</summary>
public static class ProcessResource
{
    /// <summary>The resource to ask the service for, for the given row, or null.</summary>
    /// <param name="key">The row's key, in the form <c>collector|metric|instance</c>.</param>
    /// <returns><c>cpu</c>, <c>memory</c>, <c>io</c>, or null when there is no answer for that resource.</returns>
    /// <remarks>
    /// Null for disk SPACE, and it is not an oversight: the space used on a volume is not
    /// attributable to a <i>running</i> process — whoever wrote those files may have been gone
    /// for months. A panel that opened with the CPU list under a volume's title would say
    /// something false: better that gauge does not open at all.
    /// <para>
    /// Disk ACTIVITY, on the other hand, does open, on the list by I/O. It is a list for the
    /// whole machine, not for that disk: the counters are per process, and neither of the two
    /// systems says which device the bytes ended up on. The panel's title says so.
    /// </para>
    /// </remarks>
    public static string? From(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        int separatorIndex = key.IndexOf('|', StringComparison.Ordinal);
        string collector = separatorIndex < 0 ? key : key[..separatorIndex];

        return collector switch
        {
            "cpu" => "cpu",
            "memory" => "memory",
            "disk.activity" => "io",
            _ => null,
        };
    }
}
