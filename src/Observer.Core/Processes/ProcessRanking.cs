namespace Observer.Core.Processes;

/// <summary>
/// Who is consuming what, right now.
/// </summary>
/// <remarks>
/// Memory is read and shown as it is; CPU and I/O are not. They are RATES — processor time, or
/// bytes transferred, divided by elapsed time — so a previous sample is needed, and it has to be
/// kept <b>per PID</b>.
/// <para>
/// PIDs get reused, and that is this type's trap. When a process dies the system can assign the
/// same number to a new one, and its counters restart from zero: comparing against the old
/// sample would give a negative difference, or — if the new process has already done work — a
/// huge number attributed to a program that is gone. That is why the NAME is remembered
/// alongside the counters, and a PID that changes name is a new process, not the same one that
/// slowed down.
/// </para>
/// </remarks>
public sealed class ProcessRanking
{
    private readonly IProcessLister lister;
    private readonly TimeProvider clock;
    private readonly int cores;
    private readonly Dictionary<int, Previous> previousSamples = [];
    private readonly Lock gate = new();

    private long previousInstant;
    private bool hasPrevious;

    /// <summary>Creates the ranking on top of the given port.</summary>
    /// <param name="lister">Where the process list is read from.</param>
    /// <param name="timeProvider">The clock, or null for the system one.</param>
    /// <param name="processorCount">How many cores the machine has, or null to ask for it.</param>
    public ProcessRanking(IProcessLister lister, TimeProvider? timeProvider = null, int? processorCount = null)
    {
        ArgumentNullException.ThrowIfNull(lister);

        this.lister = lister;
        clock = timeProvider ?? TimeProvider.System;
        cores = Math.Max(1, processorCount ?? Environment.ProcessorCount);
    }

    /// <summary>Reads the processes and computes how much they are consuming.</summary>
    /// <param name="processes">The list, with CPU and I/O filled in from the second round on.</param>
    /// <returns>False when the list could not be read at all.</returns>
    /// <remarks>
    /// <b>Under the lock</b>, and not out of generic caution. There is ONLY ONE of this object
    /// per process — registered as a singleton — and the <c>/processes</c> endpoint calls it
    /// inside the HTTP request, with nothing in between. The window polls it once a second for
    /// as long as the process panel stays open, so two dashboards on the same machine are enough
    /// for two reads to start together. And every read EMPTIES and rewrites
    /// <see cref="previousSamples"/>: two concurrent writes to a
    /// <see cref="Dictionary{TKey, TValue}"/> do not throw reliably, and in the worst case they
    /// spin a thread inside Insert — one core at 100% for ever, the request that never returns,
    /// and no error anywhere. It is the same danger <c>MetricSnapshotCache</c> protects the
    /// collectors from — two reads of the same source overlapping — on the one path that did not
    /// have it; there, though, a single atomic write was enough, here it is not, because here the
    /// state is not one reference but a dictionary that is emptied and refilled.
    /// <para>
    /// The price, measured on this machine with about 200 processes: the critical section is the
    /// ENTIRE read of the system, from 14 to 66 ms, and with eight callers together the last one
    /// waited between 300 and 650 ms. The service sets no deadline on requests; the ceiling is
    /// the client's <c>RequestTimeout</c> of 8 s, that is two orders of magnitude further out.
    /// That is why there is no attempt with a deadline: it would be complexity over a number
    /// that never comes close. The real price is another one, and it is declared: a read that
    /// hung now would stop <c>/processes</c> for everyone, not only for whoever asked for it.
    /// </para>
    /// </remarks>
    public bool TryRead(out IReadOnlyList<ProcessUsage> processes)
    {
        lock (gate)
        {
            return Read(out processes);
        }
    }

