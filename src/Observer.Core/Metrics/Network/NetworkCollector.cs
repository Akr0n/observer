namespace Observer.Core.Metrics.Network;

/// <summary>
/// Network traffic: bytes received and sent per second, per interface.
/// </summary>
/// <remarks>
/// A rate collector like <see cref="Disk.DiskActivityCollector"/>, but simpler: there is no
/// "busy percentage" for a network interface, only the two byte rates. The state is PER
/// INTERFACE — interfaces appear and vanish while the program runs (a VPN comes up, a Wi-Fi
/// adapter is disabled), and an interface that has just appeared has no previous sample. If it
/// stole somebody else's, or if its absolute counter were divided by one second, it would show
/// up on screen with a huge and plausible number.
/// </remarks>
public sealed class NetworkCollector : IMetricCollector
{
    /// <summary>Bytes received per second.</summary>
    public const string ReceivedBytesPerSecondMetricId = "network.received.bytespersecond";

    /// <summary>Bytes sent per second.</summary>
    public const string SentBytesPerSecondMetricId = "network.sent.bytespersecond";

    private static readonly MetricUnit BytesPerSecond = new("B/s");

    private static readonly IReadOnlyList<MetricDescriptor> DescriptorList =
    [
        new(ReceivedBytesPerSecondMetricId, "Network in", BytesPerSecond, IsPerInstance: true),
        new(SentBytesPerSecondMetricId, "Network out", BytesPerSecond, IsPerInstance: true),
    ];

    private readonly INetworkInterfaceProvider provider;
    private readonly TimeProvider clock;

    private readonly Dictionary<string, NetworkReading> previous =
        new(StringComparer.Ordinal);

    private long previousInstant;
    private bool hasPrevious;

    /// <summary>Creates the collector over the given port.</summary>
    /// <param name="provider">Where the counters are read from.</param>
    /// <param name="timeProvider">The clock, or null for the system one.</param>
    public NetworkCollector(INetworkInterfaceProvider provider, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(provider);

        this.provider = provider;
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Id => "network";

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

        if (!provider.TryRead(out IReadOnlyList<NetworkReading> readings))
        {
            // The history is cleared: resuming after the gap, the difference would be divided
            // by an interval whose duration is not known. One extra warm-up round costs a
            // second; an average over an unknown span cannot be told apart afterwards.
            Forget();

            return Degraded(CollectorStatus.Unavailable, "the network counters could not be read");
        }

        long now = clock.GetTimestamp();

        if (!hasPrevious)
        {
            Remember(readings, now);

            return Degraded(CollectorStatus.Warmup, SampleFailureText.Describe(SampleFailure.FirstSample));
        }

        TimeSpan elapsed = clock.GetElapsedTime(previousInstant, now);
        List<MetricPoint> points = new(readings.Count * 2);

        foreach (NetworkReading reading in readings)
        {
            if (previous.TryGetValue(reading.Instance, out NetworkReading before))
            {
                Measure(points, before, reading, elapsed);
            }
            else
            {
                // Interface that has just appeared: it exists, and that it exists must be said,
                // but it has no value yet. Staying silent about it would make it look absent;
                // publishing one would make it look measured.
                Missing(points, reading.Instance, SampleFailure.FirstSample);
            }
        }

        // Only the interfaces seen NOW stay in memory: one that was unplugged or disabled
        // disappears from the points instead of showing its last number for ever.
        Remember(readings, now);

        return readings.Count == 0
            ? new MetricSnapshot(Id, CollectorStatus.Ok, "no network interface to report on this machine", [])
            : new MetricSnapshot(Id, CollectorStatus.Ok, null, points);
    }

    private static void Measure(
        List<MetricPoint> points,
        NetworkReading before,
        NetworkReading now,
        TimeSpan elapsed)
    {
        points.Add(Rate(
            ReceivedBytesPerSecondMetricId, now.Instance, before.BytesReceived, now.BytesReceived, elapsed));

        points.Add(Rate(
            SentBytesPerSecondMetricId, now.Instance, before.BytesSent, now.BytesSent, elapsed));
    }

    private static MetricPoint Rate(
        string metric,
        string instance,
        ulong before,
        ulong now,
        TimeSpan elapsed) =>
        NetworkRates.TryComputeBytesPerSecond(
            before, now, elapsed, out double rate, out SampleFailure failure)
            ? MetricPoint.Measured(metric, instance, MetricValue.FromNumber(rate))
            : MetricPoint.Unavailable(metric, instance, SampleFailureText.Describe(failure));

    private static void Missing(List<MetricPoint> points, string instance, SampleFailure failure)
    {
        string reason = SampleFailureText.Describe(failure);

        points.Add(MetricPoint.Unavailable(ReceivedBytesPerSecondMetricId, instance, reason));
        points.Add(MetricPoint.Unavailable(SentBytesPerSecondMetricId, instance, reason));
    }

    private void Remember(IReadOnlyList<NetworkReading> readings, long now)
    {
        previous.Clear();

        foreach (NetworkReading reading in readings)
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

/// <summary>
/// From two successive readings to the numbers to show.
/// </summary>
/// <remarks>
/// A pure function, kept apart from the collector for the same reason as
/// <see cref="Disk.DiskActivityRates"/>: here every way of getting it wrong produces a
/// believable number, and a believable number that is false is one nobody finds by looking at
/// the window.
/// </remarks>
public static class NetworkRates
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
            // whole HTTP response because of a single interface.
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
}
