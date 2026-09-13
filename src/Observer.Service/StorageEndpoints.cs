using Observer.Core.Metrics;
using Observer.Service.Persistence;

namespace Observer.Service;

/// <summary>The endpoints that expose the history.</summary>
/// <remarks>
/// They are mapped AFTER the authentication middleware, like the ones that already exist: a
/// machine's history says when it is on, how hard it works and when nobody uses it, which is
/// more than a single sample says.
/// </remarks>
public static class StorageEndpoints
{
    /// <summary>Window used when the request does not say from when to when.</summary>
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(1);

    /// <summary>Maps /metrics/series, /metrics/history and /metrics/storage.</summary>
    /// <param name="endpoints">The application's route builder.</param>
    public static void MapStorageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Which series exist in the history. It is the equivalent of /metrics/catalog for the
        // past: the catalog says what the service KNOWS how to measure, this says what it has
        // actually measured on this machine.
        endpoints.MapGet("/metrics/series", (MetricStore store, StorageOptions options) =>
            options.Enabled
                ? Results.Ok(store.ListSeries().Select(ToResponse).ToList())
                : Disabled());

        endpoints.MapGet("/metrics/history", (
            MetricStore store,
            StorageOptions options,
            string? collector,
            string? metric,
            string? instance,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? resolution) =>
            History(store, options, collector, metric, instance, from, to, resolution));