    private bool Read(out IReadOnlyList<ProcessUsage> processes)
    {
        if (!lister.TryList(out IReadOnlyList<ProcessTimes> readings))
        {
            // The history is cleared: after a gap the delta would be divided by an interval of
            // unknown length, which is the way to invent a believable percentage.
            previousSamples.Clear();
            hasPrevious = false;
            processes = [];

            return false;
        }

        long now = clock.GetTimestamp();
        TimeSpan elapsed = hasPrevious
            ? clock.GetElapsedTime(previousInstant, now)
            : TimeSpan.Zero;

        List<ProcessUsage> usages = new(readings.Count);

        foreach (ProcessTimes reading in readings)
        {
            usages.Add(new ProcessUsage(
                reading.Pid,
                reading.Name,
                Percentage(reading, elapsed),
                reading.WorkingSet,
                IoRate(reading, elapsed)));
        }

        previousSamples.Clear();

        foreach (ProcessTimes reading in readings)
        {
            previousSamples[reading.Pid] = new Previous(reading.Name, reading.Cpu, reading.IoBytes);
        }

        previousInstant = now;
        hasPrevious = true;
        processes = usages;

        return true;
    }

    /// <summary>The processes using the most CPU, in order.</summary>
    /// <param name="all">The complete list.</param>
    /// <param name="count">How many to return.</param>
    /// <returns>The first ones, from the hungriest.</returns>
    /// <remarks>
    /// Whoever has no percentage yet ends up at the bottom, not at zero: they are processes
    /// nothing is known about, and putting them among the idle ones would be a claim that cannot
    /// be made.
    /// </remarks>
    public static IReadOnlyList<ProcessUsage> TopByCpu(
        IReadOnlyList<ProcessUsage> all, int count)
    {
        ArgumentNullException.ThrowIfNull(all);

        return
        [
            .. all
                .OrderByDescending(process => process.CpuPercent ?? -1d)
                .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, count)),
        ];
    }

    /// <summary>The processes taking the most memory, in order.</summary>
    /// <param name="all">The complete list.</param>
    /// <param name="count">How many to return.</param>
    /// <returns>The first ones, from the bulkiest.</returns>
    public static IReadOnlyList<ProcessUsage> TopByMemory(
        IReadOnlyList<ProcessUsage> all, int count)
    {
        ArgumentNullException.ThrowIfNull(all);

        return
        [
            .. all
                .OrderByDescending(process => process.WorkingSet.Bytes)
                .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, count)),
        ];
    }

    /// <summary>The processes transferring the most bytes, in order.</summary>
    /// <param name="all">The complete list.</param>
    /// <param name="count">How many to return.</param>
    /// <returns>The first ones, from the busiest.</returns>
    /// <remarks>Same rule as the CPU: whoever has no rate yet goes to the bottom, not to zero.</remarks>
    public static IReadOnlyList<ProcessUsage> TopByIo(
        IReadOnlyList<ProcessUsage> all, int count)
    {
        ArgumentNullException.ThrowIfNull(all);

        return
        [
            .. all
                .OrderByDescending(process => process.IoBytesPerSecond ?? -1d)
                .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, count)),
        ];
    }

    private double? Percentage(ProcessTimes reading, TimeSpan elapsed)
    {
        if (!HasPrevious(reading, elapsed, out Previous previous) || reading.Cpu < previous.Cpu)
        {
            return null;
        }

        double share = (reading.Cpu - previous.Cpu) / (elapsed * cores);

        // Over the WHOLE machine: 100 means every core busy, not just one. The upper clamp is
        // needed because the two clocks are not the same clock, exactly as for disk busy time.
        return double.IsFinite(share) ? Math.Clamp(share, 0d, 1d) * 100d : null;
    }

    private double? IoRate(ProcessTimes reading, TimeSpan elapsed)
    {
        if (!HasPrevious(reading, elapsed, out Previous previous)
            || reading.IoBytes is not { } now
            || previous.Io is not { } previousIo
            || now < previousIo)
        {
            return null;
        }

        // No upper clamp, unlike the CPU: there is no known physical maximum for the bytes
        // transferred in a second, and a spike of reads from the cache is real data.
        double rate = (now - previousIo) / elapsed.TotalSeconds;

        return double.IsFinite(rate) ? rate : null;
    }

    private bool HasPrevious(ProcessTimes reading, TimeSpan elapsed, out Previous previous)
    {
        previous = default;

        // Same number, different program: the PID has been reused, and the comparison is not made.
        return elapsed > TimeSpan.Zero
            && previousSamples.TryGetValue(reading.Pid, out previous)
            && string.Equals(previous.Name, reading.Name, StringComparison.Ordinal);
    }

    /// <summary>What is remembered about a process between one round and the next.</summary>
    private readonly record struct Previous(string Name, TimeSpan Cpu, ulong? Io);
}