using Observer.Core.Metrics;
using Observer.Core.Metrics.Disk;
using Observer.Core.Units;

namespace Observer.Core.Tests;

/// <summary>
/// The arithmetic of the read and write rates.
/// </summary>
/// <remarks>
/// This is the first metric that measures a SPEED, and it changes the rules. Disk space is
/// read and published as it is; bytes per second exist only as the difference between two
/// samples divided by the time elapsed in between, and every way that division can go wrong
/// would produce a number that is credible and false.
/// <para>
/// The rule nobody guesses: the busy time percentage is NOT obtained by summing read time
/// and write time. The two queues overlap, and over a single sampling window that sum has
/// already given 843%. It is derived from the IDLE time on Windows and from the busy ticks
/// on Linux, which are the same quantity seen from both sides.
/// </para>
/// </remarks>
public class DiskActivityRatesTests
{
    private static DiskActivityReading ReadingWithIdleTime(ulong bytesRead, ulong bytesWritten, double idleSeconds) =>
        DiskActivityReading.WithIdleTime("Disk 0", bytesRead, bytesWritten, TimeSpan.FromSeconds(idleSeconds));

    private static DiskActivityReading ReadingWithBusyTime(ulong bytesRead, ulong bytesWritten, double busySeconds) =>
        DiskActivityReading.WithBusyTime("sda", bytesRead, bytesWritten, TimeSpan.FromSeconds(busySeconds));

    [Fact]
    public void TheRateIsTheDeltaDividedByTheTime()
    {
        Assert.True(DiskActivityRates.TryComputeBytesPerSecond(
            1_000UL, 3_000UL, TimeSpan.FromSeconds(2), out double rate, out _));

        Assert.Equal(1_000d, rate);
    }

