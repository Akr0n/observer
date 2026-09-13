namespace Observer.Service.Persistence;

/// <summary>
/// The rollup arithmetic, with no database. It is pure logic on purpose: the rollup is the
/// point where a mistake fails nothing and produces plausible but false numbers, and that
/// class of bug is only found by tests that compare the aggregate with the direct
/// computation over the raw samples.
/// </summary>
public static class RollupMath
{
    /// <summary>
    /// Moves an instant back to the start of the bucket that contains it.
    /// </summary>
    /// <param name="timestampMs">Instant, in milliseconds since the Unix epoch (UTC).</param>
    /// <param name="bucketWidth">Width of the bucket.</param>
    /// <returns>The bucket start, in milliseconds since the Unix epoch (UTC).</returns>
    /// <exception cref="ArgumentOutOfRangeException">If the width is not positive.</exception>
    public static long AlignToBucketStart(long timestampMs, TimeSpan bucketWidth)
    {
        long widthMs = RequireWidthMs(bucketWidth, nameof(bucketWidth));
        long remainder = timestampMs % widthMs;

        // C# integer division truncates towards zero, not downwards: without this correction a
        // negative instant would land in the next bucket instead of the previous one. What is
        // needed here is a real floor.
        return remainder >= 0 ? timestampMs - remainder : timestampMs - remainder - widthMs;
    }

    /// <summary>Aggregates raw samples into buckets of the requested width.</summary>
    /// <param name="samples">The samples, in any order.</param>
    /// <param name="bucketWidth">Width of the buckets to produce.</param>
    /// <returns>The resulting buckets, ordered by increasing start instant.</returns>
    public static IReadOnlyList<RollupBucket> Aggregate(IEnumerable<RawSample> samples, TimeSpan bucketWidth)
    {
        ArgumentNullException.ThrowIfNull(samples);

        // A raw sample IS a one-sample bucket: going through the same recombination,
        // "raw -> 1 minute" and "1 minute -> 5 minutes" cannot diverge, because they are
        // literally the same code.
        return Combine(
            samples.Select(sample => RollupBucket.FromSample(sample.TimestampMs, sample.Value)),
            bucketWidth);
    }

    /// <summary>Recombines narrow buckets into wider ones.</summary>
    /// <param name="buckets">The starting buckets, in any order.</param>
    /// <param name="targetWidth">Width of the buckets to produce.</param>
    /// <returns>The resulting buckets, ordered by increasing start instant.</returns>
    public static IReadOnlyList<RollupBucket> Combine(IEnumerable<RollupBucket> buckets, TimeSpan targetWidth)
    {
        ArgumentNullException.ThrowIfNull(buckets);

        RequireWidthMs(targetWidth, nameof(targetWidth));

        Dictionary<long, Accumulator> byBucketStart = [];

        foreach (RollupBucket bucket in buckets)
        {
            long start = AlignToBucketStart(bucket.BucketStartMs, targetWidth);

            if (byBucketStart.TryGetValue(start, out Accumulator? accumulator))
            {
                accumulator.Add(bucket);
            }
            else
            {
                byBucketStart[start] = new Accumulator(bucket);
            }
        }

        // A Dictionary's order is undefined: without this sort the points would reach the chart
        // shuffled, and a chart with a shuffled time axis looks like measurement noise instead
        // of a bug.
        return byBucketStart
            .OrderBy(entry => entry.Key)
            .Select(entry => entry.Value.ToBucket(entry.Key))
            .ToList();
    }

    private static long RequireWidthMs(TimeSpan width, string paramName)
    {
        long widthMs = (long)width.TotalMilliseconds;

        if (widthMs <= 0)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                width,
                "A bucket width must be positive: at zero the division that aligns it does not exist.");
        }

        return widthMs;
    }

    /// <summary>
    /// Accumulates the buckets of one and the same interval. Mutable and private on purpose: the
    /// public type <see cref="RollupBucket"/> stays immutable and valid by construction.
    /// </summary>
    private sealed class Accumulator
    {
        private int count;
        private double sum;
        private double min;
        private double max;
        private double last;
        private long lastSourceStartMs;

        public Accumulator(RollupBucket first)
        {
            count = first.Count;
            sum = first.Sum;
            min = first.Min;
            max = first.Max;
            last = first.Last;
            lastSourceStartMs = first.BucketStartMs;
        }

        public void Add(RollupBucket bucket)
        {
            count += bucket.Count;
            sum += bucket.Sum;
            min = Math.Min(min, bucket.Min);
            max = Math.Max(max, bucket.Max);

            // "Last" means most recent, not last to arrive: the order of the source must not be
            // able to change the current value shown on the dashboard.
            if (bucket.BucketStartMs >= lastSourceStartMs)
            {
                last = bucket.Last;
                lastSourceStartMs = bucket.BucketStartMs;
            }
        }

        public RollupBucket ToBucket(long bucketStartMs) =>
            new(bucketStartMs, count, sum, min, max, last);
    }
}
