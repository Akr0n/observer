namespace Observer.Service.Persistence;

/// <summary>
/// The ONLY writer of the database. Drains the queue at a fixed interval and, every so often,
/// consolidates and deletes.
/// </summary>
/// <remarks>
/// <para>
/// A single writer is not a performance choice: SQLite serialises the writes anyway, and with
/// two writers the only difference would be a pair of transactions waiting for each other,
/// plus a SQLITE_BUSY error to handle in a place where it is not needed.
/// </para>
/// <para>
/// Consolidation and deletion run on this same loop, so they cannot overlap a write. If a
/// maintenance round is slow, the queue builds up — and if it gets full it drops the oldest
/// entries. The sampler, in none of these cases, waits.
/// </para>
/// </remarks>
public sealed partial class MetricPersistenceService : BackgroundService
{
    /// <summary>
    /// How often the queue ends up on disk. One second: more often would be one transaction
    /// per sample, less often would only widen the window of data that an abrupt shutdown
    /// takes away.
    /// </summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly MetricWriter writer;
    private readonly MetricStore store;
    private readonly SnapshotBuffer buffer;
    private readonly StorageOptions options;
    private readonly ILogger<MetricPersistenceService> logger;

    private readonly LogThrottle writeLogThrottle = new();
    private readonly LogThrottle maintenanceLogThrottle = new();

    private long lastReportedDrops;

    /// <summary>Creates the persistence service.</summary>
    /// <param name="writer">Who drains the queue onto the database.</param>
    /// <param name="store">The store, for maintenance.</param>
    /// <param name="buffer">The queue, to know how much is being dropped.</param>
    /// <param name="options">The history configuration.</param>
    /// <param name="logger">Where to report faults and drops.</param>
    public MetricPersistenceService(
        MetricWriter writer,
        MetricStore store,
        SnapshotBuffer buffer,
        StorageOptions options,
        ILogger<MetricPersistenceService> logger)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.writer = writer;
        this.store = store;
        this.buffer = buffer;
        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BEFORE any await, so still inside StartAsync: if the file cannot be created, the
        // service does not start and you see it at once. A service that starts and keeps
        // nothing is much worse than one that does not start.
        store.Initialize();

        await Task.Yield();

        DateTimeOffset nextMaintenance = DateTimeOffset.UtcNow + options.MaintenanceInterval;
        using PeriodicTimer timer = new(FlushInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            FlushSafely();
            ReportDrops();

            DateTimeOffset now = DateTimeOffset.UtcNow;

            if (now >= nextMaintenance)
            {
                MaintainSafely(now);
                nextMaintenance = now + options.MaintenanceInterval;
            }
        }

        // One last round: what is left in the queue is worth as much as the rest, and here
        // there is no sampler left to keep from waiting.
        FlushSafely();
    }

    private void FlushSafely()
    {
        try
        {
            writer.FlushPending();

            if (writeLogThrottle.ShouldLogRecovery(out int silenced))
            {
                LogFlushRecovered(logger, silenced);
            }
        }
#pragma warning disable CA1031 // A full disk or a locked file must cost one round of
        catch (Exception ex) // history, not stop live monitoring.
#pragma warning restore CA1031
        {
            // A full disk does not free itself: with no throttle this line comes out every
            // second, and the log that reports the full disk consumes disk.
            if (writeLogThrottle.ShouldLog(ex.GetType().FullName ?? "?"))
            {
                LogFlushFailed(logger, ex);
            }
        }
    }

    private void MaintainSafely(DateTimeOffset now)
    {
        try
        {
            MaintenanceReport report = store.RunMaintenance(now, options);

            if (maintenanceLogThrottle.ShouldLogRecovery(out int silenced))
            {
                LogMaintenanceRecovered(logger, silenced);
            }

            LogMaintenance(
                logger,
                report.MinuteBucketsWritten,
                report.FiveMinuteBucketsWritten,
                report.RawRowsPurged);
        }
#pragma warning disable CA1031 // Same here: skipped maintenance is recovered on the next round,
        catch (Exception ex) // because the marker does not advance if the transaction fails.
#pragma warning restore CA1031
        {
            // Every thirty seconds, that is 2 880 lines a day: less than the deluge of the
            // write path, but with the same end and for the same reason, that it does not
            // repair itself.
            if (maintenanceLogThrottle.ShouldLog(ex.GetType().FullName ?? "?"))
            {
                LogMaintenanceFailed(logger, ex);
            }
        }
    }

    private void ReportDrops()
    {
        long dropped = buffer.DroppedCount;

        if (dropped == lastReportedDrops)
        {
            return;
        }

        // A history with gaps must be REPORTED: otherwise it is indistinguishable from a history
        // in which nothing happened.
        LogDropped(logger, dropped - lastReportedDrops, dropped);
        lastReportedDrops = dropped;
    }

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Error,
        Message = "History write failed: this round of samples is lost, live monitoring continues.")]
    private static partial void LogFlushFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Error,
        Message = "History maintenance failed: nothing was deleted and consolidation resumes on the next round.")]
    private static partial void LogMaintenanceFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Debug,
        Message = "Maintenance: {MinuteBuckets} one-minute buckets, {FiveMinuteBuckets} five-minute, {RawPurged} raw samples deleted.")]
    private static partial void LogMaintenance(ILogger logger, int minuteBuckets, int fiveMinuteBuckets, int rawPurged);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Warning,
        Message = "History dropped {NewDrops} samples (total {TotalDrops}): the disk writer isn't keeping up with the sampler.")]
    private static partial void LogDropped(ILogger logger, long newDrops, long totalDrops);

    // Warning and not Information: the Windows event log provider, which UseWindowsService
    // registers, passes Warning and above. At Information the log would see the start of the
    // fault and never its end.
    [LoggerMessage(
        EventId = 14,
        Level = LogLevel.Warning,
        Message = "History writing works again ({Silenced} failures were not logged).")]
    private static partial void LogFlushRecovered(ILogger logger, int silenced);

    [LoggerMessage(
        EventId = 15,
        Level = LogLevel.Warning,
        Message = "History maintenance works again ({Silenced} failures were not logged).")]
    private static partial void LogMaintenanceRecovered(ILogger logger, int silenced);
}