    [Fact]
    public void ACounterThatGoesBackwardsProducesNoRate()
    {
        // Suspend, resume, a disk unplugged and plugged back in, a virtual machine
        // migration. The delta computed on ulong would give a huge and plausible number.
        Assert.False(DiskActivityRates.TryComputeBytesPerSecond(
            3_000UL, 1_000UL, TimeSpan.FromSeconds(2), out _, out SampleFailure error));

        Assert.Equal(SampleFailure.CounterWentBackwards, error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WithNoElapsedTimeNothingIsComputed(int seconds)
    {
        // A division by zero here would not give an error: it would give infinity, and
        // MetricValue.FromNumber would throw, wiping out the ENTIRE HTTP response because of
        // one single disk.
        Assert.False(DiskActivityRates.TryComputeBytesPerSecond(
            0UL, 1_000UL, TimeSpan.FromSeconds(seconds), out _, out SampleFailure error));

        Assert.Equal(SampleFailure.NoElapsedTime, error);
    }

    [Fact]
    public void BusyPercentIsComputedFromIdleTime()
    {
        // Windows counts IDLE time. Over a one-second interval with 0.6 s of idle, the disk
        // worked 40% of the time.
        Assert.True(DiskActivityRates.TryComputeBusy(
            ReadingWithIdleTime(0UL, 0UL, 10.0),
            ReadingWithIdleTime(0UL, 0UL, 10.6),
            TimeSpan.FromSeconds(1),
            out Percent busy,
            out _));

        Assert.Equal(40d, busy.Points, 6);
    }

    [Fact]
    public void AnIdleDiskNeverReportsANegativeBusyPercent()
    {
        // Measured on this machine, with PhysicalDrive1 idle: the idle counter advances a
        // hair MORE than the interval, because the two clocks are not the same clock, and the
        // computation gave -0.07%. Percent.TryFromRatio rejects negatives, so without the
        // clamp an idle disk would report itself as FAULTED instead of idle.
        Assert.True(DiskActivityRates.TryComputeBusy(
            ReadingWithIdleTime(0UL, 0UL, 10.0),
            ReadingWithIdleTime(0UL, 0UL, 11.0007),
            TimeSpan.FromSeconds(1),
            out Percent busy,
            out _));

        Assert.Equal(0d, busy.Points);
    }

    [Fact]
    public void BusyPercentIsAlsoComputedFromBusyTicks()
    {
        // Linux counts BUSY time: the same quantity seen from the other side.
        Assert.True(DiskActivityRates.TryComputeBusy(
            ReadingWithBusyTime(0UL, 0UL, 5.0),
            ReadingWithBusyTime(0UL, 0UL, 5.25),
            TimeSpan.FromSeconds(1),
            out Percent busy,
            out _));

        Assert.Equal(25d, busy.Points, 6);
    }

    [Fact]
    public void BusyPercentNeverExceeds100()
    {
        // With several requests queued the busy ticks can exceed the interval. The disk is
        // not 150% busy: it is busy, and that is all.
        Assert.True(DiskActivityRates.TryComputeBusy(
            ReadingWithBusyTime(0UL, 0UL, 5.0),
            ReadingWithBusyTime(0UL, 0UL, 6.5),
            TimeSpan.FromSeconds(1),
            out Percent busy,
            out _));

        Assert.Equal(100d, busy.Points);
    }

    [Fact]
    public void BusyTimeThatGoesBackwardsIsReportedAsAFailure()
    {
        Assert.False(DiskActivityRates.TryComputeBusy(
            ReadingWithBusyTime(0UL, 0UL, 6.0),
            ReadingWithBusyTime(0UL, 0UL, 5.0),
            TimeSpan.FromSeconds(1),
            out _,
            out SampleFailure error));

        Assert.Equal(SampleFailure.CounterWentBackwards, error);
    }
}

/// <summary>
/// The disk activity collector.
/// </summary>
/// <remarks>
/// It keeps state — the previous reading and the instant it was taken — and it keeps it
/// PER INSTANCE, which is the difference from the CPU: disks appear and disappear while the
/// program is running, and a disk that has just appeared must not steal another disk's
/// previous sample.
/// </remarks>
public class DiskActivityCollectorTests
{
    private static readonly TimeSpan RoundInterval = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task TheFirstRoundIsWarmupWithNoPoints()
    {
        // Zero is not "the disk is idle": it is "I do not know yet". Publishing zero on the
        // first round is the easiest way to make a disk that is working look idle.
        FakeProvider provider = new([ReadingWithIdleTime("Disk 0", 0UL, 0UL, 0)]);
        (DiskActivityCollector collector, _) = Create(provider);

        MetricSnapshot first = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Warmup, first.Status);
        Assert.Empty(first.Points);
    }

    [Fact]
    public async Task FromTheSecondRoundThereAreThreePointsPerDisk()
    {
        FakeProvider provider = new([ReadingWithIdleTime("Disk 0", 0UL, 0UL, 0)]);
        (DiskActivityCollector collector, FakeClock clock) = Create(provider);

        await collector.CollectAsync(CancellationToken.None);

        provider.Readings = [ReadingWithIdleTime("Disk 0", 2_000UL, 6_000UL, 0.75)];
        clock.Advance(RoundInterval);

        MetricSnapshot second = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Ok, second.Status);
        Assert.Equal(3, second.Points.Count);
        Assert.Equal(2_000d, ValueOf(second, DiskActivityCollector.ReadBytesPerSecondMetricId));
        Assert.Equal(6_000d, ValueOf(second, DiskActivityCollector.WriteBytesPerSecondMetricId));
        Assert.Equal(25d, ValueOf(second, DiskActivityCollector.BusyPercentMetricId), 6);
    }

    [Fact]
    public async Task EveryDiskHasItsOwnInstance()
    {
        FakeProvider provider = new(
            [ReadingWithIdleTime("Disk 0", 0UL, 0UL, 0), ReadingWithIdleTime("Disk 1", 0UL, 0UL, 0)]);
        (DiskActivityCollector collector, FakeClock clock) = Create(provider);

        await collector.CollectAsync(CancellationToken.None);

        provider.Readings =
            [ReadingWithIdleTime("Disk 0", 1_000UL, 0UL, 0.5), ReadingWithIdleTime("Disk 1", 4_000UL, 0UL, 1.0)];
        clock.Advance(RoundInterval);

        MetricSnapshot second = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(3, second.Points.Count(p => p.Instance == "Disk 0"));
        Assert.Equal(3, second.Points.Count(p => p.Instance == "Disk 1"));

        // If the state were kept per collector instead of per disk, the two would steal each
        // other's previous sample and the two disks' numbers would be swapped.
        Assert.Equal(
            1_000d,
            second.Points.Single(p =>
                p.MetricId == DiskActivityCollector.ReadBytesPerSecondMetricId
                && p.Instance == "Disk 0").Value!.Value.Number);
    }

