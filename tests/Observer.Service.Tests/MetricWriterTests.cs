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
    private static DateTimeOffset T(string istanteIso) =>
        DateTimeOffset.Parse(istanteIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static MachineSnapshot Snapshot(string istanteIso, params MetricPoint[] punti) =>
        new(
            MachineSnapshot.CurrentSchemaVersion,
            T(istanteIso),
            [new MetricSnapshot("cpu", CollectorStatus.Ok, null, punti)]);

    [Fact]
    public void Svuota_ScriveSoloIValoriNumerici()
    {
        using TempMetricStore temporaneo = new();
        SnapshotBuffer coda = new(capacity: 8);
        MetricWriter scrittore = new(coda, temporaneo.Store);

        coda.Enqueue(Snapshot(
            "2026-08-26T12:00:00Z",
            MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(42d)),
            MetricPoint.Measured("cpu.model", null, MetricValue.FromText("Ryzen")),
            MetricPoint.Unavailable("cpu.temp", null, "nessun sensore")));

        Assert.Equal(1, scrittore.FlushPending());

        StoredSeries serie = Assert.Single(temporaneo.Store.ListSeries());
        Assert.Equal("cpu.usage.total", serie.Key.MetricId);
    }

    [Fact]
    public void Svuota_ScriveInUnaSolaVoltaTuttoCioCheSiEAccumulato()
    {
        using TempMetricStore temporaneo = new();
        SnapshotBuffer coda = new(capacity: 8);
        MetricWriter scrittore = new(coda, temporaneo.Store);

        coda.Enqueue(Snapshot("2026-08-26T12:00:00Z",
            MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(1d))));
        coda.Enqueue(Snapshot("2026-08-26T12:00:01Z",
            MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(2d))));

        // Una transazione per giro, non una per campione: con una transazione al secondo per
        // metrica il disco diventerebbe il collo di bottiglia del campionatore.
        Assert.Equal(2, scrittore.FlushPending());

        Assert.Equal(2, temporaneo.Store.ReadHistory(
            new SeriesKey("cpu", "cpu.usage.total", string.Empty),
            BucketWidths.RawSeconds,
            T("2026-08-26T12:00:00Z"),
            T("2026-08-26T12:01:00Z"),
            100).Count);
    }

    [Fact]
    public void Svuota_SuCodaVuotaNonScriveNulla()
    {
        using TempMetricStore temporaneo = new();
        SnapshotBuffer coda = new(capacity: 8);
        MetricWriter scrittore = new(coda, temporaneo.Store);

        Assert.Equal(0, scrittore.FlushPending());
    }

    [Fact]
    public void Svuota_NonRiscriveDueVolteLoStessoSnapshot()
    {
        using TempMetricStore temporaneo = new();
        SnapshotBuffer coda = new(capacity: 8);
        MetricWriter scrittore = new(coda, temporaneo.Store);

        coda.Enqueue(Snapshot("2026-08-26T12:00:00Z",
            MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(1d))));

        scrittore.FlushPending();

        // La coda deve restare svuotata: se lo svuotamento non consumasse davvero, ogni
        // giro riscriverebbe tutta la storia da capo e il file crescerebbe senza motivo.
        Assert.Equal(0, scrittore.FlushPending());
    }
}
