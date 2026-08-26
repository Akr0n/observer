using System.Text.Json;
using Observer.App.Services;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// La traduzione da campionamento a righe di schermo. E' il punto in cui uno stato degradato
/// puo' diventare in silenzio uno zero dall'aria innocente, cioe' il difetto peggiore per chi
/// non puo' leggere il codice: una macchina che sembra a riposo mentre in realta' non si sta
/// misurando niente.
/// </summary>
public class SnapshotProjectionTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static readonly MetricCatalog Catalogo = new(
    [
        new CollectorCatalogEntry("cpu",
        [
            new MetricDescriptor("cpu.usage.total", "CPU usage", MetricUnit.Percent, IsPerInstance: false),
        ]),
        new CollectorCatalogEntry("memory",
        [
            new MetricDescriptor("memory.used.bytes", "Used memory", MetricUnit.Bytes, IsPerInstance: false),
            new MetricDescriptor("memory.used.percent", "Used memory", MetricUnit.Percent, IsPerInstance: false),
            new MetricDescriptor("memory.available.estimated", "Available is estimated", MetricUnit.None, IsPerInstance: false),
        ]),
    ]);

    [Fact]
    public void Project_ConUnaPercentuale_LaFormattaEriempieLaBarra()
    {
        IReadOnlyList<MetricGroupState> gruppi = Proietta(
            Ok("cpu", MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(64.25d))));

        MetricRowState riga = Assert.Single(gruppi[0].Rows);

        Assert.Equal("CPU", gruppi[0].Title);
        Assert.Equal("CPU usage", riga.Label);

        // 64.2 e non 64.3: "F1" arrotonda il mezzo al pari. Su una percentuale di CPU la
        // differenza e' irrilevante, ma vale la pena che sia scritta invece che scoperta.
        // Il punto come separatore decimale e' voluto: gli eseguibili girano in modalita'
        // di globalizzazione invariante (vedi runtimeconfig.template.json).
        Assert.Equal("64.2 %", riga.Display);
        Assert.Equal(0.6425d, riga.Fraction!.Value, precision: 6);
        Assert.Equal(MetricSeverity.Ok, riga.Severity);
    }

    [Fact]
    public void Project_ConUnValoreInByte_UsaIPrefissiBinari()
    {
        IReadOnlyList<MetricGroupState> gruppi = Proietta(
            Ok("memory", MetricPoint.Measured("memory.used.bytes", null, MetricValue.FromNumber(34122366976d))));

        MetricRowState riga = Assert.Single(gruppi[0].Rows);

        Assert.Equal("Memory", gruppi[0].Title);
        Assert.Equal("31.8 GiB", riga.Display);
        Assert.Null(riga.Fraction);
    }

    [Fact]
    public void Project_ConUnFlag_LoScriveAParole()
    {
        IReadOnlyList<MetricGroupState> gruppi = Proietta(
            Ok("memory", MetricPoint.Measured("memory.available.estimated", null, MetricValue.FromFlag(true))));

        Assert.Equal("Yes", Assert.Single(gruppi[0].Rows).Display);
    }

    [Fact]
    public void Project_ConCollectorInWarmup_MostraLaSpiegazioneENonLaChiamaGuasto()
    {
        // Il Warmup all'avvio e' normale: manca il secondo campione per calcolare la
        // percentuale. Un riquadro vuoto qui sarebbe indiagnosticabile, e un errore rosso
        // sarebbe una bugia.
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [new MetricSnapshot("cpu", CollectorStatus.Warmup, "primo campione: manca il precedente", [])]);

        MetricGroupState gruppo = Assert.Single(SnapshotProjection.Project(snapshot, Catalogo));

        Assert.Empty(gruppo.Rows);
        Assert.Equal("primo campione: manca il precedente", gruppo.Note);
        Assert.Equal(MetricSeverity.InAttesa, gruppo.Severity);
    }

    [Fact]
    public void Project_ConCollectorNonSupportato_LoDistingueDaUnGuasto()
    {
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [new MetricSnapshot("cpu", CollectorStatus.Unsupported, "niente ntdll qui", [])]);

        MetricGroupState gruppo = Assert.Single(SnapshotProjection.Project(snapshot, Catalogo));

        Assert.Equal(MetricSeverity.NonMisurabile, gruppo.Severity);
        Assert.Equal("niente ntdll qui", gruppo.Note);
    }

    [Fact]
    public void Project_ConCollectorDegradatoSenzaMessaggio_MetteComunqueUnaFrase()
    {
        // Un riquadro vuoto e muto e' esattamente cio' che non deve capitare a chi non legge
        // i log.
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [new MetricSnapshot("cpu", CollectorStatus.Faulted, null, [])]);

        MetricGroupState gruppo = Assert.Single(SnapshotProjection.Project(snapshot, Catalogo));

        Assert.False(string.IsNullOrWhiteSpace(gruppo.Note));
        Assert.Equal(MetricSeverity.Problema, gruppo.Severity);
    }

    [Fact]
    public void Project_ConCollectorOkMaSenzaPunti_NonLasciaIlRiquadroMuto()
    {
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [new MetricSnapshot("cpu", CollectorStatus.Ok, null, [])]);

        MetricGroupState gruppo = Assert.Single(SnapshotProjection.Project(snapshot, Catalogo));

        Assert.False(string.IsNullOrWhiteSpace(gruppo.Note));
    }

    [Fact]
    public void Project_ConPuntoDegradato_MostraIlMessaggioAlPostoDelNumero()
    {
        IReadOnlyList<MetricGroupState> gruppi = Proietta(
            Ok("smart", MetricPoint.Unavailable("smart.temp", "nvme1", "il bridge USB non inoltra i comandi SMART")));

        MetricRowState riga = Assert.Single(gruppi[0].Rows);

        Assert.Equal("smart.temp (nvme1)", riga.Label);
        Assert.Equal("il bridge USB non inoltra i comandi SMART", riga.Display);
        Assert.Null(riga.Fraction);
        Assert.Equal(MetricSeverity.Problema, riga.Severity);
    }

    [Fact]
    public void Project_ConMetricaFuoriCatalogo_MostraLidentificatoreGrezzoInvecediSparire()
    {
        IReadOnlyList<MetricGroupState> gruppi = Proietta(
            Ok("gpu", MetricPoint.Measured("gpu.temp", null, MetricValue.FromNumber(61d))));

        MetricRowState riga = Assert.Single(gruppi[0].Rows);

        Assert.Equal("gpu.temp", riga.Label);
        Assert.Equal("gpu", gruppi[0].Title);
        Assert.Equal("61", riga.Display);
    }

    [Fact]
    public void Project_ConPuntoOkMaSenzaValore_LoDiceInvecediMostrareZero()
    {
        // Questo caso non e' costruibile dalle fabbriche di MetricPoint: arriva solo dal filo,
        // ed e' proprio il difetto che i commenti di Observer.Core temono. Mostrare "0" qui
        // significherebbe una macchina piena di zeri marcati "Ok".
        MachineSnapshot? snapshot = JsonSerializer.Deserialize<MachineSnapshot>(
            """
            {"schemaVersion":1,"capturedAt":"2026-08-26T09:15:49.34Z","collectors":[
              {"collectorId":"cpu","status":1,"message":null,"points":[
                {"metricId":"cpu.usage.total","instance":null,"value":null,"status":1,"message":null}]}]}
            """,
            Wire);

        MetricRowState riga = Assert.Single(SnapshotProjection.Project(snapshot!, Catalogo)[0].Rows);

        Assert.Equal(MetricSeverity.Problema, riga.Severity);
        Assert.DoesNotContain("0", riga.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_ConValoreDiTipoSconosciuto_LoDiceInvecediMostrareZero()
    {
        // kind = 0 significa che la deserializzazione non ha agganciato il costruttore: il
        // numero sarebbe zero e sembrerebbe una misura valida.
        MachineSnapshot? snapshot = JsonSerializer.Deserialize<MachineSnapshot>(
            """
            {"schemaVersion":1,"capturedAt":"2026-08-26T09:15:49.34Z","collectors":[
              {"collectorId":"cpu","status":1,"message":null,"points":[
                {"metricId":"cpu.usage.total","instance":null,
                 "value":{"kind":0,"number":0,"text":null,"flag":false},
                 "status":1,"message":null}]}]}
            """,
            Wire);

        MetricRowState riga = Assert.Single(SnapshotProjection.Project(snapshot!, Catalogo)[0].Rows);

        Assert.Equal(MetricSeverity.Problema, riga.Severity);
        Assert.Contains("unrecognized", riga.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_SenzaCatalogo_MostraTuttoConGliIdentificatoriGrezzi()
    {
        // Se /metrics/catalog non risponde, le metriche non devono sparire.
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [
                new MetricSnapshot("cpu", CollectorStatus.Ok, null,
                    [MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(12d))]),
            ]);

        MetricRowState riga = Assert.Single(SnapshotProjection.Project(snapshot, MetricCatalog.Empty)[0].Rows);

        Assert.Equal("cpu.usage.total", riga.Label);
        Assert.Equal("12", riga.Display);
        Assert.Null(riga.Fraction);
    }

    [Fact]
    public void Project_LeChiaviDelleRigheSonoStabiliTraDueLetture()
    {
        // Le chiavi servono ad aggiornare le righe sul posto: se cambiassero a ogni giro, la
        // finestra ricostruirebbe l'elenco ogni secondo e lampeggerebbe.
        MetricRowState prima = Proietta(
            Ok("cpu", MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(10d))))[0].Rows[0];

        MetricRowState dopo = Proietta(
            Ok("cpu", MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(90d))))[0].Rows[0];

        Assert.Equal(prima.Key, dopo.Key);
        Assert.NotEqual(prima.Display, dopo.Display);
    }

    [Fact]
    public void Project_ConDueMetricheDalNomeIdentico_LeDistingueConLunita()
    {
        // Il collector della memoria chiama "Memoria usata" sia i byte sia la percentuale:
        // due righe con lo stesso nome e numeri diversi sembrano una contraddizione.
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [
                new MetricSnapshot("memory", CollectorStatus.Ok, null,
                [
                    MetricPoint.Measured("memory.used.bytes", null, MetricValue.FromNumber(1073741824d)),
                    MetricPoint.Measured("memory.used.percent", null, MetricValue.FromNumber(41d)),
                ]),
            ]);

        IReadOnlyList<MetricRowState> righe = SnapshotProjection.Project(snapshot, Catalogo)[0].Rows;

        Assert.Equal("Used memory (B)", righe[0].Label);
        Assert.Equal("Used memory (%)", righe[1].Label);
    }

    [Fact]
    public void Project_ConNomiGiaDistinti_NonAggiungeNulla()
    {
        MetricRowState riga = Assert.Single(Proietta(
            Ok("cpu", MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(10d)))).Single().Rows);

        Assert.Equal("CPU usage", riga.Label);
    }

    [Theory]
    [InlineData(0d, "0 B")]
    [InlineData(512d, "512 B")]
    [InlineData(1024d, "1.0 KiB")]
    [InlineData(1048576d, "1.0 MiB")]
    [InlineData(34122366976d, "31.8 GiB")]
    public void DescribeBytes_UsaIPrefissiBinari(double byteTotali, string atteso) =>
        Assert.Equal(atteso, MetricFormatting.DescribeBytes(byteTotali));

    private static IReadOnlyList<MetricGroupState> Proietta(MetricSnapshot collector) =>
        SnapshotProjection.Project(
            new MachineSnapshot(MachineSnapshot.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, [collector]),
            Catalogo);

    private static MetricSnapshot Ok(string collectorId, MetricPoint punto) =>
        new(collectorId, CollectorStatus.Ok, null, [punto]);
}