        endpoints.MapGet("/metrics/storage", (
            MetricStore store,
            StorageOptions options,
            SnapshotBuffer buffer) => Storage(store, options, buffer));
    }

    private static IResult History(
        MetricStore store,
        StorageOptions options,
        string? collector,
        string? metric,
        string? instance,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? resolution)
    {
        if (!options.Enabled)
        {
            return Disabled();
        }

        if (string.IsNullOrWhiteSpace(collector) || string.IsNullOrWhiteSpace(metric))
        {
            return Problem("Both 'collector' and 'metric' are required to identify the series to read.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset upper = to ?? now;
        DateTimeOffset lower = from ?? upper - DefaultWindow;

        if (upper <= lower)
        {
            return Problem("'to' must come after 'from': the requested time range is empty or reversed.");
        }

        int bucketSeconds;

        if (IsAuto(resolution))
        {
            // Raw data older than the retention has already been deleted: asking for it
            // would give an empty chart, which reads as "machine not monitored".
            bucketSeconds = HistoryResolution.Choose(
                lower, upper, options.MaxHistoryPoints, now - options.RawRetention);
        }
        else if (!TryParseResolution(resolution, out bucketSeconds))
        {
            return Problem(
                "Unknown resolution: expected 'auto', 'raw', '1m' or '5m'.");
        }

        IReadOnlyList<HistoryPoint> points = store.ReadHistory(
            new SeriesKey(collector, metric, instance ?? string.Empty),
            bucketSeconds,
            lower,
            upper,
            options.MaxHistoryPoints);

        return Results.Ok(new HistoryResponse(
            collector,
            metric,
            string.IsNullOrEmpty(instance) ? null : instance,
            ResolutionLabel(bucketSeconds),
            bucketSeconds,
            lower,
            upper,

            // Declaring the truncation is what distinguishes "the chart ends here"
            // from "the machine was off here".
            points.Count >= options.MaxHistoryPoints,
            points.Select(point => new HistoryPointResponse(
                point.Timestamp,
                point.Count,
                point.Average,
                point.Min,
                point.Max,
                point.Last)).ToList()));
    }

    private static IResult Storage(MetricStore store, StorageOptions options, SnapshotBuffer buffer)
    {
        if (!options.Enabled)
        {
            return Disabled();
        }

        StorageStats stats = store.ReadStats();

        return Results.Ok(new StorageResponse(
            Enabled: true,
            stats.DatabasePath,
            stats.FileSizeBytes,
            stats.SeriesCount,
            stats.RawSamples,
            stats.MinuteBuckets,
            stats.FiveMinuteBuckets,
            stats.MinuteConsolidatedThrough,
            stats.FiveMinuteConsolidatedThrough,
            buffer.DroppedCount,
            new RetentionResponse(
                options.RawRetention,
                options.MinuteRetention,
                options.FiveMinuteRetention)));
    }

    private static StoredSeriesResponse ToResponse(StoredSeries series) =>
        new(
            series.Key.CollectorId,
            series.Key.MetricId,

            // On the wire a missing instance is null, as in MetricPoint: the empty string lives
            // only inside the database, where the UNIQUE index needs it.
            string.IsNullOrEmpty(series.Key.Instance) ? null : series.Key.Instance,
            (int)series.Kind);

    private static bool IsAuto(string? resolution) =>
        string.IsNullOrWhiteSpace(resolution)
        || string.Equals(resolution, "auto", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseResolution(string? resolution, out int bucketSeconds)
    {
        if (string.Equals(resolution, "raw", StringComparison.OrdinalIgnoreCase))
        {
            bucketSeconds = BucketWidths.RawSeconds;
            return true;
        }

        if (string.Equals(resolution, "1m", StringComparison.OrdinalIgnoreCase))
        {
            bucketSeconds = BucketWidths.MinuteSeconds;
            return true;
        }

        if (string.Equals(resolution, "5m", StringComparison.OrdinalIgnoreCase))
        {
            bucketSeconds = BucketWidths.FiveMinuteSeconds;
            return true;
        }

        bucketSeconds = 0;

        return false;
    }

    private static string ResolutionLabel(int bucketSeconds) => bucketSeconds switch
    {
        BucketWidths.RawSeconds => "raw",
        BucketWidths.MinuteSeconds => "1m",
        _ => "5m",
    };

    private static IResult Problem(string message) =>
        Results.BadRequest(new ErrorResponse(message));

    private static IResult Disabled() =>
        Results.Json(
            new ErrorResponse(
                "History is disabled on this service (Observer:Storage:Enabled). " +
                "The /metrics/catalog and /metrics/latest endpoints still work."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
}

/// <summary>The reason a request produced no data.</summary>
/// <param name="Message">Human-readable explanation.</param>
public sealed record ErrorResponse(string Message);

/// <summary>A series present in the history.</summary>
/// <param name="CollectorId">Who produces it.</param>
/// <param name="MetricId">Which metric.</param>
/// <param name="Instance">The core, the disk, the interface; null if the metric is unique.</param>
/// <param name="ValueKind">
/// The branch of <see cref="MetricValue"/> it comes from, with the same numeric encoding as
/// /metrics/latest.
/// </param>
public sealed record StoredSeriesResponse(string CollectorId, string MetricId, string? Instance, int ValueKind);

/// <summary>A history point.</summary>
/// <param name="Timestamp">Instant of the sample, or start of the bucket.</param>
/// <param name="Count">How many raw samples are inside. On raw data it is 1.</param>
/// <param name="Avg">Average of the samples.</param>
/// <param name="Min">Minimum value.</param>
/// <param name="Max">Maximum value.</param>
/// <param name="Last">Last value in time order.</param>
public sealed record HistoryPointResponse(
    DateTimeOffset Timestamp,
    int Count,
    double Avg,
    double Min,
    double Max,
    double Last);

/// <summary>The response of /metrics/history.</summary>
/// <param name="CollectorId">Who produces the series.</param>
/// <param name="MetricId">Which metric.</param>
/// <param name="Instance">The instance, or null.</param>
/// <param name="Resolution">The resolution actually used: "raw", "1m" or "5m".</param>
/// <param name="BucketSeconds">The same resolution in seconds.</param>
/// <param name="From">Start of the window, included.</param>
/// <param name="To">End of the window, excluded.</param>
/// <param name="Truncated">True if the point limit cut the response.</param>
/// <param name="Points">The points, in increasing time order.</param>
public sealed record HistoryResponse(
    string CollectorId,
    string MetricId,
    string? Instance,
    string Resolution,
    int BucketSeconds,
    DateTimeOffset From,
    DateTimeOffset To,
    bool Truncated,
    IReadOnlyList<HistoryPointResponse> Points);

/// <summary>The configured retention durations.</summary>
/// <param name="Raw">How long the one-second sampling is kept.</param>
/// <param name="Minute">How long the one-minute buckets are kept.</param>
/// <param name="FiveMinute">How long the five-minute buckets are kept.</param>
public sealed record RetentionResponse(TimeSpan Raw, TimeSpan Minute, TimeSpan FiveMinute);

/// <summary>The response of /metrics/storage.</summary>
/// <param name="Enabled">Whether the history is on.</param>
/// <param name="DatabasePath">Where the file is.</param>
/// <param name="FileSizeBytes">How much it takes up, WAL included.</param>
/// <param name="SeriesCount">How many distinct series.</param>
/// <param name="RawSamples">Raw samples still present.</param>
/// <param name="MinuteBuckets">One-minute buckets present.</param>
/// <param name="FiveMinuteBuckets">Five-minute buckets present.</param>
/// <param name="MinuteConsolidatedThrough">How far the one-minute level has aggregated.</param>
/// <param name="FiveMinuteConsolidatedThrough">How far the five-minute level has aggregated.</param>
/// <param name="DroppedSnapshots">
/// How many samplings were discarded because the disk was not keeping up. Different from
/// zero means the history has holes.
/// </param>
/// <param name="Retention">The configured durations.</param>
public sealed record StorageResponse(
    bool Enabled,
    string DatabasePath,
    long FileSizeBytes,
    long SeriesCount,
    long RawSamples,
    long MinuteBuckets,
    long FiveMinuteBuckets,
    DateTimeOffset? MinuteConsolidatedThrough,
    DateTimeOffset? FiveMinuteConsolidatedThrough,
    long DroppedSnapshots,
    RetentionResponse Retention);
