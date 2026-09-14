using System.Globalization;
using Observer.Core.Metrics;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// The SQLite layer against a real database. This does not re-test the rollup arithmetic —
/// that is already covered on its own — but everything only a database can get wrong:
/// series identity, transactions, idempotence, and the order between consolidation and
/// purging.
/// </summary>
public class MetricStoreTests
{
    private static readonly TimeSpan NoGrace = TimeSpan.Zero;
    private static readonly TimeSpan OneHourPerPass = TimeSpan.FromHours(1);

    private static DateTimeOffset T(string isoInstant) =>
        DateTimeOffset.Parse(isoInstant, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static SeriesSample CpuSample(string isoInstant, double value, string instance = "") =>
        new(
            new SeriesKey("cpu", "cpu.usage.total", instance),
            MetricValueKind.Number,
            T(isoInstant).ToUnixTimeMilliseconds(),
            value);

    private static SeriesKey CpuSeries(string instance = "") =>
        new("cpu", "cpu.usage.total", instance);

    [Fact]
    public void Initialize_IsIdempotent()
    {
        using TempMetricStore temp = new();

        // The service restarts and calls Initialize on a file that already exists: if this
        // line threw, the service would never start a second time.
        temp.Store.Initialize();

        Assert.Empty(temp.Store.ListSeries());
        Assert.True(File.Exists(temp.DatabasePath));
    }

    [Fact]
    public void ReadHistory_RawBeyondTheLimit_KeepsTheNewestPointsNotTheOldest()
    {
        // An ascending ORDER BY plus LIMIT keeps the OLDEST points. A 90-day request over
        // 5-minute buckets is 25920 points against a limit of 5000: the chart would show only
        // the oldest seventeen days of the ninety and stop about seventy-three days in the
        // past, plausible and with no error at all.
        // On a dashboard the present is the part you cannot afford to lose.
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(
        [
            CpuSample("2026-08-26T12:00:00Z", 1d),
            CpuSample("2026-08-26T12:00:01Z", 2d),
            CpuSample("2026-08-26T12:00:02Z", 3d),
            CpuSample("2026-08-26T12:00:03Z", 4d),
            CpuSample("2026-08-26T12:00:04Z", 5d),
        ]);

        IReadOnlyList<HistoryPoint> points = temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 3);

        Assert.Equal(3, points.Count);

        // The last three, and still returned in ascending order: the client draws from left
        // to right and must not have to reorder anything.
        Assert.Equal(T("2026-08-26T12:00:02Z"), points[0].Timestamp);
        Assert.Equal(T("2026-08-26T12:00:03Z"), points[1].Timestamp);
        Assert.Equal(T("2026-08-26T12:00:04Z"), points[2].Timestamp);
        Assert.Equal(5d, points[2].Last);
    }

    [Fact]
    public void Write_ReadsRawBackInTheSameShapeAsAggregates()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(
        [
            CpuSample("2026-08-26T12:00:00Z", 10d),
            CpuSample("2026-08-26T12:00:01Z", 20d),
        ]);