    [Fact]
    public async Task ADiskThatAppearsLaterWaitsForItsSecondSample()
    {
        // A USB stick plugged in just now has no previous sample. Computing its rate from
        // the absolute counter would give "since the disk came into existence", not "now":
        // a huge number, and nobody would flag it.
        FakeProvider provider = new([ReadingWithIdleTime("Disk 0", 0UL, 0UL, 0)]);
        (DiskActivityCollector collector, FakeClock clock) = Create(provider);

        await collector.CollectAsync(CancellationToken.None);

        provider.Readings =
            [ReadingWithIdleTime("Disk 0", 1_000UL, 0UL, 0.5), ReadingWithIdleTime("Disk 9", 999_999UL, 0UL, 0.5)];
        clock.Advance(RoundInterval);

        MetricSnapshot second = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(3, second.Points.Count(p => p.Instance == "Disk 0"));
        Assert.DoesNotContain(second.Points, p => p.Instance == "Disk 9" && p.Value is not null);
    }

    [Fact]
    public async Task ADiskThatDisappearsLeavesNoStaleNumbers()
    {
        // A disk that has been unplugged must not go on showing its last number: it would be
        // a frozen reading that reads as a current one.
        FakeProvider provider = new(
            [ReadingWithIdleTime("Disk 0", 0UL, 0UL, 0), ReadingWithIdleTime("Disk 1", 0UL, 0UL, 0)]);
        (DiskActivityCollector collector, FakeClock clock) = Create(provider);

        await collector.CollectAsync(CancellationToken.None);

        provider.Readings = [ReadingWithIdleTime("Disk 0", 1_000UL, 0UL, 0.5)];
        clock.Advance(RoundInterval);

        MetricSnapshot second = await collector.CollectAsync(CancellationToken.None);

        Assert.DoesNotContain(second.Points, p => p.Instance == "Disk 1");
    }

    [Fact]
    public async Task AFailedReadClearsTheHistory()
    {
        // The gap is the point: resuming after an error, the delta would be computed over an
        // interval whose duration is unknown. Better one more warm-up round than an average
        // invented over an unknown span of time.
        FakeProvider provider = new([ReadingWithIdleTime("Disk 0", 0UL, 0UL, 0)]);
        (DiskActivityCollector collector, FakeClock clock) = Create(provider);

        await collector.CollectAsync(CancellationToken.None);

        provider.Readable = false;
        clock.Advance(RoundInterval);
        MetricSnapshot broken = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Unavailable, broken.Status);

        provider.Readable = true;
        provider.Readings = [ReadingWithIdleTime("Disk 0", 50_000UL, 0UL, 0.5)];
        clock.Advance(RoundInterval);
        MetricSnapshot resumed = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Warmup, resumed.Status);
        Assert.Empty(resumed.Points);
    }

    [Fact]
    public async Task APlatformThatCannotBeMeasuredSaysSo()
    {
        FakeProvider provider = new([], supported: false, reason: "nothing is measured here");
        (DiskActivityCollector collector, _) = Create(provider);

        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Unsupported, snapshot.Status);
        Assert.Equal("nothing is measured here", snapshot.Message);
    }

    [Fact]
    public void TheCatalogDeclaresEverythingPerInstance()
    {
        (DiskActivityCollector collector, _) = Create(new FakeProvider([]));

        Assert.Equal(3, collector.Descriptors.Count);
        Assert.All(collector.Descriptors, d => Assert.True(d.IsPerInstance));

        // A gauge appears only for the percentages: the two rates stay as text rows, and
        // that is a deliberate consequence, not an oversight.
        Assert.Single(collector.Descriptors, d => d.Unit == MetricUnit.Percent);
    }

    private static double ValueOf(MetricSnapshot snapshot, string metricId) =>
        snapshot.Points.Single(p => p.MetricId == metricId).Value!.Value.Number;

    private static DiskActivityReading ReadingWithIdleTime(
        string instance, ulong bytesRead, ulong bytesWritten, double idleSeconds) =>
        DiskActivityReading.WithIdleTime(
            instance, bytesRead, bytesWritten, TimeSpan.FromSeconds(idleSeconds));

    private static (DiskActivityCollector Collector, FakeClock Clock) Create(
        IDiskActivityProvider provider)
    {
        FakeClock clock = new();

        return (new DiskActivityCollector(provider, clock), clock);
    }

    /// <summary>A clock that advances only when it is told to.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private long now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => now;

        public void Advance(TimeSpan delta) => now += delta.Ticks;
    }

    private sealed class FakeProvider(
        IReadOnlyList<DiskActivityReading> readings,
        bool supported = true,
        string? reason = null) : IDiskActivityProvider
    {
        public IReadOnlyList<DiskActivityReading> Readings { get; set; } = readings;

        public bool Readable { get; set; } = true;

        public bool IsSupported => supported;

        public string? UnsupportedReason => reason;

        public bool TryRead(out IReadOnlyList<DiskActivityReading> readings)
        {
            readings = Readings;

            return Readable;
        }
    }
}