namespace Observer.Core.Metrics.Cpu;

/// <summary>
/// Port to the platform's CPU time counters. It exists to keep the raw reading out of the
/// collector: that is what makes the computation testable without hardware.
/// </summary>
public interface ICpuTimesProvider
{
    /// <summary>False when the platform does not expose these counters at all.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Why it is not supported, when <see cref="IsSupported"/> is false. It is the sentence
    /// that ends up in the dashboard in place of the value.
    /// </summary>
    string? UnsupportedReason { get; }

    /// <summary>Reads the cumulative counters. False if the reading fails right now.</summary>
    bool TryRead(out CpuTimes times);
}

/// <summary>
/// CPU usage collector. It opens no file and calls no OS API: it asks the port for the ticks and
/// applies <see cref="CpuUsage"/>. It keeps the previous sample because a percentage is by
/// definition a difference between two readings.
/// </summary>
public sealed class CpuCollector : IMetricCollector
{
    /// <summary>Identifier of the total CPU usage metric.</summary>
    public const string TotalUsageMetricId = "cpu.usage.total";

    private static readonly MetricDescriptor[] DescriptorList =
    [
        new(TotalUsageMetricId, "CPU usage", MetricUnit.Percent, IsPerInstance: false),
    ];

    private readonly ICpuTimesProvider provider;
    private CpuTimes? previous;

    /// <summary>Creates the collector on top of the given port.</summary>
    public CpuCollector(ICpuTimesProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        this.provider = provider;
    }

    /// <inheritdoc />
    public string Id => "cpu";

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
            // Stays in the catalog with the explanation: "it cannot be measured here" is
            // information, "the metric is gone" is an apparent bug.
            return Degraded(
                CollectorStatus.Unsupported,
                provider.UnsupportedReason ?? "source not supported on this platform");
        }

        if (!provider.TryRead(out CpuTimes current))
        {
            // Clear the history: computing a delta across a hole would produce a percentage
            // averaged over an unknown interval, that is, a made-up number.
            previous = null;
            return Degraded(CollectorStatus.Unavailable, "couldn't read the CPU counters");
        }

        if (previous is not CpuTimes last)
        {
            previous = current;
            return Degraded(
                CollectorStatus.Warmup,
                SampleFailureText.Describe(SampleFailure.FirstSample));
        }

        previous = current;

        if (!CpuUsage.TryComputePercent(last, current, out Units.Percent usage, out SampleFailure failure))
        {
            // Empty and explained, never a wrong number.
            return Degraded(CollectorStatus.Unavailable, SampleFailureText.Describe(failure));
        }

        return new MetricSnapshot(
            Id,
            CollectorStatus.Ok,
            Message: null,
            [MetricPoint.Measured(TotalUsageMetricId, instance: null, MetricValue.FromNumber(usage.Points))]);
    }

    private MetricSnapshot Degraded(CollectorStatus status, string message) =>
        new(Id, status, message, []);
}
