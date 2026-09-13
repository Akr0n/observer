using Observer.Core.Units;

namespace Observer.Core.Metrics.Disk;

/// <summary>
/// The cumulative counters of ONE device: how much it has read, how much it has written, and how
/// long it has been working.
/// </summary>
/// <remarks>
/// These are counters since power-on, not measurements: on their own they say nothing useful. The
/// value comes from the difference between two readings, and <see cref="DiskActivityRates"/> is
/// what takes it.
/// <para>
/// Working time arrives from the two opposite sides depending on the platform, and that is why
/// there are two factories instead of a single field: Windows counts IDLE ticks, Linux counts
/// BUSY ticks. They are the same quantity seen from the opposite direction, and flattening them
/// in here would mean one of the two sides is lying.
/// </para>
/// </remarks>
public readonly record struct DiskActivityReading
{
    private DiskActivityReading(
        string instance,
        ulong bytesRead,
        ulong bytesWritten,
        TimeSpan? busy,
        TimeSpan? idle)
    {
        Instance = instance;
        BytesRead = bytesRead;
        BytesWritten = bytesWritten;
        Busy = busy;
        Idle = idle;
    }

    /// <summary>
    /// What the device is called for whoever is looking: <c>Disk 0</c> on Windows, <c>sda</c> on
    /// Linux. It must stay stable from one sample to the next, or the series breaks in two.
    /// </summary>
    /// <remarks>
    /// It is a DEVICE, not a volume: <c>C:</c> and <c>Disk 0</c> are not the same thing and the
    /// correspondence between the two is not one to one. Tying them together would require
    /// crossing the partitions, and a row saying "C:" while showing the traffic of two volumes
    /// would be worse than a row that honestly says "Disk 0".
    /// </remarks>
    public string Instance { get; }

    /// <summary>Bytes read since power-on.</summary>
    public ulong BytesRead { get; }

    /// <summary>Bytes written since power-on.</summary>
    public ulong BytesWritten { get; }

    /// <summary>Cumulative time in which the device had requests in flight, when known.</summary>
    public TimeSpan? Busy { get; }

    /// <summary>Cumulative time in which the device had nothing to do, when known.</summary>
    public TimeSpan? Idle { get; }

    /// <summary>Builds a reading from a platform that counts BUSY time.</summary>
    /// <param name="instance">Device name.</param>
    /// <param name="bytesRead">Bytes read since power-on.</param>
    /// <param name="bytesWritten">Bytes written since power-on.</param>
    /// <param name="busy">Cumulative time with requests in flight.</param>
    /// <returns>The reading.</returns>
    public static DiskActivityReading WithBusyTime(
        string instance,
        ulong bytesRead,
        ulong bytesWritten,
        TimeSpan busy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        NonNegative(busy, nameof(busy));

        return new DiskActivityReading(instance, bytesRead, bytesWritten, busy, idle: null);
    }

    /// <summary>Builds a reading from a platform that counts IDLE time.</summary>
    /// <param name="instance">Device name.</param>
    /// <param name="bytesRead">Bytes read since power-on.</param>
    /// <param name="bytesWritten">Bytes written since power-on.</param>
    /// <param name="idle">Cumulative time with no requests in flight.</param>
    /// <returns>The reading.</returns>
    public static DiskActivityReading WithIdleTime(
        string instance,
        ulong bytesRead,
        ulong bytesWritten,
        TimeSpan idle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        NonNegative(idle, nameof(idle));

        return new DiskActivityReading(instance, bytesRead, bytesWritten, busy: null, idle);
    }

    private static void NonNegative(TimeSpan amount, string parameterName)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, amount, "a cumulative time cannot be negative");
        }
    }
}

/// <summary>
/// The port that reads the disks' activity counters.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="IDiskReadingProvider"/> on purpose, even though it speaks of the
/// same physical objects: that one measures space on the VOLUMES, this one traffic on the
/// DEVICES, and the two are read from different places under different names. A single port would
/// force one of the two to pretend it knows the other.
/// </remarks>
public interface IDiskActivityProvider
{
    /// <summary>False when on this platform nothing is measured at all.</summary>
    bool IsSupported { get; }

    /// <summary>Why nothing is measured, when nothing is measured.</summary>
    string? UnsupportedReason { get; }

