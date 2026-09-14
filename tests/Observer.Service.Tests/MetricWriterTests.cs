using System.Globalization;
using Observer.Core.Metrics;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// Il collegamento fra la coda e il file. E' l'unico punto in cui si vede se la persistenza
/// e' davvero attaccata: tutto il resto puo' essere perfetto e non scrivere una riga.
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

        // Una transazione per giro, non una per campione: con una transazione al secondo per
        // metrica il disco diventerebbe il collo di bottiglia del campionatore.
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

        // La coda deve restare svuotata: se lo svuotamento non consumasse davvero, ogni
        // giro riscriverebbe tutta la storia da capo e il file crescerebbe senza motivo.
        Assert.Equal(0, writer.FlushPending());
    }
}
