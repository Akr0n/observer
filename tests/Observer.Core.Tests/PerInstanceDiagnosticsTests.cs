using System.Text.Json;
using Observer.Core.Metrics;

namespace Observer.Core.Tests;

/// <summary>
/// The proof of the "measure any parameter" requirement. The SMART collector below is defined
/// ENTIRELY in this test file: if making it work took modifying even a single file in
/// Observer.Core, the design would have lost its main property.
/// </summary>
/// <remarks>
/// The case chosen is the one that breaks a design with a single state per collector: three
/// disks, one of them behind a USB bridge that does not forward SMART commands. With one state
/// per collector the only choices would be to declare everything Ok, silently making the
/// problem disk disappear, or to declare everything Unavailable, losing the healthy disks too.
/// Both are forbidden: the first hides a fault, the second degrades more than it should.
/// </remarks>
public class PerInstanceDiagnosticsTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task MixedDisks_HealthyDiskReportsTheValueAndProblemDiskReportsTheReason()
    {
        SmartCollector collector = new();

        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

        // The collector as a whole works: only one of the disks cannot be read.
        Assert.Equal(CollectorStatus.Ok, snapshot.Status);

        MetricPoint healthy = Assert.Single(
            snapshot.Points,
            p => p.MetricId == "smart.temperature" && p.Instance == "nvme0");
        Assert.Equal(CollectorStatus.Ok, healthy.Status);
        Assert.Equal(41d, healthy.Value!.Value.Number);

        MetricPoint problematic = Assert.Single(
            snapshot.Points,
            p => p.MetricId == "smart.temperature" && p.Instance == "sdb");
        Assert.Equal(CollectorStatus.Unsupported, problematic.Status);
        Assert.Null(problematic.Value);
        Assert.Contains("USB", problematic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MixedValuesOnTheSameInstance_NumberTextAndFlagCoexist()
    {
        // SMART emits a number, a piece of text and a boolean for the same disk. If the
        // vocabulary could not carry all three, a second transport format would be needed.
        SmartCollector collector = new();

        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(
            MetricValueKind.Number,
            snapshot.Points.Single(p => p.MetricId == "smart.temperature" && p.Instance == "nvme0").Value!.Value.Kind);
        Assert.Equal(
            MetricValueKind.Text,
            snapshot.Points.Single(p => p.MetricId == "smart.model").Value!.Value.Kind);
        Assert.Equal(
            MetricValueKind.Flag,
            snapshot.Points.Single(p => p.MetricId == "smart.failurePredicted").Value!.Value.Kind);
    }

    [Fact]
    public async Task PerInstanceDiagnostics_SurvivesTheTransport()
    {
        // A reason that gets lost in transport is an invisible fault: the client would see
        // a point with no value and no explanation, which is a silent gap.
        SmartCollector collector = new();
        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);
        MachineSnapshot machine = new(MachineSnapshot.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, [snapshot]);

        string json = JsonSerializer.Serialize(machine, Options);
        MachineSnapshot roundTripped = JsonSerializer.Deserialize<MachineSnapshot>(json, Options)!;

        MetricPoint problematic = roundTripped.Collectors[0].Points
            .Single(p => p.MetricId == "smart.temperature" && p.Instance == "sdb");

        Assert.Equal(CollectorStatus.Unsupported, problematic.Status);
        Assert.Null(problematic.Value);
        Assert.Contains("USB", problematic.Message, StringComparison.Ordinal);

        MetricPoint healthy = roundTripped.Collectors[0].Points
            .Single(p => p.MetricId == "smart.temperature" && p.Instance == "nvme0");
        Assert.Equal(41d, healthy.Value!.Value.Number);
    }

    [Fact]
    public void NewUnitOfMeasure_NeedsNoChangeToTheCore()
    {
        // The degree Celsius is not among the predefined units, and you should not have to
        // add it there: MetricUnit is an open type precisely for this.
        MetricUnit celsius = new("degC");

        Assert.Equal("degC", celsius.Symbol);
    }

    /// <summary>
    /// Fake SMART source, written out in full right here. That it compiles without modifying
    /// any file in Observer.Core IS the demonstration that the extension point holds: no
    /// interface widened, no enum extended, no case added elsewhere.
    /// </summary>
    private sealed class SmartCollector : IMetricCollector
    {
        private static readonly MetricDescriptor[] DescriptorList =
        [
            new("smart.temperature", "Disk temperature", new MetricUnit("degC"), IsPerInstance: true),
            new("smart.model", "Disk model", MetricUnit.None, IsPerInstance: true),
            new("smart.failurePredicted", "Failure predicted", MetricUnit.None, IsPerInstance: true),
        ];

        public string Id => "smart";

        public IReadOnlyList<MetricDescriptor> Descriptors => DescriptorList;

        public ValueTask<MetricSnapshot> CollectAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MetricSnapshot(
                Id,
                CollectorStatus.Ok,
                Message: null,
                [
                    MetricPoint.Measured("smart.temperature", "nvme0", MetricValue.FromNumber(41d)),
                    MetricPoint.Measured("smart.model", "nvme0", MetricValue.FromText("Samsung 990 PRO")),
                    MetricPoint.Measured("smart.failurePredicted", "nvme0", MetricValue.FromFlag(false)),
                    MetricPoint.Unsupported(
                        "smart.temperature",
                        "sdb",
                        "the USB bridge does not forward SMART commands to this device"),
                ]));
    }
}
