using System.Text.Json;
using System.Text.Json.Serialization;
using Observer.Core.Metrics;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// What goes into the history and what does not. The risk here is writing a zero in place of
/// missing data: an invented zero in a CPU chart is indistinguishable from an idle machine,
/// and nobody ever finds out.
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
        // In SQLite two NULLs are not equal inside a UNIQUE index. With null here, the same
        // series would be reinserted every second: thousands of one-point series, a history
        // that cannot be queried and a file that blows up. Nothing fails: you only see it
        // by opening the database.
        MachineSnapshot snapshot = WithOnePoint(
            MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(7d)));

        SeriesSample sample = Assert.Single(SnapshotFlattener.Flatten(snapshot));

        Assert.Equal(string.Empty, sample.Key.Instance);
    }

    [Fact]
    public void Flattens_AFlagBecomesOneOrZero()
    {
        // A flag kept as 0/1 makes the average over the interval readable: "true for half
        // the minute". Throwing it away would hide from the history the one metric that
        // really matters, the SMART failure.
        MachineSnapshot snapshot = WithOnePoint(
            MetricPoint.Measured("smart.failing", "nvme0", MetricValue.FromFlag(true)));

        SeriesSample sample = Assert.Single(SnapshotFlattener.Flatten(snapshot));

        Assert.Equal(1d, sample.Value);
        Assert.Equal(MetricValueKind.Flag, sample.Kind);
    }

    [Fact]
    public void Flattens_IgnoresTextValues()
    {
        // A disk model is not a time series: it is a constant repeated once a second.
        // Putting it in the history bloats the file and adds nothing.
        MachineSnapshot snapshot = WithOnePoint(
            MetricPoint.Measured("disk.model", "nvme0", MetricValue.FromText("Samsung 990")));

        Assert.Empty(SnapshotFlattener.Flatten(snapshot));
    }

    [Theory]
    [InlineData(CollectorStatus.Unsupported)]
    [InlineData(CollectorStatus.Unavailable)]
    public void Flattens_IgnoresPointsWithNoValue(CollectorStatus status)
    {
        // A missing point must NOT become a zero: in the chart a zero is data, a gap is a
        // gap. The difference only shows if the gap stays a gap.
        MetricPoint point = status == CollectorStatus.Unsupported
            ? MetricPoint.Unsupported("cpu.temp", null, "no sensor here")
            : MetricPoint.Unavailable("cpu.temp", null, "driver not loaded");

        Assert.Empty(SnapshotFlattener.Flatten(WithOnePoint(point)));
    }

    [Fact]
    public void Flattens_IgnoresAValueOfUnknownKind()
    {
        // default(MetricValue) is Kind=Unknown with Number=0: it comes from a partial
        // deserialization, and writing it would mean recording a perfectly believable zero
        // for a metric that was never measured.
        MetricValue emptyValue = JsonSerializer.Deserialize<MetricValue>("{}", WebOptions);

        Assert.Equal(MetricValueKind.Unknown, emptyValue.Kind);
        Assert.Empty(SnapshotFlattener.Flatten(
            WithOnePoint(MetricPoint.Measured("cpu.usage.total", null, emptyValue))));
    }

    [Fact]
    public void Flattens_IgnoresANonFiniteNumber()
    {
        // MetricValue.FromNumber rejects non-finite numbers, but a value that ARRIVED from
        // JSON does not. A NaN that got into the rollup would make the writing service throw
        // on every round, and the history would stop silently while the endpoints keep
        // answering.
        MetricValue brokenValue = JsonSerializer.Deserialize<MetricValue>(
            """{"kind":1,"number":"NaN","text":null,"flag":false}""", OptionsAllowingNaN);

        Assert.Equal(MetricValueKind.Number, brokenValue.Kind);
        Assert.Empty(SnapshotFlattener.Flatten(
            WithOnePoint(MetricPoint.Measured("cpu.usage.total", null, brokenValue))));
    }

    [Fact]
    public void Flattens_KeepsThePointsOfHealthyCollectorsWhenAnotherIsFaulted()
    {
        // Graceful degradation has to reach all the way to the disk: a broken collector must
        // not empty the history of the others.
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            Instant,
            [
                new MetricSnapshot("smart", CollectorStatus.Faulted, "crashed", []),
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