    /// <summary>Reads the counters. False when the reading fails entirely.</summary>
    bool TryRead(out IReadOnlyList<DiskActivityReading> readings);
}

/// <summary>
/// From two successive readings to the numbers to show.
/// </summary>
/// <remarks>
/// A pure function, kept apart from the collector for the same reason as <c>CpuUsage</c>: here
/// every way of getting it wrong produces a believable number, and a believable number that is
/// false is one nobody finds by looking at the window.
/// </remarks>
public static class DiskActivityRates
{
    /// <summary>Bytes per second between two readings of the same counter.</summary>
    /// <param name="previous">The counter at the previous sample.</param>
    /// <param name="current">The counter now.</param>
    /// <param name="elapsed">Time elapsed between the two.</param>
    /// <param name="rate">The rate, set only if the computation succeeds.</param>
    /// <param name="failure">Why it could not be computed.</param>
    /// <returns>True if the rate is usable.</returns>
    public static bool TryComputeBytesPerSecond(
        ulong previous,
        ulong current,
        TimeSpan elapsed,
        out double rate,
        out SampleFailure failure)
    {
        rate = 0d;

        if (elapsed <= TimeSpan.Zero)
        {
            // This is not pedantry: dividing by zero here does not raise an error, it produces
            // infinity, and MetricValue.FromNumber throws on non-finite values — losing the
            // whole HTTP response because of a single disk.
            failure = SampleFailure.NoElapsedTime;

            return false;
        }

        if (current < previous)
        {
            failure = SampleFailure.CounterWentBackwards;

            return false;
        }

        double value = (current - previous) / elapsed.TotalSeconds;

        if (!double.IsFinite(value))
        {
            failure = SampleFailure.NotFinite;

            return false;
        }

        rate = value;
        failure = SampleFailure.Unknown;

        return true;
    }

    /// <summary>How busy the device has been, as a percentage of the elapsed time.</summary>
    /// <param name="previous">Previous reading.</param>
    /// <param name="current">Current reading.</param>
    /// <param name="elapsed">Time elapsed between the two.</param>
    /// <param name="busy">The busy share, set only if the computation succeeds.</param>
    /// <param name="failure">Why it could not be computed.</param>
    /// <returns>True if the busy share is usable.</returns>
    /// <remarks>
    /// Read time and write time are <b>not</b> summed. The two queues overlap, and on one window
    /// that sum has already given 843%: a number nobody takes for wrong until it goes past a
    /// hundred.
    /// </remarks>
    public static bool TryComputeBusy(
        DiskActivityReading previous,
        DiskActivityReading current,
        TimeSpan elapsed,
        out Percent busy,
        out SampleFailure failure)
    {
        busy = default;

        if (elapsed <= TimeSpan.Zero)
        {
            failure = SampleFailure.NoElapsedTime;

            return false;
        }

        double ratio;

        if (previous.Busy is { } busyBefore && current.Busy is { } busyNow)
        {
            if (busyNow < busyBefore)
            {
                failure = SampleFailure.CounterWentBackwards;

                return false;
            }

            ratio = (busyNow - busyBefore) / elapsed;
        }
        else if (previous.Idle is { } idleBefore && current.Idle is { } idleNow)
        {
            if (idleNow < idleBefore)
            {
                failure = SampleFailure.CounterWentBackwards;

                return false;
            }

            ratio = 1d - ((idleNow - idleBefore) / elapsed);
        }
        else
        {
            // Unreachable when going through the two factories, which always set one of the two
            // times. It is only reached with a reading built as default, and in that case there
            // really is no diagnosis to give.
            failure = SampleFailure.Unknown;

            return false;
        }

        if (!double.IsFinite(ratio))
        {
            failure = SampleFailure.NotFinite;

            return false;
        }

        // The two bounds are measured, not theoretical. Below zero: on an IDLE disk the idle
        // counter advances by a hair more than the interval, because it is not the same clock
        // counting them, and on this machine the computation gave -0.07% — which
        // Percent.TryFromRatio rejects, turning an idle disk into a fault. Above a hundred: with
        // more requests queued the busy ticks exceed the interval, and a disk is not 150% busy,
        // it is busy.
        if (!Percent.TryFromRatio(Math.Clamp(ratio, 0d, 1d), out busy))
        {
            failure = SampleFailure.NotFinite;

            return false;
        }

        failure = SampleFailure.Unknown;

        return true;
    }
}