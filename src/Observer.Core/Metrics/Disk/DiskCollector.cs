namespace Observer.Core.Metrics.Disk;

/// <summary>
/// The space used on the mounted volumes.
/// </summary>
/// <remarks>
/// It is the first <b>per-instance</b> collector: one volume, one instance. The per-instance
/// dimension is a field of the point and not a hierarchy of types, so nothing new is needed here —
/// it was already provided for, and this is the first source that really uses it.
/// <para>
/// It measures SPACE and not read and write activity. That is not an oversight: activity is read
/// with <c>DeviceIoControl</c> and the marshalling of a struct, and it brings in traps that have
/// to be faced calmly — among others, that the percentage of busy time is computed from IDLE time
/// and not by summing read and write times, which overlap in the queue and on one and the same
/// window gave 843%.
/// </para>
/// <para>
/// There is not even a distinction between solid state and mechanical disk, and that is a choice:
/// it was measured that it <b>lies</b>. In a virtual machine four disks declare themselves
/// mechanical while the physical medium is NVMe, and behind a USB adapter the question does not
/// get through at all. A column that says "mechanical" on an SSD is worse than a column that is
/// not there.
/// </para>
/// </remarks>
public sealed class DiskCollector : IMetricCollector
{
    /// <summary>Capacity of the volume.</summary>
    public const string TotalBytesMetricId = "disk.total.bytes";

    /// <summary>Space still writable.</summary>
    public const string FreeBytesMetricId = "disk.free.bytes";

    /// <summary>Space used.</summary>
    public const string UsedBytesMetricId = "disk.used.bytes";

    /// <summary>How full the volume is, as a percentage.</summary>
    public const string UsedPercentMetricId = "disk.used.percent";

    private static readonly IReadOnlyList<MetricDescriptor> DescriptorList =
    [
        new(TotalBytesMetricId, "Volume size", MetricUnit.Bytes, IsPerInstance: true),
        new(FreeBytesMetricId, "Free space", MetricUnit.Bytes, IsPerInstance: true),
        new(UsedBytesMetricId, "Used space", MetricUnit.Bytes, IsPerInstance: true),
        new(UsedPercentMetricId, "Disk usage", MetricUnit.Percent, IsPerInstance: true),
    ];

    private readonly IDiskReadingProvider provider;

    /// <summary>Creates the collector on top of the given port.</summary>
    /// <param name="provider">Where the volumes are read from.</param>
    public DiskCollector(IDiskReadingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        this.provider = provider;
    }

    /// <inheritdoc />
    public string Id => "disk";

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
            return new MetricSnapshot(
                Id,
                CollectorStatus.Unsupported,
                provider.UnsupportedReason ?? "source not supported on this platform",
                []);
        }

        if (!provider.TryRead(out IReadOnlyList<DiskReading> readings))
        {
            return new MetricSnapshot(
                Id,
                CollectorStatus.Unavailable,
                "the list of mounted volumes could not be read",
                []);
        }

        // No volume is NOT a fault, and it matters not to call it one: inside a minimal
        // container there may be not a single filesystem worth showing. "Ok with zero points"
        // and "I could not read" must stay distinguishable.
        if (readings.Count == 0)
        {
            return new MetricSnapshot(
                Id,
                CollectorStatus.Ok,
                "no mounted volume worth reporting on this machine",
                []);
        }

        List<MetricPoint> points = new(readings.Count * 4);

        foreach (DiskReading reading in readings)
        {
            points.Add(MetricPoint.Measured(
                TotalBytesMetricId, reading.Instance, MetricValue.FromNumber(reading.Total.Bytes)));

            points.Add(MetricPoint.Measured(
                FreeBytesMetricId, reading.Instance, MetricValue.FromNumber(reading.Free.Bytes)));

            points.Add(MetricPoint.Measured(
                UsedBytesMetricId, reading.Instance, MetricValue.FromNumber(reading.Used.Bytes)));

            // The percentage is the only one that can be MISSING on a volume that exists: the
            // capacity comes out as zero from special mounts and from devices that unmount while
            // they are being read. It is declared unavailable on that volume alone, with the
            // reason, instead of publishing a zero that would read as "empty".
            points.Add(reading.Fraction is { } fraction
                ? MetricPoint.Measured(
                    UsedPercentMetricId,
                    reading.Instance,
                    MetricValue.FromNumber(fraction * 100d))
                : MetricPoint.Unavailable(
                    UsedPercentMetricId,
                    reading.Instance,
                    "the volume reports a size of zero, so how full it is cannot be computed"));
        }

        return new MetricSnapshot(Id, CollectorStatus.Ok, null, points);
    }
}