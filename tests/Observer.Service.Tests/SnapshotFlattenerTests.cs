using System.Text.Json;
using System.Text.Json.Serialization;
using Observer.Core.Metrics;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// Cosa entra nello storico e cosa no. Il rischio qui e' la scrittura di uno zero al posto
/// di un dato mancante: uno zero inventato in un grafico di CPU non si distingue da una
/// macchina scarica, e nessuno lo scopre mai.
/// </summary>
public class SnapshotFlattenerTests
{
    private static readonly DateTimeOffset Instant =
        new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions OptionsAllowingNaN = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static MachineSnapshot WithOnePoint(MetricPoint point) =>
        new(
            MachineSnapshot.CurrentSchemaVersion,
            Instant,
            [new MetricSnapshot("cpu", CollectorStatus.Ok, null, [point])]);

    [Fact]
    public void Flattens_ANumberWithTheFullKeyAndTheSnapshotInstant()
    {
        MachineSnapshot snapshot = WithOnePoint(
            MetricPoint.Measured("cpu.usage.core", "core0", MetricValue.FromNumber(42.5d)));

        SeriesSample sample = Assert.Single(SnapshotFlattener.Flatten(snapshot));

        Assert.Equal("cpu", sample.Key.CollectorId);
        Assert.Equal("cpu.usage.core", sample.Key.MetricId);
        Assert.Equal("core0", sample.Key.Instance);
        Assert.Equal(42.5d, sample.Value);
        Assert.Equal(Instant.ToUnixTimeMilliseconds(), sample.TimestampMs);
    }

    [Fact]
    public void Flattens_AMissingInstanceBecomesAnEmptyStringNotNull()
    {
        // In SQLite due NULL non sono uguali dentro un indice UNIQUE. Con null qui, la
        // stessa serie verrebbe reinserita a ogni secondo: migliaia di serie da un punto
        // ciascuna, uno storico che non si puo' interrogare e un file che esplode. Non
        // fallisce niente: si vede solo aprendo il database.
        MachineSnapshot snapshot = WithOnePoint(
            MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(7d)));

        SeriesSample sample = Assert.Single(SnapshotFlattener.Flatten(snapshot));

        Assert.Equal(string.Empty, sample.Key.Instance);
    }

    [Fact]
    public void Flattens_AFlagBecomesOneOrZero()
    {
        // Un flag conservato come 0/1 rende la media dell'intervallo leggibile: "vero per
        // meta' del minuto". Buttarlo via renderebbe invisibile in storico l'unica metrica
        // che conta davvero, il guasto SMART.
        MachineSnapshot snapshot = WithOnePoint(
            MetricPoint.Measured("smart.failing", "nvme0", MetricValue.FromFlag(true)));

        SeriesSample sample = Assert.Single(SnapshotFlattener.Flatten(snapshot));

        Assert.Equal(1d, sample.Value);
        Assert.Equal(MetricValueKind.Flag, sample.Kind);
    }

    [Fact]
    public void Flattens_IgnoresTextValues()
    {
        // Il modello di un disco non e' una serie temporale: e' una costante ripetuta una
        // volta al secondo. Metterla nello storico gonfia il file e non aggiunge nulla.
        MachineSnapshot snapshot = WithOnePoint(
            MetricPoint.Measured("disk.model", "nvme0", MetricValue.FromText("Samsung 990")));

        Assert.Empty(SnapshotFlattener.Flatten(snapshot));
    }

    [Theory]
    [InlineData(CollectorStatus.Unsupported)]
    [InlineData(CollectorStatus.Unavailable)]
    public void Flattens_IgnoresPointsWithNoValue(CollectorStatus status)
    {
        // Un punto mancante NON deve diventare uno zero: nel grafico uno zero e' un dato,
        // un buco e' un buco. La differenza si vede solo se il buco resta un buco.
        MetricPoint point = status == CollectorStatus.Unsupported
            ? MetricPoint.Unsupported("cpu.temp", null, "niente sensore qui")
            : MetricPoint.Unavailable("cpu.temp", null, "driver non caricato");

        Assert.Empty(SnapshotFlattener.Flatten(WithOnePoint(point)));
    }

    [Fact]
    public void Flattens_IgnoresAValueOfUnknownKind()
    {
        // default(MetricValue) e' Kind=Unknown con Number=0: arriva da una
        // deserializzazione parziale, e scriverlo significherebbe registrare uno zero
        // perfettamente credibile per una metrica che non e' mai stata misurata.
        MetricValue emptyValue = JsonSerializer.Deserialize<MetricValue>("{}", WebOptions);

        Assert.Equal(MetricValueKind.Unknown, emptyValue.Kind);
        Assert.Empty(SnapshotFlattener.Flatten(
            WithOnePoint(MetricPoint.Measured("cpu.usage.total", null, emptyValue))));
    }

    [Fact]
    public void Flattens_IgnoresANonFiniteNumber()
    {
        // MetricValue.FromNumber rifiuta i non finiti, ma un valore ARRIVATO da JSON no.
        // Un NaN che entrasse nel rollup farebbe lanciare il servizio di scrittura a ogni
        // giro, e lo storico si fermerebbe in silenzio mentre gli endpoint continuano a
        // rispondere.
        MetricValue brokenValue = JsonSerializer.Deserialize<MetricValue>(
            """{"kind":1,"number":"NaN","text":null,"flag":false}""", OptionsAllowingNaN);

        Assert.Equal(MetricValueKind.Number, brokenValue.Kind);
        Assert.Empty(SnapshotFlattener.Flatten(
            WithOnePoint(MetricPoint.Measured("cpu.usage.total", null, brokenValue))));
    }

    [Fact]
    public void Flattens_KeepsThePointsOfHealthyCollectorsWhenAnotherIsFaulted()
    {
        // La degradazione graziosa deve arrivare fino al disco: un collector rotto non deve
        // svuotare lo storico degli altri.
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            Instant,
            [
                new MetricSnapshot("smart", CollectorStatus.Faulted, "esploso", []),
                new MetricSnapshot("mem", CollectorStatus.Ok, null,
                    [MetricPoint.Measured("mem.used", null, MetricValue.FromNumber(1024d))]),
            ]);

        SeriesSample sample = Assert.Single(SnapshotFlattener.Flatten(snapshot));

        Assert.Equal("mem", sample.Key.CollectorId);
    }

    [Fact]
    public void Flattens_RejectsANullSnapshot()
    {
        Assert.Throws<ArgumentNullException>(() => SnapshotFlattener.Flatten(null!));
    }
}