        IReadOnlyList<HistoryPoint> points = temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100);

        Assert.Equal(2, points.Count);
        Assert.Equal(T("2026-08-26T12:00:00Z"), points[0].Timestamp);

        // On raw data the count is 1 and the four values are identical: that is what lets the
        // client switch resolution without having two different drawing branches.
        Assert.Equal(1, points[0].Count);
        Assert.Equal(10d, points[0].Average);
        Assert.Equal(10d, points[0].Min);
        Assert.Equal(10d, points[0].Max);
        Assert.Equal(10d, points[0].Last);
        Assert.Equal(20d, points[1].Last);
    }

    [Fact]
    public void Write_DoesNotDuplicateTheSeriesOnEverySample()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:00Z", 1d)]);
        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:01Z", 2d)]);
        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:02Z", 3d)]);

        StoredSeries stored = Assert.Single(temp.Store.ListSeries());

        Assert.Equal("cpu.usage.total", stored.Key.MetricId);
        Assert.Equal(string.Empty, stored.Key.Instance);
        Assert.Equal(MetricValueKind.Number, stored.Kind);
    }

    [Fact]
    public void Write_KeepsInstancesApart()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(
        [
            CpuSample("2026-08-26T12:00:00Z", 1d),
            CpuSample("2026-08-26T12:00:00Z", 2d, "core0"),
            CpuSample("2026-08-26T12:00:00Z", 3d, "core1"),
        ]);

        Assert.Equal(3, temp.Store.ListSeries().Count);

        HistoryPoint point = Assert.Single(temp.Store.ReadHistory(
            CpuSeries("core1"), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100));

        Assert.Equal(3d, point.Last);
    }

    [Fact]
    public void Write_TheSameInstantTwiceDoesNotCreateTwoRows()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:00Z", 1d)]);
        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:00Z", 2d)]);

        // The service can rewrite the same snapshot after a transient error. Without an upsert
        // the write would throw and the queue would stall; with a plain INSERT that is ignored
        // the old value would stay.
        HistoryPoint point = Assert.Single(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100));

        Assert.Equal(2d, point.Last);
    }

    [Fact]
    public void Consolidate_ProducesTheMinuteBucketWithTheRightValues()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(
        [
            CpuSample("2026-08-26T12:00:10Z", 10d),
            CpuSample("2026-08-26T12:00:20Z", 30d),
            CpuSample("2026-08-26T12:00:30Z", 20d),
        ]);

        int written = temp.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), NoGrace, OneHourPerPass);

        Assert.Equal(1, written);

        HistoryPoint bucket = Assert.Single(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));

        Assert.Equal(T("2026-08-26T12:00:00Z"), bucket.Timestamp);
        Assert.Equal(3, bucket.Count);
        Assert.Equal(20d, bucket.Average);
        Assert.Equal(10d, bucket.Min);
        Assert.Equal(30d, bucket.Max);
        Assert.Equal(20d, bucket.Last);
    }

    [Fact]
    public void Consolidate_LeavesTheMinuteInProgressAlone()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(
        [
            CpuSample("2026-08-26T12:00:30Z", 1d),
            CpuSample("2026-08-26T12:01:30Z", 2d),
        ]);

        temp.Store.ConsolidateMinutes(T("2026-08-26T12:01:40Z"), NoGrace, OneHourPerPass);

        // The 12:01 minute is still open: consolidating it now would freeze it at a single
        // sample, and purging the raw data would make that error permanent.
        HistoryPoint bucket = Assert.Single(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));

        Assert.Equal(T("2026-08-26T12:00:00Z"), bucket.Timestamp);
    }

    [Fact]
    public void Consolidate_TwoPassesInARowDoNotDoubleTheCounts()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(
        [
            CpuSample("2026-08-26T12:00:10Z", 10d),
            CpuSample("2026-08-26T12:00:20Z", 20d),
        ]);

        temp.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), NoGrace, OneHourPerPass);
        int secondPass = temp.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), NoGrace, OneHourPerPass);

        Assert.Equal(0, secondPass);

        HistoryPoint bucket = Assert.Single(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));

        // If consolidation added instead of rewriting, there would be 4 samples here and a
        // perfectly believable average computed over twice the data.
        Assert.Equal(2, bucket.Count);
        Assert.Equal(15d, bucket.Average);
    }

    [Fact]
    public void Consolidate_ResumesWhereItLeftOff()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:30Z", 1d)]);
        Assert.Equal(1, temp.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), NoGrace, OneHourPerPass));

        temp.Store.WriteSamples([CpuSample("2026-08-26T12:02:10Z", 2d)]);
        Assert.Equal(1, temp.Store.ConsolidateMinutes(T("2026-08-26T12:04:00Z"), NoGrace, OneHourPerPass));

        IReadOnlyList<HistoryPoint> buckets = temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100);

        Assert.Equal(2, buckets.Count);
        Assert.Equal(T("2026-08-26T12:00:00Z"), buckets[0].Timestamp);
        Assert.Equal(T("2026-08-26T12:02:00Z"), buckets[1].Timestamp);
    }

    [Fact]
    public void ConsolidateFiveMinutes_DoesNotRunAheadOfTheMinuteLevel()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(SevenMinutesOfSamples());

        // The minute level has only reached 12:03. A five-minute buckets built now would hold
        // three minutes out of five: a plausible number, a false average, and since the rollup
        // moves its marker forward it would never be corrected again.
        temp.Store.ConsolidateMinutes(T("2026-08-26T12:03:10Z"), NoGrace, OneHourPerPass);

        int written = temp.Store.ConsolidateFiveMinutes(
            T("2026-08-26T12:10:00Z"), NoGrace, OneHourPerPass);

        Assert.Equal(0, written);
        Assert.Null(temp.Store.ConsolidatedThrough(BucketWidths.FiveMinuteSeconds));
        Assert.Empty(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.FiveMinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:30:00Z"), 100));
    }

    [Fact]
    public void ConsolidateFiveMinutes_ProducesTheBucketOnceTheLevelBelowCoversIt()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(SevenMinutesOfSamples());
        temp.Store.ConsolidateMinutes(T("2026-08-26T12:07:10Z"), NoGrace, OneHourPerPass);

        int written = temp.Store.ConsolidateFiveMinutes(
            T("2026-08-26T12:10:00Z"), NoGrace, OneHourPerPass);

        Assert.Equal(1, written);

        HistoryPoint bucket = Assert.Single(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.FiveMinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:30:00Z"), 100));

        Assert.Equal(T("2026-08-26T12:00:00Z"), bucket.Timestamp);
        Assert.Equal(5, bucket.Count);
    }

    [Fact]
    public void ConsolidateFiveMinutes_AverageMatchesTheRawAverage()
    {
        using TempMetricStore temp = new();

        // Minutes with a DIFFERENT number of samples: the normal case, not an edge case.
        temp.Store.WriteSamples(
        [
            CpuSample("2026-08-26T12:00:10Z", 100d),
            CpuSample("2026-08-26T12:01:10Z", 0d),
            CpuSample("2026-08-26T12:01:20Z", 0d),
            CpuSample("2026-08-26T12:01:30Z", 0d),
            CpuSample("2026-08-26T12:02:10Z", 0d),
            CpuSample("2026-08-26T12:02:20Z", 0d),
            CpuSample("2026-08-26T12:03:10Z", 0d),
            CpuSample("2026-08-26T12:04:10Z", 0d),
        ]);

        temp.Store.ConsolidateMinutes(T("2026-08-26T12:06:00Z"), NoGrace, OneHourPerPass);
        temp.Store.ConsolidateFiveMinutes(T("2026-08-26T12:10:00Z"), NoGrace, OneHourPerPass);

        HistoryPoint bucket = Assert.Single(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.FiveMinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));

        // 100 / 8 = 12.5. Averaging the minute averages would give 20.
        Assert.Equal(8, bucket.Count);
        Assert.Equal(100d / 8d, bucket.Average);
        Assert.Equal(100d, bucket.Max);
        Assert.Equal(0d, bucket.Min);
    }

    [Fact]
    public void PurgeRaw_LeavesWhatNothingHasAggregatedYet()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:00Z", 1d)]);

        // The rollup has never run: that sample exists in exactly one place in the world.
        int purged = temp.Store.PurgeRaw(T("2026-08-26T20:00:00Z"), TimeSpan.FromHours(6));

        Assert.Equal(0, purged);
        Assert.NotEmpty(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100));
    }

    [Fact]
    public void PurgeRaw_DeletesWhatIsAlreadyAggregatedAndOldEnough()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:10Z", 1d)]);
        temp.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), NoGrace, OneHourPerPass);

        int purged = temp.Store.PurgeRaw(T("2026-08-26T20:00:00Z"), TimeSpan.FromHours(6));

        Assert.Equal(1, purged);
        Assert.Empty(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100));

        // The raw data goes, the summary stays: that is exactly the point of the rollup.
        Assert.NotEmpty(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));
    }

    [Fact]
    public void PurgeRaw_LeavesWhatIsStillInsideTheRetentionWindow()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:10Z", 1d)]);
        temp.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), NoGrace, OneHourPerPass);

        int purged = temp.Store.PurgeRaw(T("2026-08-26T12:05:00Z"), TimeSpan.FromHours(6));

        Assert.Equal(0, purged);
    }

    [Fact]
    public void PurgeRollup_MinutesDoNotRunAheadOfFiveMinutes()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:10Z", 1d)]);
        temp.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), NoGrace, OneHourPerPass);

        // The five-minute level has never run: purging the minutes would mean losing that
        // stretch of history for ever.
        int purged = temp.Store.PurgeRollup(
            BucketWidths.MinuteSeconds, T("2026-09-30T00:00:00Z"), TimeSpan.FromDays(7));

        Assert.Equal(0, purged);
        Assert.NotEmpty(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));
    }

    [Fact]
    public void PurgeRollup_FiveMinutesHasNoLevelBelowItAndGoesByRetentionAlone()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(SevenMinutesOfSamples());
        temp.Store.ConsolidateMinutes(T("2026-08-26T12:07:10Z"), NoGrace, OneHourPerPass);
        temp.Store.ConsolidateFiveMinutes(T("2026-08-26T12:10:00Z"), NoGrace, OneHourPerPass);

        // The last level has nothing downstream of it: if it waited for a later consolidation
        // it would NEVER delete anything and the file would grow for ever.
        int purged = temp.Store.PurgeRollup(
            BucketWidths.FiveMinuteSeconds, T("2027-01-01T00:00:00Z"), TimeSpan.FromDays(90));

        Assert.Equal(1, purged);
    }

    [Fact]
    public void ReadHistory_ReturnsOnlyTheRequestedWindow()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(
        [
            CpuSample("2026-08-26T11:59:59Z", 1d),
            CpuSample("2026-08-26T12:00:00Z", 2d),
            CpuSample("2026-08-26T12:00:30Z", 3d),
            CpuSample("2026-08-26T12:01:00Z", 4d),
        ]);

        IReadOnlyList<HistoryPoint> points = temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100);

        // Start inclusive, end exclusive: that way two consecutive windows do not show the
        // same point twice.
        Assert.Equal(2, points.Count);
        Assert.Equal(2d, points[0].Last);
        Assert.Equal(3d, points[1].Last);
    }

    [Fact]
    public void ReadHistory_RespectsThePointLimit()
    {
        using TempMetricStore temp = new();

        List<SeriesSample> samples = [];

        for (int second = 0; second < 50; second++)
        {
            samples.Add(new SeriesSample(
                CpuSeries(),
                MetricValueKind.Number,
                T("2026-08-26T12:00:00Z").AddSeconds(second).ToUnixTimeMilliseconds(),
                second));
        }

        temp.Store.WriteSamples(samples);

        IReadOnlyList<HistoryPoint> points = temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 10);

        // The limit protects the service: a one-month window at one-second resolution must not
        // be able to build a response of hundreds of megabytes in memory.
        Assert.Equal(10, points.Count);
    }

    [Fact]
    public void ReadHistory_OfAnUnknownSeriesIsEmptyNotAnError()
    {
        using TempMetricStore temp = new();

        Assert.Empty(temp.Store.ReadHistory(
            new SeriesKey("gpu", "gpu.temp", "0"),
            BucketWidths.RawSeconds,
            T("2026-08-26T12:00:00Z"),
            T("2026-08-26T13:00:00Z"),
            100));
    }

    [Fact]
    public void ReadHistory_RejectsAnUnknownResolution()
    {
        using TempMetricStore temp = new();

        // A made-up resolution must not return an empty list: it would read as "no data"
        // instead of "you asked for the wrong thing".
        Assert.Throws<ArgumentOutOfRangeException>(() => temp.Store.ReadHistory(
            CpuSeries(), 30, T("2026-08-26T12:00:00Z"), T("2026-08-26T13:00:00Z"), 100));
    }

    [Fact]
    public void ReadStats_CountsSeriesRowsAndConsolidation()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(SevenMinutesOfSamples());
        temp.Store.ConsolidateMinutes(T("2026-08-26T12:07:10Z"), NoGrace, OneHourPerPass);
        temp.Store.ConsolidateFiveMinutes(T("2026-08-26T12:10:00Z"), NoGrace, OneHourPerPass);

        StorageStats stats = temp.Store.ReadStats();

        Assert.Equal(1L, stats.SeriesCount);
        Assert.Equal(7L, stats.RawSamples);
        Assert.Equal(7L, stats.MinuteBuckets);
        Assert.Equal(1L, stats.FiveMinuteBuckets);
        Assert.Equal(T("2026-08-26T12:07:00Z"), stats.MinuteConsolidatedThrough);
        Assert.Equal(T("2026-08-26T12:05:00Z"), stats.FiveMinuteConsolidatedThrough);
        Assert.True(stats.FileSizeBytes > 0L);
    }

    [Fact]
    public void RunMaintenance_ConsolidatesAndPurgesInOnePass()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(SevenMinutesOfSamples());

        StorageOptions options = new()
        {
            ConsolidationGrace = TimeSpan.Zero,
            RawRetention = TimeSpan.FromMinutes(1),
            MinuteRetention = TimeSpan.FromDays(7),
            FiveMinuteRetention = TimeSpan.FromDays(90),
        };

        // A single pass must do everything in the right ORDER: aggregate first, then purge.
        // With the order reversed the first pass would delete the raw data that the same
        // pass's consolidation had not read yet.
        MaintenanceReport report = temp.Store.RunMaintenance(T("2026-08-26T12:10:00Z"), options);

        Assert.Equal(7, report.MinuteBucketsWritten);

        // Two five-minute buckets, not one: the minutes are consolidated through 12:10, so the
        // 12:05-12:10 interval is closed as well, even though it holds only two minutes of
        // real data.
        Assert.Equal(2, report.FiveMinuteBucketsWritten);
        Assert.Equal(7, report.RawRowsPurged);
        Assert.Equal(0, report.MinuteRowsPurged);
        Assert.Equal(0, report.FiveMinuteRowsPurged);

        // The minutes stay readable even though the raw data is gone.
        Assert.Equal(7, temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:30:00Z"), 100).Count);
    }

    private static IReadOnlyList<SeriesSample> SevenMinutesOfSamples() =>
    [
        CpuSample("2026-08-26T12:00:10Z", 1d),
        CpuSample("2026-08-26T12:01:10Z", 2d),
        CpuSample("2026-08-26T12:02:10Z", 3d),
        CpuSample("2026-08-26T12:03:10Z", 4d),
        CpuSample("2026-08-26T12:04:10Z", 5d),
        CpuSample("2026-08-26T12:05:10Z", 6d),
        CpuSample("2026-08-26T12:06:10Z", 7d),
    ];
}