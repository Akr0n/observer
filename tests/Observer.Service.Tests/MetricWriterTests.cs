using System.Globalization;
using Observer.Core.Metrics;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// The link between the queue and the file. It is the only place where you can see whether
/// persistence is really wired up: everything else can be perfect and still not write a row.
/// </summary>
public class MetricWriterTests
{
    private static DateTimeOffset T(string isoInstant) =>
        DateTimeOffset.Parse(isoInstant, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static MachineSnapshot Snapshot(string isoInstant, params MetricPoint[] points) =>
        new(
            MachineSnapshot.CurrentSchemaVersion,
            T(isoInstant),
            [new MetricSnapshot("cpu", CollectorStatus.Ok, null, points)]);

    [Fact]
    public void FlushPending_WritesOnlyNumericValues()
    {
        using TempMetricStore temp = new();
        SnapshotBuffer buffer = new(capacity: 8);
        MetricWriter writer = new(buffer, temp.Store);

        buffer.Enqueue(Snapshot(
            "2026-08-26T12:00:00Z",
            MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(42d)),
            MetricPoint.Measured("cpu.model", null, MetricValue.FromText("Ryzen")),
            MetricPoint.Unavailable("cpu.temp", null, "nessun sensore")));

        Assert.Equal(1, writer.FlushPending());

        StoredSeries series = Assert.Single(temp.Store.ListSeries());
        Assert.Equal("cpu.usage.total", series.Key.MetricId);
    }

    [Fact]
    public void FlushPending_WritesEverythingAccumulatedInOnePass()
    {
        using TempMetricStore temp = new();
        SnapshotBuffer buffer = new(capacity: 8);
        MetricWriter writer = new(buffer, temp.Store);

        buffer.Enqueue(Snapshot("2026-08-26T12:00:00Z",
            MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(1d))));
        buffer.Enqueue(Snapshot("2026-08-26T12:00:01Z",
            MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(2d))));

        // One transaction per pass, not one per sample: with one transaction a second per
        // metric the disk would become the sampler's bottleneck.
        Assert.Equal(2, writer.FlushPending());

        Assert.Equal(2, temp.Store.ReadHistory(
            new SeriesKey("cpu", "cpu.usage.total", string.Empty),
            BucketWidths.RawSeconds,
            T("2026-08-26T12:00:00Z"),
            T("2026-08-26T12:01:00Z"),
            100).Count);
    }

    [Fact]
    public void FlushPending_OnAnEmptyBufferWritesNothing()
    {
        using TempMetricStore temp = new();
        SnapshotBuffer buffer = new(capacity: 8);
        MetricWriter writer = new(buffer, temp.Store);

        Assert.Equal(0, writer.FlushPending());
    }

    [Fact]
    public void FlushPending_DoesNotWriteTheSameSnapshotTwice()
    {
        using TempMetricStore temp = new();
        SnapshotBuffer buffer = new(capacity: 8);
        MetricWriter writer = new(buffer, temp.Store);

        buffer.Enqueue(Snapshot("2026-08-26T12:00:00Z",
            MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(1d))));

        writer.FlushPending();

        // The queue must stay drained: if the flush did not really consume it, every pass
        // would rewrite the whole history from scratch and the file would grow for nothing.
        Assert.Equal(0, writer.FlushPending());
    }
}
