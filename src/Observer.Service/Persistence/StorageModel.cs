using Observer.Core.Metrics;

namespace Observer.Service.Persistence;

/// <summary>
/// The widths of the aggregation levels, in seconds. They are constants and not configurable
/// because they end up INSIDE the database, in a column: changing them on a live system would
/// make what has already been written unreadable.
/// </summary>
public static class BucketWidths
{
    /// <summary>The raw level. It is not a bucket: it is the sample exactly as it was read.</summary>
    public const int RawSeconds = 1;

    /// <summary>First aggregation level: one minute.</summary>
    public const int MinuteSeconds = 60;

    /// <summary>Second aggregation level: five minutes.</summary>
    public const int FiveMinuteSeconds = 300;
}

/// <summary>A series present in the history.</summary>
/// <param name="Key">The triple that identifies it.</param>
/// <param name="Kind">The kind of value it was first recorded with.</param>
public sealed record StoredSeries(SeriesKey Key, MetricValueKind Kind);

/// <summary>
/// A point of the history. It has the SAME shape for the raw level and for the aggregates: on
/// the raw level the count is 1 and average, minimum, maximum and last match the value read.
/// </summary>
/// <remarks>
/// The single shape is not laziness: it is what lets the client change resolution without
/// changing drawing code. If the raw level had a different shape, every chart would need two
/// branches and one of the two would always be the less exercised one.
/// </remarks>
/// <param name="Timestamp">Sample instant, or bucket start, in UTC.</param>
/// <param name="Count">How many raw samples are inside.</param>
/// <param name="Average">Average of the samples.</param>
/// <param name="Min">Minimum value.</param>
/// <param name="Max">Maximum value.</param>
/// <param name="Last">Last value in time order.</param>
public sealed record HistoryPoint(
    DateTimeOffset Timestamp,
    int Count,
    double Average,
    double Min,
    double Max,
    double Last);

/// <summary>How much space the history takes and how far it has been consolidated.</summary>
/// <param name="DatabasePath">Path of the file.</param>
/// <param name="FileSizeBytes">Size of the file, WAL included.</param>
/// <param name="SeriesCount">Number of distinct series.</param>
/// <param name="RawSamples">Raw samples still present.</param>
/// <param name="MinuteBuckets">One-minute buckets present.</param>
/// <param name="FiveMinuteBuckets">Five-minute buckets present.</param>
/// <param name="MinuteConsolidatedThrough">How far the one-minute level has aggregated.</param>
/// <param name="FiveMinuteConsolidatedThrough">How far the five-minute level has aggregated.</param>
public sealed record StorageStats(
    string DatabasePath,
    long FileSizeBytes,
    long SeriesCount,
    long RawSamples,
    long MinuteBuckets,
    long FiveMinuteBuckets,
    DateTimeOffset? MinuteConsolidatedThrough,
    DateTimeOffset? FiveMinuteConsolidatedThrough);

/// <summary>Outcome of a maintenance pass.</summary>
/// <param name="MinuteBucketsWritten">One-minute buckets written or rewritten.</param>
/// <param name="FiveMinuteBucketsWritten">Five-minute buckets written or rewritten.</param>
/// <param name="RawRowsPurged">Raw samples deleted.</param>
/// <param name="MinuteRowsPurged">One-minute buckets deleted.</param>
/// <param name="FiveMinuteRowsPurged">Five-minute buckets deleted.</param>
public sealed record MaintenanceReport(
    int MinuteBucketsWritten,
    int FiveMinuteBucketsWritten,
    int RawRowsPurged,
    int MinuteRowsPurged,
    int FiveMinuteRowsPurged);
