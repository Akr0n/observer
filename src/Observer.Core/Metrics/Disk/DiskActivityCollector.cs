using Observer.Core.Units;

namespace Observer.Core.Metrics.Disk;

/// <summary>
/// How hard the disks are working: bytes read and written per second, and the percentage of
/// time busy.
/// </summary>
/// <remarks>
/// It is the first collector that measures a RATE, and that changes three things compared
/// with all the others.
/// <para>
/// The first: a clock is needed. The CPU computes a percentage as the ratio of two deltas of
/// the same quantity, and in the ratio the unit cancels out — no collector, so far, has ever
/// needed to know how much time had passed. Bytes per second do, and the clock comes from
/// outside (<see cref="TimeProvider"/>) because a test must not wait a real second to
/// exercise a division.
/// </para>
/// <para>
/// The second: the state is PER INSTANCE. Disks appear and vanish while the program runs — a
/// memory stick, a network drive — and a device that has just appeared has no previous
/// sample. If it stole somebody else's, or if its absolute counter were divided by one
/// second, it would show up on screen with a huge and plausible number.
/// </para>
/// <para>
/// The third: the instances are DEVICES, not volumes, and therefore do not line up with those
/// of <see cref="DiskCollector"/>. A disk can carry several volumes and a volume can span
/// several disks: the correspondence is not one to one, and pretending it is in order to make
/// two on-screen lists match would mean attributing to <c>C:</c> traffic that is not its own.
/// </para>
/// </remarks>
public sealed class DiskActivityCollector : IMetricCollector
{
    /// <summary>Bytes read per second.</summary>
    public const string ReadBytesPerSecondMetricId = "disk.read.bytespersecond";

    /// <summary>Bytes written per second.</summary>
    public const string WriteBytesPerSecondMetricId = "disk.write.bytespersecond";

    /// <summary>Percentage of time in which the device had requests outstanding.</summary>
    public const string BusyPercentMetricId = "disk.busy.percent";

    // MetricUnit is an open type and not an enum, on purpose: a new unit does not touch Core.
    private static readonly MetricUnit BytesPerSecond = new("B/s");

    private static readonly IReadOnlyList<MetricDescriptor> DescriptorList =
    [
        new(ReadBytesPerSecondMetricId, "Disk read", BytesPerSecond, IsPerInstance: true),
        new(WriteBytesPerSecondMetricId, "Disk write", BytesPerSecond, IsPerInstance: true),
        new(BusyPercentMetricId, "Disk activity", MetricUnit.Percent, IsPerInstance: true),
    ];

    private readonly IDiskActivityProvider provider;
    private readonly TimeProvider clock;

    private readonly Dictionary<string, DiskActivityReading> previous =
        new(StringComparer.Ordinal);

    private long previousInstant;
    private bool hasPrevious;

    /// <summary>Creates the collector over the given port.</summary>
    /// <param name="provider">Where the counters are read from.</param>
    /// <param name="timeProvider">The clock, or null for the system one.</param>
    public DiskActivityCollector(IDiskActivityProvider provider, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(provider);

        this.provider = provider;
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Id => "disk.activity";

    /// <inheritdoc />
    public IReadOnlyList<MetricDescriptor> Descriptors => DescriptorList;

    /// <inheritdoc />
    public ValueTask<MetricSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(Collect());
    }

    private MetricSnapshot Collect()
    {
        if (!provider.IsSupported)
        {
            return Degraded(
                CollectorStatus.Unsupported,
                provider.UnsupportedReason ?? "source not supported on this platform");
        }

        if (!provider.TryRead(out IReadOnlyList<DiskActivityReading> readings))
        {
            // The history is cleared: resuming after the gap, the difference would be divided
            // by an interval whose duration is not known. One extra warm-up round costs a
            // second; an average over an unknown span cannot be told apart afterwards.
            Forget();

            return Degraded(CollectorStatus.Unavailable, "the disk activity counters could not be read");
        }

        long now = clock.GetTimestamp();

        if (!hasPrevious)
        {
            Remember(readings, now);

            return Degraded(CollectorStatus.Warmup, SampleFailureText.Describe(SampleFailure.FirstSample));
        }

        TimeSpan elapsed = clock.GetElapsedTime(previousInstant, now);
        List<MetricPoint> points = new(readings.Count * 3);

        foreach (DiskActivityReading reading in readings)
        {
            if (previous.TryGetValue(reading.Instance, out DiskActivityReading before))
            {
                Measure(points, before, reading, elapsed);
            }
            else
            {
                // Device that has just appeared: it exists, and that it exists must be said,
                // but it has no value yet. Staying silent about it would make it look absent;
                // publishing one would make it look measured.
                Missing(points, reading.Instance, SampleFailure.FirstSample);
            }
        }

        // Only the devices seen NOW stay in memory: one that is unplugged disappears from the
        // points instead of showing its last number for ever.
        Remember(readings, now);

        return readings.Count == 0
            ? new MetricSnapshot(Id, CollectorStatus.Ok, "no disk device to report on this machine", [])
            : new MetricSnapshot(Id, CollectorStatus.Ok, null, points);
    }

    private static void Measure(
        List<MetricPoint> points,
        DiskActivityReading before,
        DiskActivityReading now,
        TimeSpan elapsed)
    {
        points.Add(Rate(
            ReadBytesPerSecondMetricId, now.Instance, before.BytesRead, now.BytesRead, elapsed));

        points.Add(Rate(
            WriteBytesPerSecondMetricId,
            now.Instance,
            before.BytesWritten,
            now.BytesWritten,
            elapsed));

        points.Add(
            DiskActivityRates.TryComputeBusy(
                before, now, elapsed, out Percent busy, out SampleFailure failure)
                ? MetricPoint.Measured(
                    BusyPercentMetricId, now.Instance, MetricValue.FromNumber(busy.Points))
                : MetricPoint.Unavailable(
                    BusyPercentMetricId, now.Instance, SampleFailureText.Describe(failure)));
    }

    private static MetricPoint Rate(
        string metric,
        string instance,
        ulong before,
        ulong now,
        TimeSpan elapsed) =>
        DiskActivityRates.TryComputeBytesPerSecond(
            before, now, elapsed, out double rate, out SampleFailure failure)
            ? MetricPoint.Measured(metric, instance, MetricValue.FromNumber(rate))
            : MetricPoint.Unavailable(metric, instance, SampleFailureText.Describe(failure));

    private static void Missing(List<MetricPoint> points, string instance, SampleFailure failure)
    {
        string reason = SampleFailureText.Describe(failure);

        points.Add(MetricPoint.Unavailable(ReadBytesPerSecondMetricId, instance, reason));
        points.Add(MetricPoint.Unavailable(WriteBytesPerSecondMetricId, instance, reason));
        points.Add(MetricPoint.Unavailable(BusyPercentMetricId, instance, reason));
    }

    private void Remember(IReadOnlyList<DiskActivityReading> readings, long now)
    {
        previous.Clear();

        foreach (DiskActivityReading reading in readings)
        {
            previous[reading.Instance] = reading;
        }

        previousInstant = now;
        hasPrevious = true;
    }

    private void Forget()
    {
        previous.Clear();
        hasPrevious = false;
    }

    private MetricSnapshot Degraded(CollectorStatus status, string reason) =>
        new(Id, status, reason, []);
}