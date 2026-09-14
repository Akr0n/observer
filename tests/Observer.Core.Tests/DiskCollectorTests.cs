using Observer.Core.Metrics;
using Observer.Core.Metrics.Disk;
using Observer.Core.Units;

namespace Observer.Core.Tests;

/// <summary>
/// The disk collector, and the ways in which it could lie.
/// </summary>
/// <remarks>
/// It is the first PER-INSTANCE collector, and the difference matters: here a fault is not about
/// "the metric", it is about one volume only. Three disks, one of which will not answer, must
/// give two measurements and a reason, not three blanks.
/// </remarks>
public class DiskCollectorTests
{
    private static DiskReading Volume(string name, long totalBytes, long freeBytes) =>
        new(name, ByteSize.FromBytes(totalBytes), ByteSize.FromBytes(freeBytes));

    private static MetricSnapshot Collect(IDiskReadingProvider provider) =>
        new DiskCollector(provider).CollectAsync(CancellationToken.None).AsTask().Result;

    [Fact]
    public void EveryVolumeGivesFourPointsWithItsOwnInstance()
    {
        MetricSnapshot snapshot = Collect(new FakeProvider(
            [
                Volume("C:", 500_000_000_000L, 100_000_000_000L),
                Volume("D:", 1_000_000_000_000L, 900_000_000_000L),
            ]));

        Assert.Equal(CollectorStatus.Ok, snapshot.Status);
        Assert.Equal(8, snapshot.Points.Count);

        // The instance is what keeps two disks apart: without it the rows overwrite each other
        // and only one is left on screen, carrying the numbers of the last one read.
        Assert.Equal(4, snapshot.Points.Count(point => point.Instance == "C:"));
        Assert.Equal(4, snapshot.Points.Count(point => point.Instance == "D:"));
    }

    [Fact]
    public void UsedSpaceIsWhatIsNotFree()
    {
        MetricSnapshot snapshot = Collect(new FakeProvider([Volume("C:", 1000L, 250L)]));

        MetricPoint usedBytes = snapshot.Points.Single(
            point => point.MetricId == DiskCollector.UsedBytesMetricId);

        Assert.Equal(750d, usedBytes.Value!.Value.Number);

        MetricPoint usedPercent = snapshot.Points.Single(
            point => point.MetricId == DiskCollector.UsedPercentMetricId);

        Assert.Equal(75d, usedPercent.Value!.Value.Number);
    }

    [Fact]
    public void AVolumeWithMoreFreeThanTotalGivesNoAbsurdPercentage()
    {
        // It happens: on a volume with quotas or with reserved blocks the two numbers come from
        // different counters. A negative subtraction would give a negative percentage, which is
        // worse than a missing number because it still looks like a measurement.
        MetricSnapshot snapshot = Collect(new FakeProvider([Volume("C:", 1000L, 1200L)]));

        MetricPoint usedPercent = snapshot.Points.Single(
            point => point.MetricId == DiskCollector.UsedPercentMetricId);

        Assert.Equal(0d, usedPercent.Value!.Value.Number);
    }

    [Fact]
    public void AZeroSizedVolumeDoesNotDeclareItselfEmpty()
    {
        // Zero total bytes does NOT mean "there is all the space in the world": it means the
        // capacity is unknown. It happens on special mounts and on devices that unmount while
        // they are being read. Publishing 0% would be the most reassuring lie possible.
        MetricSnapshot snapshot = Collect(new FakeProvider([Volume("Z:", 0L, 0L)]));

        MetricPoint usedPercent = snapshot.Points.Single(
            point => point.MetricId == DiskCollector.UsedPercentMetricId);

        Assert.Equal(CollectorStatus.Unavailable, usedPercent.Status);
        Assert.Null(usedPercent.Value);
        Assert.Contains("size of zero", usedPercent.Message, StringComparison.Ordinal);

        // But capacity and free space stay published: they are measurements, even though they
        // read zero, and removing them would hide the fact that that volume exists.
        Assert.Equal(4, snapshot.Points.Count);
    }

    [Fact]
    public void NoVolumesIsNotAFailure()
    {
        // Inside a minimal container there may not be a single filesystem worth showing.
        // "Ok with zero points" and "I could not read" must stay distinguishable, otherwise
        // you go looking for a fault that is not there.
        MetricSnapshot snapshot = Collect(new FakeProvider([]));

        Assert.Equal(CollectorStatus.Ok, snapshot.Status);
        Assert.Empty(snapshot.Points);
        Assert.NotNull(snapshot.Message);
    }

    [Fact]
    public void APlatformThatCannotBeMeasuredSaysSoAndDoesNotVanish()
    {
        MetricSnapshot snapshot = Collect(
            new FakeProvider([], supported: false, reason: "this platform has no volumes"));

        Assert.Equal(CollectorStatus.Unsupported, snapshot.Status);
        Assert.Equal("this platform has no volumes", snapshot.Message);
    }

    [Fact]
    public void AFailedReadIsDistinctFromZeroVolumes()
    {
        MetricSnapshot snapshot = Collect(new FakeProvider([], readable: false));

        Assert.Equal(CollectorStatus.Unavailable, snapshot.Status);
        Assert.Empty(snapshot.Points);
    }

    [Fact]
    public void TheCatalogDeclaresAllFourMetricsAsPerInstance()
    {
        // If one of them were declared not-per-instance, the client would look it up only once
        // and would show a single disk, with nothing to signal it.
        DiskCollector collector = new(new FakeProvider([]));

        Assert.Equal(4, collector.Descriptors.Count);
        Assert.All(collector.Descriptors, descriptor => Assert.True(descriptor.IsPerInstance));
    }

    private sealed class FakeProvider(
        IReadOnlyList<DiskReading> readingsToReturn,
        bool supported = true,
        bool readable = true,
        string? reason = null) : IDiskReadingProvider
    {
        public bool IsSupported => supported;

        public string? UnsupportedReason => reason;

        public bool TryRead(out IReadOnlyList<DiskReading> readings)
        {
            readings = readingsToReturn;

            return readable;
        }
    }
}