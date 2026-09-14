using System.Globalization;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// The rollup arithmetic, tested WITHOUT a database. It is the most dangerous spot in the whole
/// persistence layer: a mistake here fails no test, throws nothing and shows up in no log — it
/// produces charts full of plausible, wrong numbers. The only way to find it is to compare the
/// aggregate against the direct calculation over the raw samples.
/// </summary>
public class RollupMathTests
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    private static long Ms(string instantIso) =>
        DateTimeOffset.Parse(instantIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUnixTimeMilliseconds();

    [Fact]
    public void AlignToBucketStart_SnapsToTheStartOfTheMinute()
    {
        long aligned = RollupMath.AlignToBucketStart(Ms("2026-08-26T12:03:47.812Z"), OneMinute);

        Assert.Equal(Ms("2026-08-26T12:03:00Z"), aligned);
    }

    [Fact]
    public void AlignToBucketStart_LeavesAnAlreadyAlignedInstantWhereItIs()
    {
        // If an instant exactly on the boundary slipped into the previous bucket, every bucket
        // would hold one sample belonging to the next one and every average would be off by a
        // single sample: slightly wrong, and therefore invisible.
        long aligned = RollupMath.AlignToBucketStart(Ms("2026-08-26T12:05:00Z"), FiveMinutes);

        Assert.Equal(Ms("2026-08-26T12:05:00Z"), aligned);
    }

    [Fact]
    public void AlignToBucketStart_RoundsDownEvenBeforeTheEpoch()
    {
        // With C# integer division -1500 / 60000 is 0, so an instant before 1970 would land in
        // the NEXT bucket instead of the previous one. It does not happen in production, but it
        // is the cheapest way to check that the rounding is a real floor and not a truncation
        // towards zero.
        long aligned = RollupMath.AlignToBucketStart(-1500L, OneMinute);

        Assert.Equal(-60000L, aligned);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public void AlignToBucketStart_RejectsANonPositiveWidth(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RollupMath.AlignToBucketStart(0L, TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void Aggregate_ComputesCountSumMinMaxAndLast()
    {
        RawSample[] samples =
        [
            new(Ms("2026-08-26T12:00:00Z"), 10d),
            new(Ms("2026-08-26T12:00:01Z"), 30d),
            new(Ms("2026-08-26T12:00:02Z"), 20d),
        ];

        RollupBucket bucket = Assert.Single(RollupMath.Aggregate(samples, OneMinute));

        Assert.Equal(Ms("2026-08-26T12:00:00Z"), bucket.BucketStartMs);
        Assert.Equal(3, bucket.Count);
        Assert.Equal(60d, bucket.Sum);
        Assert.Equal(10d, bucket.Min);
        Assert.Equal(30d, bucket.Max);
        Assert.Equal(20d, bucket.Last);
        Assert.Equal(20d, bucket.Average);
    }

    [Fact]
    public void Aggregate_SplitsTheBucketsAndReturnsThemInTimeOrder()
    {
        RawSample[] samples =
        [
            new(Ms("2026-08-26T12:01:30Z"), 5d),
            new(Ms("2026-08-26T12:00:30Z"), 1d),
            new(Ms("2026-08-26T12:00:31Z"), 3d),
        ];

        IReadOnlyList<RollupBucket> buckets = RollupMath.Aggregate(samples, OneMinute);

        Assert.Equal(2, buckets.Count);
        Assert.Equal(Ms("2026-08-26T12:00:00Z"), buckets[0].BucketStartMs);
        Assert.Equal(2, buckets[0].Count);
        Assert.Equal(Ms("2026-08-26T12:01:00Z"), buckets[1].BucketStartMs);
        Assert.Equal(1, buckets[1].Count);
    }

    [Fact]
    public void Aggregate_LastIsTheMostRecentNotTheLastToArrive()
    {
        // The samples already arrive ordered from the database, but "last" has to mean "most
        // recent" and not "last in the list": otherwise, the day someone drops the ORDER BY from
        // the query, the current value shown on the dashboard becomes an old value picked at
        // random, and nothing fails.
        RawSample[] samplesOutOfOrder =
        [
            new(Ms("2026-08-26T12:00:59Z"), 99d),
            new(Ms("2026-08-26T12:00:01Z"), 1d),
        ];

        RollupBucket bucket = Assert.Single(RollupMath.Aggregate(samplesOutOfOrder, OneMinute));

        Assert.Equal(99d, bucket.Last);
    }

    [Fact]
    public void Aggregate_ProducesNoBucketWhenThereAreNoSamples()
    {
        // An empty bucket would have a count of zero and an average of 0/0 = NaN, and a NaN in
        // JSON fails the WHOLE HTTP response, not just that one metric.
        Assert.Empty(RollupMath.Aggregate([], OneMinute));
    }

    [Fact]
    public void Combine_TheFiveMinuteAverageMatchesTheAverageOfTheRawSamples()
    {
        // THE test. Five minutes with a DIFFERENT number of samples each: that is the normal
        // case, not an edge case — it happens at every service restart, at every collector
        // timeout and every time a metric appears halfway through a minute. Anything that keeps
        // the average instead of the sum and the count computes the average of the averages
        // here, and gets a believable, false number.
        RawSample[] rawSamples =
        [
            new(Ms("2026-08-26T12:00:10Z"), 100d),
            new(Ms("2026-08-26T12:01:10Z"), 0d),
            new(Ms("2026-08-26T12:01:20Z"), 0d),
            new(Ms("2026-08-26T12:01:30Z"), 0d),
            new(Ms("2026-08-26T12:02:10Z"), 0d),
            new(Ms("2026-08-26T12:02:20Z"), 0d),
            new(Ms("2026-08-26T12:03:10Z"), 0d),
            new(Ms("2026-08-26T12:04:10Z"), 0d),
        ];

        IReadOnlyList<RollupBucket> minuteBuckets = RollupMath.Aggregate(rawSamples, OneMinute);
        RollupBucket fiveMinuteBucket = Assert.Single(RollupMath.Combine(minuteBuckets, FiveMinutes));

        // True average: 100 / 8 = 12.5. Average of the averages: (100+0+0+0+0)/5 = 20.
        Assert.Equal(100d / 8d, fiveMinuteBucket.Average);
        Assert.Equal(8, fiveMinuteBucket.Count);
        Assert.Equal(100d, fiveMinuteBucket.Sum);
    }

    [Fact]
    public void Combine_TakesTheExtremesNotTheirSum()
    {
        RollupBucket[] minuteBuckets =
        [
            new(Ms("2026-08-26T12:00:00Z"), 60, 600d, 2d, 40d, 7d),
            new(Ms("2026-08-26T12:01:00Z"), 60, 600d, 5d, 90d, 9d),
        ];

        RollupBucket combined = Assert.Single(RollupMath.Combine(minuteBuckets, FiveMinutes));

        Assert.Equal(2d, combined.Min);
        Assert.Equal(90d, combined.Max);
    }

    [Fact]
    public void Combine_LastComesFromTheMostRecentBucket()
    {
        RollupBucket[] minuteBucketsOutOfOrder =
        [
            new(Ms("2026-08-26T12:04:00Z"), 60, 600d, 1d, 20d, 42d),
            new(Ms("2026-08-26T12:00:00Z"), 60, 600d, 1d, 20d, 7d),
        ];

        RollupBucket combined = Assert.Single(RollupMath.Combine(minuteBucketsOutOfOrder, FiveMinutes));

        Assert.Equal(42d, combined.Last);
        Assert.Equal(Ms("2026-08-26T12:00:00Z"), combined.BucketStartMs);
    }

    [Fact]
    public void Combine_KeepsDifferentFiveMinuteBucketsApart()
    {
        RollupBucket[] minuteBuckets =
        [
            new(Ms("2026-08-26T12:04:00Z"), 60, 60d, 1d, 1d, 1d),
            new(Ms("2026-08-26T12:05:00Z"), 60, 120d, 2d, 2d, 2d),
        ];

        IReadOnlyList<RollupBucket> combined = RollupMath.Combine(minuteBuckets, FiveMinutes);

        Assert.Equal(2, combined.Count);
        Assert.Equal(Ms("2026-08-26T12:00:00Z"), combined[0].BucketStartMs);
        Assert.Equal(Ms("2026-08-26T12:05:00Z"), combined[1].BucketStartMs);
    }

    [Fact]
    public void Bucket_RejectsANonPositiveCount()
    {
        // A bucket with a count of zero produces a NaN average and breaks the serialization of
        // the whole response. Better not to let one exist in the first place.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RollupBucket(0L, 0, 0d, 0d, 0d, 0d));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Bucket_RejectsNonFiniteValues(double brokenValue)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RollupBucket(0L, 1, brokenValue, brokenValue, brokenValue, brokenValue));
    }
}
