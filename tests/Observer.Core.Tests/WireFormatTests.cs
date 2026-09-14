using System.Text.Json;
using Observer.Core.Metrics;

namespace Observer.Core.Tests;

/// <summary>
/// The serialization boundary. It is the point where a defect shows up neither at compile time
/// nor by looking at the outgoing JSON: it shows up only by feeding back in what came out. A
/// value that serializes but does not deserialize back produces a client full of zeros marked
/// "Ok" — the most dangerous bug there can be for someone who cannot read the code.
/// </summary>
public class WireFormatTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static TheoryData<string, MetricValue> ValuesOfEveryKind() => new()
    {
        { "number", MetricValue.FromNumber(34122366976d) },
        { "text", MetricValue.FromText("Samsung 990") },
        { "flag", MetricValue.FromFlag(true) },
    };

    [Theory]
    [MemberData(nameof(ValuesOfEveryKind))]
    public void MetricValue_EveryKind_SurvivesTheRoundTrip(string metricId, MetricValue original)
    {
        // Iterating over ALL the kinds is not pedantry: the day someone adds a fourth
        // MetricValueKind without updating the serialization, this test fails on its own
        // instead of letting the value disappear in silence.
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [
                new MetricSnapshot(
                    "test",
                    CollectorStatus.Ok,
                    null,
                    [MetricPoint.Measured(metricId, null, original)]),
            ]);

        string json = JsonSerializer.Serialize(snapshot, Options);
        MachineSnapshot? roundTripped = JsonSerializer.Deserialize<MachineSnapshot>(json, Options);

        MetricValue value = roundTripped!.Collectors[0].Points[0].Value!.Value;

        Assert.Equal(original.Kind, value.Kind);
        Assert.Equal(original.Number, value.Number);
        Assert.Equal(original.Text, value.Text);
        Assert.Equal(original.Flag, value.Flag);
    }

    [Fact]
    public void MachineSnapshot_SurvivesTheRoundTrip_WithStatusAndInstance()
    {
        // Structure and diagnostics must survive the round trip just as the values do: a
        // degraded status lost in transport becomes an invisible fault.
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [
                new MetricSnapshot("cpu", CollectorStatus.Unsupported, "no ntdll here", []),
                new MetricSnapshot("smart", CollectorStatus.Ok, null,
                    [MetricPoint.Measured("smart.temp", "nvme0", MetricValue.FromNumber(41d))]),
            ]);

        string json = JsonSerializer.Serialize(snapshot, Options);
        MachineSnapshot roundTripped = JsonSerializer.Deserialize<MachineSnapshot>(json, Options)!;

        Assert.Equal(MachineSnapshot.CurrentSchemaVersion, roundTripped.SchemaVersion);
        Assert.Equal(DateTimeOffset.UnixEpoch, roundTripped.CapturedAt);
        Assert.Equal(CollectorStatus.Unsupported, roundTripped.Collectors[0].Status);
        Assert.Equal("no ntdll here", roundTripped.Collectors[0].Message);
        Assert.Equal("nvme0", roundTripped.Collectors[1].Points[0].Instance);
        Assert.Equal(41d, roundTripped.Collectors[1].Points[0].Value!.Value.Number);
    }

    [Fact]
    public void MachineSnapshot_CarriesTheSchemaVersionOnTheWire()
    {
        // Without a version on the wire, a client and a service built from different commits
        // diverge in silence with zeroed fields instead of with a readable message.
        MachineSnapshot snapshot = new(MachineSnapshot.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, []);

        string json = JsonSerializer.Serialize(snapshot, Options);

        Assert.Contains("schemaVersion", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void MetricValue_FromNumber_RejectsNonFiniteValues(double brokenValue)
    {
        // A NaN accepted here makes the serializer THROW later, and at that point it is not
        // one metric that is lost: the ENTIRE HTTP response is lost, all the others with it.
        // Better a noisy error right away, where you can see who produced it.
        Assert.Throws<ArgumentOutOfRangeException>(() => MetricValue.FromNumber(brokenValue));
    }
}
