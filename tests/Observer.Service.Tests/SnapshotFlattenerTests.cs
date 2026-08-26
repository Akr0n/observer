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
    private static readonly DateTimeOffset Istante =
        new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions OpzioniWeb = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions OpzioniConNaN = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static MachineSnapshot ConUnPunto(MetricPoint punto) =>
        new(
            MachineSnapshot.CurrentSchemaVersion,
            Istante,
            [new MetricSnapshot("cpu", CollectorStatus.Ok, null, [punto])]);

    [Fact]
    public void Appiattisce_UnNumeroConLaTernaCompletaELIstanteDelloSnapshot()
    {
        MachineSnapshot snapshot = ConUnPunto(
            MetricPoint.Measured("cpu.usage.core", "core0", MetricValue.FromNumber(42.5d)));

        SeriesSample campione = Assert.Single(SnapshotFlattener.Flatten(snapshot));

        Assert.Equal("cpu", campione.Key.CollectorId);
        Assert.Equal("cpu.usage.core", campione.Key.MetricId);
        Assert.Equal("core0", campione.Key.Instance);
        Assert.Equal(42.5d, campione.Value);
        Assert.Equal(Istante.ToUnixTimeMilliseconds(), campione.TimestampMs);
    }

    [Fact]
    public void Appiattisce_UnIstanzaAssenteDiventaStringaVuotaNonNull()
    {
        // In SQLite due NULL non sono uguali dentro un indice UNIQUE. Con null qui, la
        // stessa serie verrebbe reinserita a ogni secondo: migliaia di serie da un punto
        // ciascuna, uno storico che non si puo' interrogare e un file che esplode. Non
        // fallisce niente: si vede solo aprendo il database.
        MachineSnapshot snapshot = ConUnPunto(
            MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(7d)));

        SeriesSample campione = Assert.Single(SnapshotFlattener.Flatten(snapshot));

        Assert.Equal(string.Empty, campione.Key.Instance);
    }

    [Fact]
    public void Appiattisce_UnFlagDiventaUnoOZero()
    {
        // Un flag conservato come 0/1 rende la media dell'intervallo leggibile: "vero per
        // meta' del minuto". Buttarlo via renderebbe invisibile in storico l'unica metrica
        // che conta davvero, il guasto SMART.
        MachineSnapshot snapshot = ConUnPunto(
            MetricPoint.Measured("smart.failing", "nvme0", MetricValue.FromFlag(true)));

        SeriesSample campione = Assert.Single(SnapshotFlattener.Flatten(snapshot));

        Assert.Equal(1d, campione.Value);
        Assert.Equal(MetricValueKind.Flag, campione.Kind);
    }

    [Fact]
    public void Appiattisce_IgnoraITestuali()
    {
        // Il modello di un disco non e' una serie temporale: e' una costante ripetuta una
        // volta al secondo. Metterla nello storico gonfia il file e non aggiunge nulla.
        MachineSnapshot snapshot = ConUnPunto(
            MetricPoint.Measured("disk.model", "nvme0", MetricValue.FromText("Samsung 990")));

        Assert.Empty(SnapshotFlattener.Flatten(snapshot));
    }

    [Theory]
    [InlineData(CollectorStatus.Unsupported)]
    [InlineData(CollectorStatus.Unavailable)]
    public void Appiattisce_IgnoraIPuntiSenzaValore(CollectorStatus stato)
    {
        // Un punto mancante NON deve diventare uno zero: nel grafico uno zero e' un dato,
        // un buco e' un buco. La differenza si vede solo se il buco resta un buco.
        MetricPoint punto = stato == CollectorStatus.Unsupported
            ? MetricPoint.Unsupported("cpu.temp", null, "niente sensore qui")
            : MetricPoint.Unavailable("cpu.temp", null, "driver non caricato");

        Assert.Empty(SnapshotFlattener.Flatten(ConUnPunto(punto)));
    }

    [Fact]
    public void Appiattisce_IgnoraUnValoreDiTipoSconosciuto()
    {
        // default(MetricValue) e' Kind=Unknown con Number=0: arriva da una
        // deserializzazione parziale, e scriverlo significherebbe registrare uno zero
        // perfettamente credibile per una metrica che non e' mai stata misurata.
        MetricValue valoreVuoto = JsonSerializer.Deserialize<MetricValue>("{}", OpzioniWeb);

        Assert.Equal(MetricValueKind.Unknown, valoreVuoto.Kind);
        Assert.Empty(SnapshotFlattener.Flatten(
            ConUnPunto(MetricPoint.Measured("cpu.usage.total", null, valoreVuoto))));
    }

    [Fact]
    public void Appiattisce_IgnoraUnNumeroNonFinito()
    {
        // MetricValue.FromNumber rifiuta i non finiti, ma un valore ARRIVATO da JSON no.
        // Un NaN che entrasse nel rollup farebbe lanciare il servizio di scrittura a ogni
        // giro, e lo storico si fermerebbe in silenzio mentre gli endpoint continuano a
        // rispondere.
        MetricValue valoreRotto = JsonSerializer.Deserialize<MetricValue>(
            """{"kind":1,"number":"NaN","text":null,"flag":false}""", OpzioniConNaN);

        Assert.Equal(MetricValueKind.Number, valoreRotto.Kind);
        Assert.Empty(SnapshotFlattener.Flatten(
            ConUnPunto(MetricPoint.Measured("cpu.usage.total", null, valoreRotto))));
    }

    [Fact]
    public void Appiattisce_TieneIPuntiSaniDeiCollectorSaniQuandoUnAltroEGuasto()
    {
        // La degradazione graziosa deve arrivare fino al disco: un collector rotto non deve
        // svuotare lo storico degli altri.
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            Istante,
            [
                new MetricSnapshot("smart", CollectorStatus.Faulted, "esploso", []),
                new MetricSnapshot("mem", CollectorStatus.Ok, null,
                    [MetricPoint.Measured("mem.used", null, MetricValue.FromNumber(1024d))]),
            ]);

        SeriesSample campione = Assert.Single(SnapshotFlattener.Flatten(snapshot));

        Assert.Equal("mem", campione.Key.CollectorId);
    }

    [Fact]
    public void Appiattisce_RifiutaUnoSnapshotNullo()
    {
        Assert.Throws<ArgumentNullException>(() => SnapshotFlattener.Flatten(null!));
    }
}
