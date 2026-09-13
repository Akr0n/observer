using Observer.Core.Metrics;

namespace Observer.Service.Persistence;

/// <summary>
/// The identity of a time series: the triple that tells one number from another.
/// </summary>
/// <param name="CollectorId">Who produced the value, for example "cpu".</param>
/// <param name="MetricId">Which metric, for example "cpu.usage.total".</param>
/// <param name="Instance">
/// The core, the disk, the interface. EMPTY STRING, not null, when the metric is unique per
/// machine: in SQLite two NULLs are not considered equal in a UNIQUE index, so with null the
/// same series would be inserted again at every sampling and the history would break into
/// thousands of series with one point each.
/// </param>
public readonly record struct SeriesKey(string CollectorId, string MetricId, string Instance);

/// <summary>A numeric value flattened out of a snapshot, ready to be written.</summary>
/// <param name="Key">The series it belongs to.</param>
/// <param name="Kind">The <see cref="MetricValue"/> branch it comes from.</param>
/// <param name="TimestampMs">Sampling instant, in milliseconds since the Unix epoch (UTC).</param>
/// <param name="Value">The numeric value.</param>
public readonly record struct SeriesSample(SeriesKey Key, MetricValueKind Kind, long TimestampMs, double Value);

/// <summary>A raw sample of ONE series, already stripped of the series identity.</summary>
/// <param name="TimestampMs">Sampling instant, in milliseconds since the Unix epoch (UTC).</param>
/// <param name="Value">The measured value.</param>
public readonly record struct RawSample(long TimestampMs, double Value);

/// <summary>
/// An aggregated time interval of ONE series.
/// </summary>
/// <remarks>
/// It keeps the SUM and the COUNT instead of the already computed average, and that is the
/// central decision of the whole rollup. Recombining five one-minute buckets into a
/// five-minute one, the average of the averages is wrong every time the buckets do not hold
/// the same number of samples — and they do not hold the same number every time the service
/// restarts, a collector times out, or a metric appears halfway through a minute. The result
/// would be a plausible and false number. With sum and count the five-minute average matches,
/// digit for digit, the average of the raw samples.
/// </remarks>
public sealed record RollupBucket
{
    /// <summary>Creates an aggregated bucket.</summary>
    /// <param name="bucketStartMs">Start of the interval, aligned to its width.</param>
    /// <param name="count">Number of raw samples that flowed in here.</param>
    /// <param name="sum">Sum of the values.</param>
    /// <param name="min">Minimum value.</param>
    /// <param name="max">Maximum value.</param>
    /// <param name="last">Last value in time order.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// If the count is not positive, or if a value is not finite. Both cases produce JSON that
    /// cannot be serialised: an empty bucket has an average of 0/0 = NaN, and a NaN in the
    /// response does not lose one metric, it loses the WHOLE HTTP response.
    /// </exception>
    public RollupBucket(long bucketStartMs, int count, double sum, double min, double max, double last)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        RequireFinite(sum, nameof(sum));
        RequireFinite(min, nameof(min));
        RequireFinite(max, nameof(max));
        RequireFinite(last, nameof(last));

        BucketStartMs = bucketStartMs;
        Count = count;
        Sum = sum;
        Min = min;
        Max = max;
        Last = last;
    }

    /// <summary>Start of the interval, in milliseconds since the Unix epoch (UTC).</summary>
    public long BucketStartMs { get; }

    /// <summary>Number of raw samples aggregated. Always at least 1.</summary>
    public int Count { get; }

    /// <summary>Sum of the aggregated values.</summary>
    public double Sum { get; }

    /// <summary>Minimum value in the interval.</summary>
    public double Min { get; }

    /// <summary>Maximum value in the interval.</summary>
    public double Max { get; }

    /// <summary>Last value of the interval, in time order.</summary>
    public double Last { get; }

    /// <summary>Average of the aggregated samples.</summary>
    public double Average => Sum / Count;

    /// <summary>
    /// The degenerate bucket that stands for a single raw sample. It is what makes raw samples
    /// and aggregates go through the SAME recombination, instead of writing two versions of it
    /// that can diverge.
    /// </summary>
    /// <param name="timestampMs">Sample instant, in milliseconds since the Unix epoch (UTC).</param>
    /// <param name="value">The measured value.</param>
    /// <returns>A bucket with a count of 1.</returns>
    public static RollupBucket FromSample(long timestampMs, double value) =>
        new(timestampMs, 1, value, value, value, value);

    private static void RequireFinite(double value, string paramName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                "A non-finite value cannot be represented in JSON and would fail the whole response.");
        }
    }
}
