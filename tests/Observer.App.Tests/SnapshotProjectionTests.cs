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

    private static readonly MetricCatalog Catalog = new(
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
    public void Project_WithAPercentage_FormatsItAndFillsTheBar()
    {
        IReadOnlyList<MetricGroupState> groups = Project(
            Ok("cpu", MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(64.25d))));

        MetricRowState row = Assert.Single(groups[0].Rows);

        Assert.Equal("CPU", groups[0].Title);
        Assert.Equal("CPU usage", row.Label);

        // 64.2 e non 64.3: "F1" arrotonda il mezzo al pari. Su una percentuale di CPU la
        // differenza e' irrilevante, ma vale la pena che sia scritta invece che scoperta.
        // Il punto come separatore decimale e' voluto: gli eseguibili girano in modalita'
        // di globalizzazione invariante (vedi runtimeconfig.template.json).
        Assert.Equal("64.2 %", row.Display);
        Assert.Equal(0.6425d, row.Fraction!.Value, precision: 6);
        Assert.Equal(MetricSeverity.Ok, row.Severity);
    }

    [Fact]
    public void Project_WithAByteValue_UsesBinaryPrefixes()
    {
        IReadOnlyList<MetricGroupState> groups = Project(
            Ok("memory", MetricPoint.Measured("memory.used.bytes", null, MetricValue.FromNumber(34122366976d))));

        MetricRowState row = Assert.Single(groups[0].Rows);

        Assert.Equal("Memory", groups[0].Title);
        Assert.Equal("31.8 GiB", row.Display);
        Assert.Null(row.Fraction);
    }

    [Fact]
    public void Project_WithAFlag_WritesItInWords()
    {
        // Una metrica a bandiera QUALUNQUE: memory.available.estimated non serve piu' come
        // esempio, perche' quella ha un trattamento suo (vedi i test qui sotto).
        IReadOnlyList<MetricGroupState> groups = Project(
            Ok("memory", MetricPoint.Measured("memory.swap.enabled", null, MetricValue.FromFlag(true))));

        Assert.Equal("Yes", Assert.Single(groups[0].Rows).Display);
    }

    [Fact]
    public void Project_WhenAvailableMemoryIsMeasured_DoesNotAddARowToSaySo()
    {
        // Su Windows quel flag e' cablato a falso: quella riga direbbe "No" per sempre, su
        // qualunque macchina Windows. Una riga che ripete all'infinito la stessa risposta
        // insegna a saltarla, e verrebbe saltata anche il giorno in cui dicesse altro.
        IReadOnlyList<MetricGroupState> groups = Project(Ok(
            "memory",
            MetricPoint.Measured("memory.available.bytes", null, MetricValue.FromNumber(17_179_869_184d)),
            MetricPoint.Measured("memory.available.estimated", null, MetricValue.FromFlag(false))));

        MetricRowState row = Assert.Single(groups[0].Rows);

        Assert.Equal("memory|memory.available.bytes|", row.Key);
        Assert.DoesNotContain("estimate", row.Display, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_WhenAvailableMemoryIsEstimated_SaysSoOnTheValue()
    {
        // Il caso per cui quel flag esiste: su Linux, se il kernel non espone MemAvailable,
        // il numero viene sommato da memoria libera, buffer, cache e memoria recuperabile.
        // Non e' sbagliato, ma non e' una misura, e va detto DOVE si legge il numero.
        IReadOnlyList<MetricGroupState> groups = Project(Ok(
            "memory",
            MetricPoint.Measured("memory.available.bytes", null, MetricValue.FromNumber(3_435_973_836d)),
            MetricPoint.Measured("memory.available.estimated", null, MetricValue.FromFlag(true))));

        MetricRowState row = Assert.Single(groups[0].Rows);

        Assert.Equal("memory|memory.available.bytes|", row.Key);
        Assert.EndsWith("(estimated)", row.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_WhenReadingTheFlagFailed_TheRowStays()
    {
        // Non e' ne' si' ne' no: e' un guasto, e un guasto che sparisce dallo schermo e'
        // peggio di una riga di troppo.
        IReadOnlyList<MetricGroupState> groups = Project(Ok(
            "memory",
            MetricPoint.Unavailable("memory.available.estimated", null, "the reading failed")));

        MetricRowState row = Assert.Single(groups[0].Rows);

        Assert.Contains("failed", row.Display, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_WithACollectorInWarmup_ShowsTheExplanationAndDoesNotCallItAFault()
    {
        // Il Warmup all'avvio e' normale: manca il secondo campione per calcolare la
        // percentuale. Un riquadro vuoto qui sarebbe indiagnosticabile, e un errore rosso
        // sarebbe una bugia.
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [new MetricSnapshot("cpu", CollectorStatus.Warmup, "primo campione: manca il precedente", [])]);

        MetricGroupState group = Assert.Single(SnapshotProjection.Project(snapshot, Catalog));

        Assert.Empty(group.Rows);
        Assert.Equal("primo campione: manca il precedente", group.Note);
        Assert.Equal(MetricSeverity.Warmup, group.Severity);
    }

    [Fact]
    public void Project_WithAnUnsupportedCollector_TellsItApartFromAFault()
    {
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [new MetricSnapshot("cpu", CollectorStatus.Unsupported, "niente ntdll qui", [])]);

        MetricGroupState group = Assert.Single(SnapshotProjection.Project(snapshot, Catalog));

        Assert.Equal(MetricSeverity.Unsupported, group.Severity);
        Assert.Equal("niente ntdll qui", group.Note);
    }

    [Fact]
    public void Project_WithAFaultedCollectorAndNoMessage_StillPutsASentence()
    {
        // Un riquadro vuoto e muto e' esattamente cio' che non deve capitare a chi non legge
        // i log.
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [new MetricSnapshot("cpu", CollectorStatus.Faulted, null, [])]);

        MetricGroupState group = Assert.Single(SnapshotProjection.Project(snapshot, Catalog));

        Assert.False(string.IsNullOrWhiteSpace(group.Note));
        Assert.Equal(MetricSeverity.Problem, group.Severity);
    }

    [Fact]
    public void Project_WithAnOkCollectorButNoPoints_DoesNotLeaveThePanelSilent()
    {
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [new MetricSnapshot("cpu", CollectorStatus.Ok, null, [])]);

        MetricGroupState group = Assert.Single(SnapshotProjection.Project(snapshot, Catalog));

        Assert.False(string.IsNullOrWhiteSpace(group.Note));
    }

    [Fact]
    public void Project_WithAnUnavailablePoint_ShowsTheMessageInsteadOfTheNumber()
    {
        IReadOnlyList<MetricGroupState> groups = Project(
            Ok("smart", MetricPoint.Unavailable("smart.temp", "nvme1", "il bridge USB non inoltra i comandi SMART")));

        MetricRowState row = Assert.Single(groups[0].Rows);

        Assert.Equal("smart.temp (nvme1)", row.Label);
        Assert.Equal("il bridge USB non inoltra i comandi SMART", row.Display);
        Assert.Null(row.Fraction);
        Assert.Equal(MetricSeverity.Problem, row.Severity);
    }

    [Fact]
    public void Project_WithAMetricOutsideTheCatalog_ShowsTheRawIdentifierInsteadOfVanishing()
    {
        IReadOnlyList<MetricGroupState> groups = Project(
            Ok("gpu", MetricPoint.Measured("gpu.temp", null, MetricValue.FromNumber(61d))));

        MetricRowState row = Assert.Single(groups[0].Rows);

        Assert.Equal("gpu.temp", row.Label);
        Assert.Equal("gpu", groups[0].Title);
        Assert.Equal("61", row.Display);
    }

    [Fact]
    public void Project_WithAnOkPointButNoValue_SaysSoInsteadOfShowingZero()
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

        MetricRowState row = Assert.Single(SnapshotProjection.Project(snapshot!, Catalog)[0].Rows);

        Assert.Equal(MetricSeverity.Problem, row.Severity);
        Assert.DoesNotContain("0", row.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_WithAValueOfUnknownKind_SaysSoInsteadOfShowingZero()
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

        MetricRowState row = Assert.Single(SnapshotProjection.Project(snapshot!, Catalog)[0].Rows);

        Assert.Equal(MetricSeverity.Problem, row.Severity);
        Assert.Contains("unrecognized", row.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_WithNoCatalog_ShowsEverythingWithRawIdentifiers()
    {
        // Se /metrics/catalog non risponde, le metriche non devono sparire.
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [
                new MetricSnapshot("cpu", CollectorStatus.Ok, null,
                    [MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(12d))]),
            ]);

        MetricRowState row = Assert.Single(SnapshotProjection.Project(snapshot, MetricCatalog.Empty)[0].Rows);

        Assert.Equal("cpu.usage.total", row.Label);
        Assert.Equal("12", row.Display);
        Assert.Null(row.Fraction);
    }

    [Fact]
    public void Project_RowKeysAreStableAcrossTwoReadings()
    {
        // Le chiavi servono ad aggiornare le righe sul posto: se cambiassero a ogni giro, la
        // finestra ricostruirebbe l'elenco ogni secondo e lampeggerebbe.
        MetricRowState before = Project(
            Ok("cpu", MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(10d))))[0].Rows[0];

        MetricRowState after = Project(
            Ok("cpu", MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(90d))))[0].Rows[0];

        Assert.Equal(before.Key, after.Key);
        Assert.NotEqual(before.Display, after.Display);
    }

    [Fact]
    public void Project_WithTwoMetricsSharingAName_TellsThemApartByTheUnit()
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

        IReadOnlyList<MetricRowState> rows = SnapshotProjection.Project(snapshot, Catalog)[0].Rows;

        Assert.Equal("Used memory (B)", rows[0].Label);
        Assert.Equal("Used memory (%)", rows[1].Label);
    }

    [Fact]
    public void Project_WithNamesAlreadyDistinct_AddsNothing()
    {
        MetricRowState row = Assert.Single(Project(
            Ok("cpu", MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(10d)))).Single().Rows);

        Assert.Equal("CPU usage", row.Label);
    }

    [Theory]
    [InlineData(0d, "0 B")]
    [InlineData(512d, "512 B")]
    [InlineData(1024d, "1.0 KiB")]
    [InlineData(1048576d, "1.0 MiB")]
    [InlineData(34122366976d, "31.8 GiB")]
    public void DescribeBytes_UsesBinaryPrefixes(double totalBytes, string expected) =>
        Assert.Equal(expected, MetricFormatting.DescribeBytes(totalBytes));

    [Theory]
    [InlineData(0d, "0 B/s")]
    [InlineData(449852d, "439.3 KiB/s")]
    [InlineData(1073741824d, "1.0 GiB/s")]
    public void ARateUsesTheSamePrefixesAsBytes(double perSecond, string expected) =>
        // I byte al secondo sono l'unita' nuova portata dall'attivita' dei dischi. Senza un
        // ramo suo finiscono nel formato generico e a schermo si legge "449852 B/s", con il
        // formattatore dei byte li' accanto a non fare niente.
        Assert.Equal(
            expected,
            MetricFormatting.Describe(MetricValue.FromNumber(perSecond), new MetricUnit("B/s")));

    private static IReadOnlyList<MetricGroupState> Project(MetricSnapshot collector) =>
        SnapshotProjection.Project(
            new MachineSnapshot(MachineSnapshot.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, [collector]),
            Catalog);

    private static MetricSnapshot Ok(string collectorId, MetricPoint point) =>
        new(collectorId, CollectorStatus.Ok, null, [point]);

    private static MetricSnapshot Ok(string collectorId, params MetricPoint[] points) =>
        new(collectorId, CollectorStatus.Ok, null, points);
}