using System.Text.Json;
using Observer.App.Services;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// The translation from a snapshot into rows on screen. This is where a degraded status can
/// silently turn into an innocent-looking zero, which is the worst defect for someone who
/// cannot read the code: a machine that looks idle when in fact nothing is being measured
/// at all.
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

        // 64.2 and not 64.3: "F1" rounds a half to even. On a CPU percentage the difference
        // does not matter, but it is worth writing it down instead of discovering it.
        // The dot as the decimal separator is deliberate: the executables run in invariant
        // globalization mode (see runtimeconfig.template.json).
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
        // ANY flag metric will do: memory.available.estimated no longer works as the example,
        // because that one gets a treatment of its own (see the tests below).
        IReadOnlyList<MetricGroupState> groups = Project(
            Ok("memory", MetricPoint.Measured("memory.swap.enabled", null, MetricValue.FromFlag(true))));

        Assert.Equal("Yes", Assert.Single(groups[0].Rows).Display);
    }

    [Fact]
    public void Project_WhenAvailableMemoryIsMeasured_DoesNotAddARowToSaySo()
    {
        // On Windows that flag is hardwired to false: that row would say "No" for ever, on
        // every Windows machine. A row that repeats the same answer endlessly teaches the
        // reader to skip it, and it would be skipped on the day it said something else too.
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
        // The case that flag exists for: on Linux, when the kernel does not expose MemAvailable,
        // the number is summed from free memory, buffers, cache and reclaimable memory. That is
        // not wrong, but it is not a measurement, and it must be said WHERE the number is read.
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
        // It is neither yes nor no: it is a fault, and a fault that disappears from the screen
        // is worse than one row too many.
        IReadOnlyList<MetricGroupState> groups = Project(Ok(
            "memory",
            MetricPoint.Unavailable("memory.available.estimated", null, "the reading failed")));

        MetricRowState row = Assert.Single(groups[0].Rows);

        Assert.Contains("failed", row.Display, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_WithACollectorInWarmup_ShowsTheExplanationAndDoesNotCallItAFault()
    {
        // Warmup at startup is normal: the second sample needed to work out the percentage is
        // not there yet. An empty panel here would be impossible to diagnose, and a red error
        // would be a lie.
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [new MetricSnapshot("cpu", CollectorStatus.Warmup, "first sample: the previous one is missing", [])]);

        MetricGroupState group = Assert.Single(SnapshotProjection.Project(snapshot, Catalog));

        Assert.Empty(group.Rows);
        Assert.Equal("first sample: the previous one is missing", group.Note);
        Assert.Equal(MetricSeverity.Warmup, group.Severity);
    }

    [Fact]
    public void Project_WithAnUnsupportedCollector_TellsItApartFromAFault()
    {
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [new MetricSnapshot("cpu", CollectorStatus.Unsupported, "no ntdll here", [])]);

        MetricGroupState group = Assert.Single(SnapshotProjection.Project(snapshot, Catalog));

        Assert.Equal(MetricSeverity.Unsupported, group.Severity);
        Assert.Equal("no ntdll here", group.Note);
    }

    [Fact]
    public void Project_WithAFaultedCollectorAndNoMessage_StillPutsASentence()
    {
        // An empty, silent panel is exactly what must not happen to someone who does not read
        // the logs.
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
            Ok("smart", MetricPoint.Unavailable("smart.temp", "nvme1", "the USB bridge does not forward SMART commands")));

        MetricRowState row = Assert.Single(groups[0].Rows);

        Assert.Equal("smart.temp (nvme1)", row.Label);
        Assert.Equal("the USB bridge does not forward SMART commands", row.Display);
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
        // This case cannot be built from the MetricPoint factories: it only arrives over the
        // wire, and it is exactly the defect the comments in Observer.Core fear. Showing "0"
        // here would mean a machine full of zeros marked "Ok".
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
        // kind = 0 means deserialization never reached the constructor: the number would be
        // zero and would look like a valid measurement.
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
        // If /metrics/catalog does not answer, the metrics must not disappear.
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
        // The keys are what updates the rows in place: if they changed on every pass, the
        // window would rebuild the list every second and flicker.
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
        // The memory collector calls both the bytes and the percentage "Used memory": two rows
        // with the same name and different numbers look like a contradiction.
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
        // Bytes per second are the new unit that disk activity brought in. Without a branch of
        // their own they fall into the generic format and the screen reads "449852 B/s", with
        // the byte formatter sitting right next to it doing nothing.
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