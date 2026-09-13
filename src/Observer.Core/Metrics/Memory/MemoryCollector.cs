using Observer.Core.Units;

namespace Observer.Core.Metrics.Memory;

/// <summary>
/// Port to the platform's memory values. Unlike the CPU no delta is needed: these are
/// instantaneous values, one reading is enough.
/// </summary>
public interface IMemoryReadingProvider
{
    /// <summary>False when the platform does not expose these values.</summary>
    bool IsSupported { get; }

    /// <summary>Why it is not supported, when <see cref="IsSupported"/> is false.</summary>
    string? UnsupportedReason { get; }

    /// <summary>Reads the memory values. False if the reading does not succeed now.</summary>
    bool TryRead(out MemoryReading value);
}

/// <summary>
/// Memory collector. Publishes usage computed on "available" and not on "free": on Linux
/// the difference between the two is the difference between saying 50% and saying 99% on
/// the same relaxed machine.
/// </summary>
public sealed class MemoryCollector : IMetricCollector
{
    /// <summary>Total physical memory, in bytes.</summary>
    public const string TotalBytesMetricId = "memory.total.bytes";

    /// <summary>Memory available for new allocations, in bytes.</summary>
    public const string AvailableBytesMetricId = "memory.available.bytes";

    /// <summary>Memory in use, in bytes.</summary>
    public const string UsedBytesMetricId = "memory.used.bytes";

    /// <summary>Memory in use, in percentage points.</summary>
    public const string UsedPercentMetricId = "memory.used.percent";

    /// <summary>True when "available" is an estimate and not a measurement.</summary>
    public const string AvailableEstimatedMetricId = "memory.available.estimated";

    /// <summary>Total swap, in bytes. Absent on machines without swap.</summary>
    public const string SwapTotalMetricId = "memory.swap.total.bytes";

    /// <summary>Swap in use, in bytes. Absent on machines without swap.</summary>
    public const string SwapUsedMetricId = "memory.swap.used.bytes";

    private static readonly MetricDescriptor[] DescriptorList =
    [
        new(TotalBytesMetricId, "Total memory", MetricUnit.Bytes, IsPerInstance: false),
        new(AvailableBytesMetricId, "Available memory", MetricUnit.Bytes, IsPerInstance: false),
        new(UsedBytesMetricId, "Used memory", MetricUnit.Bytes, IsPerInstance: false),
        // "Memory usage" and not a second "Used memory": two rows with the SAME name
        // forced the projection to tell them apart by the unit symbol, and that symbol
        // is not the one read in the value - the row said "Used memory (B)" while on the
        // right there was "11.2 GiB", because sizes are shown scaled.
        // The name also pairs with "CPU usage", which is the same thing for the other panel.
        new(UsedPercentMetricId, "Memory usage", MetricUnit.Percent, IsPerInstance: false),
        // Spelled out: "Available is estimated" does not tell the reader WHAT it is about.
        // It is "Yes" when the system does not report available memory and it has to be deduced.
        new(AvailableEstimatedMetricId, "Available memory is an estimate", MetricUnit.None, IsPerInstance: false),
        new(SwapTotalMetricId, "Total swap", MetricUnit.Bytes, IsPerInstance: false),
        new(SwapUsedMetricId, "Used swap", MetricUnit.Bytes, IsPerInstance: false),
    ];

    private readonly IMemoryReadingProvider provider;

    /// <summary>Creates the collector over the given port.</summary>
    public MemoryCollector(IMemoryReadingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        this.provider = provider;
    }

    /// <inheritdoc />
    public string Id => "memory";

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

        if (!provider.TryRead(out MemoryReading reading))
        {
            return Degraded(CollectorStatus.Unavailable, "couldn't read the memory values");
        }

        List<MetricPoint> points =
        [
            MetricPoint.Measured(TotalBytesMetricId, null, MetricValue.FromNumber(reading.Total.Bytes)),
            MetricPoint.Measured(AvailableBytesMetricId, null, MetricValue.FromNumber(reading.Available.Bytes)),
            MetricPoint.Measured(UsedBytesMetricId, null, MetricValue.FromNumber(reading.Used.Bytes)),
        ];

        // A total of zero would make the percentage a division by zero: the point is omitted
        // instead of publishing a NaN, which on top of that is not valid JSON.
        if (reading.Total.Bytes > 0L
            && Percent.TryFromRatio((double)reading.Used.Bytes / reading.Total.Bytes, out Percent used))
        {
            points.Add(MetricPoint.Measured(UsedPercentMetricId, null, MetricValue.FromNumber(used.Points)));
        }

        // A machine without swap is a legitimate configuration, not a fault: the absence of
        // the points says "not applicable", while a zero would say "it is there and it is empty".
        if (reading.SwapTotal.Bytes > 0L)
        {
            points.Add(MetricPoint.Measured(
                SwapTotalMetricId,
                null,
                MetricValue.FromNumber(reading.SwapTotal.Bytes)));
            points.Add(MetricPoint.Measured(
                SwapUsedMetricId,
                null,
                MetricValue.FromNumber(reading.SwapTotal.SaturatingSubtract(reading.SwapFree).Bytes)));
        }

        // At the BOTTOM, and not among the quantities: it is the only row that is not a
        // measurement but a note about another row, and in the middle of the others it broke
        // the reading.
        points.Add(MetricPoint.Measured(
            AvailableEstimatedMetricId,
            null,
            MetricValue.FromFlag(reading.AvailableWasEstimated)));

        return new MetricSnapshot(Id, CollectorStatus.Ok, Message: null, points);
    }

    private MetricSnapshot Degraded(CollectorStatus status, string message) =>
        new(Id, status, message, []);
}