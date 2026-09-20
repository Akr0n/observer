using System.Diagnostics;
using Observer.Core.Metrics;
using Observer.Service.Persistence;

namespace Observer.Service;

/// <summary>
/// Keeps the latest sample. The HTTP endpoints read FROM HERE and never call the collectors
/// directly.
/// </summary>
/// <remarks>
/// This is not a performance detail, it is a defence against concurrency. The CPU collector
/// keeps the previous sample in a field: two simultaneous collections would compute wrong
/// percentages, intermittently and plausibly, the worst kind of bug. With a single sampler
/// and reads from the cache, that situation cannot happen.
/// </remarks>
/// <remarks>
/// It also knows HOW OLD what it is holding is, and that is not bookkeeping: the endpoint reads
/// from here and never samples, so a sampling loop that dies leaves this cache handing out the
/// same snapshot for ever. Served with a 200 it reads as current, and a machine that has stopped
/// measuring then looks healthier on a dashboard than one that is switched off.
/// <para>
/// The age is taken from a MONOTONIC counter - <see cref="TimeProvider.GetTimestamp"/>, which on
/// <see cref="TimeProvider.System"/> is the counter behind <see cref="Stopwatch"/>, the one the
/// sampler below times its own rounds on; they are the same counter in the running service, and
/// a test that injects its own provider moves this one alone. It is never taken from the snapshot's
/// <c>CapturedAt</c>, which is a wall clock. A wall clock steps: NTP corrects it, a virtual
/// machine resumes with it behind, somebody sets it by hand. Any of those would invent a stale
/// snapshot out of a perfectly healthy sampler, or hide a dead one. Here there is one process,
/// one counter, and the difference between two of its readings.
/// </para>
/// </remarks>
public sealed class MetricSnapshotCache
{
    /// <summary>
    /// How long the same sample may be the newest one before the service stops calling it
    /// current.
    /// </summary>
    /// <remarks>
    /// Fifteen seconds, that is fifteen missed rounds at the sampler's 1 Hz. It is deliberately
    /// well above one round: a round is ALLOWED to overrun the period - the sampler logs it when
    /// it does, precisely because it happens on a loaded machine - so a threshold of a handful of
    /// periods would fire on ordinary load. Fifteen seconds of nothing is not a slow round, it is
    /// a loop that is not running. The cost of the margin is that the dashboard learns fifteen
    /// seconds later, against a fault that otherwise lasts until somebody restarts the service.
    /// </remarks>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

    /// <summary>A sample and the instant it was published, inseparably.</summary>
    /// <remarks>
    /// One reference and not two fields, and that is the whole concurrency argument. With a
    /// snapshot and a stamp written separately, a reader can land between the two writes and
    /// pair one publish's snapshot with another's stamp - in one of the two orders that skews
    /// the age UP, which is harmless, and in the other DOWN, which is not. Neither can happen
    /// to a single reference: what is read is what was written.
    /// </remarks>
    private sealed record Published(MachineSnapshot Snapshot, long At);

    private readonly TimeProvider time;
    private Published? published;

    /// <summary>Creates the cache on a clock.</summary>
    /// <param name="time">Where the monotonic timestamps come from.</param>
    public MetricSnapshotCache(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        this.time = time;
    }

    /// <summary>The latest sample, or null if it has not happened yet.</summary>
    public MachineSnapshot? Latest => Volatile.Read(ref published)?.Snapshot;

    /// <summary>The latest sample and how long ago it was published.</summary>
    /// <returns>The sample, null if there has never been one, and its age.</returns>
    /// <remarks>
    /// Both in one call, deliberately, and from one read: asked for separately they could come
    /// from two different publishes, and the age would belong to a snapshot other than the one
    /// returned. The age of a cache that has never published is zero and means nothing - the
    /// null is what the caller has to look at, and it comes first.
    /// </remarks>
    public (MachineSnapshot? Snapshot, TimeSpan Age) Read()
    {
        Published? current = Volatile.Read(ref published);

        return current is null ? (null, TimeSpan.Zero) : (current.Snapshot, time.GetElapsedTime(current.At));
    }

    /// <summary>Publishes a new sample.</summary>
    public void Publish(MachineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Volatile.Write(ref published, new Published(snapshot, time.GetTimestamp()));
    }
}

/// <summary>
/// The process's ONLY sampler. Polls the collectors at a fixed interval and publishes the
/// result to the cache.
/// </summary>
public sealed partial class MetricSamplingService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <remarks>
    /// Below the sampling period, so that one slow source does not make all the others late.
    /// MIND the real limit: a P/Invoke or a WMI query that hangs does NOT observe the
    /// cancellation token. The thread stays busy and the deadline only holds from the
    /// sampler's point of view. For sources known to hang — SMART, WMI — the real answer is
    /// a separate process, not a CancellationToken.
    /// </remarks>
    private static readonly TimeSpan CollectorTimeout = TimeSpan.FromMilliseconds(750);

    private readonly IReadOnlyList<IMetricCollector> collectors;
    private readonly LogThrottle[] collectorThrottles;
    private readonly LogThrottle longRoundThrottle = new();
    private readonly MetricSnapshotCache cache;
    private readonly IMetricSnapshotSink sink;
    private readonly ILogger<MetricSamplingService> logger;

    /// <summary>Creates the sampler.</summary>
    /// <param name="collectors">The sources to poll.</param>
    /// <param name="cache">Where to publish the latest sample for the endpoints.</param>
    /// <param name="sink">
    /// Where to drop the same sample so that it ends up in the history. It only drops it: if
    /// it waited for the disk, a slow fsync would not make the chart slow, it would falsify
    /// the CPU percentage of the next reading, which is computed on the DISTANCE between two
    /// samples.
    /// </param>
    /// <param name="logger">Where to report slow or faulted collectors.</param>
    public MetricSamplingService(
        IReadOnlyList<IMetricCollector> collectors,
        MetricSnapshotCache cache,
        IMetricSnapshotSink sink,
        ILogger<MetricSamplingService> logger)
    {
        ArgumentNullException.ThrowIfNull(collectors);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(logger);

        this.collectors = collectors;
        this.cache = cache;
        this.sink = sink;
        this.logger = logger;

        // One throttle per source, by index and not by Id: two collectors with the same Id
        // would break a dictionary, and there is nothing to gain here by risking it.
        collectorThrottles = [.. collectors.Select(_ => new LogThrottle())];
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hands control back to the host immediately: this way a slow first collection delays
        // the first metric, not the opening of the HTTP port.
        await Task.Yield();

        using PeriodicTimer timer = new(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            long startTimestamp = Stopwatch.GetTimestamp();

            MachineSnapshot snapshot = await CollectAllAsync(stoppingToken).ConfigureAwait(false);

            // A round longer than the period drops a tick, and PeriodicTimer drops it in
            // SILENCE: the sample is not there, and downstream it reads as a moment in which
            // nothing was being measured - indistinguishable from a machine that is off. If it
            // happens, let it at least be written down somewhere which source stretched the
            // round. Written ONCE: a machine that stays under load stretches every round, and
            // the message at 1 Hz is 86 400 lines a day in the event log.
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);

            if (elapsed > Interval)
            {
                if (longRoundThrottle.ShouldLog("long"))
                {
                    LogRoundTooLong(logger, elapsed.TotalMilliseconds, Interval.TotalMilliseconds);
                }
            }
            else if (longRoundThrottle.ShouldLogRecovery(out int silencedRounds))
            {
                LogRoundsBackOnTime(logger, silencedRounds);
            }

            cache.Publish(snapshot);

            // The SAME object also goes to the history: this way the chart of an instant and
            // the tile of the present cannot show different numbers. Enqueue does not wait
            // for the disk, by construction.
            sink.Enqueue(snapshot);

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown requested: normal exit, not an error.
                return;
            }
        }
    }

    /// <summary>
    /// Polls ALL the sources together and waits for them to finish.
    /// </summary>
    /// <remarks>
    /// <b>Together, not one after the other, and the difference grows with every new source.</b>
    /// In sequence the round lasts the SUM of the times, so the worst case is the number of
    /// collectors multiplied by <see cref="CollectorTimeout"/>: with two it already exceeds
    /// one second, with five it is four times that. And a round longer than the period makes no
    /// noise - <see cref="PeriodicTimer"/> drops the ticks in silence, the samples disappear,
    /// and the history strip declares "not measured" an hour in which the machine was on and
    /// healthy. Together, the worst case is the slowest collector, and it stays under the
    /// period by construction.
    /// <para>
    /// It does not introduce the concurrency <see cref="MetricSnapshotCache"/> fears: that one
    /// comes from two collections <i>of the same</i> collector overlapping, and here every
    /// source is polled exactly once per round. It is the loop that stays single, not the queue.
    /// </para>
    /// </remarks>
    private async Task<MachineSnapshot> CollectAllAsync(CancellationToken cancellationToken)
    {
        // The order of the results stays that of the collectors, because WhenAll preserves
        // the order of the tasks: the tiles on screen do not swap places on every round.
        MetricSnapshot[] results = await Task.WhenAll(
            collectors.Select((collector, index) => CollectOneAsync(collector, collectorThrottles[index], cancellationToken)))
            .ConfigureAwait(false);

        return new MachineSnapshot(MachineSnapshot.CurrentSchemaVersion, DateTimeOffset.UtcNow, results);
    }

    /// <summary>Polls one source, and never lets a fault of its own through.</summary>
    private async Task<MetricSnapshot> CollectOneAsync(
        IMetricCollector collector,
        LogThrottle throttle,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource attempt =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        attempt.CancelAfter(CollectorTimeout);

        try
        {
            MetricSnapshot result = await collector.CollectAsync(attempt.Token).ConfigureAwait(false);

            if (throttle.ShouldLogRecovery(out int silenced))
            {
                LogCollectorRecovered(logger, collector.Id, silenced);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Service shutting down: propagate, it is not a fault of the metric.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Time ran out: the source is slow, not broken. The others carry on. And it is
            // said once: a source that times out once times out on every round.
            if (throttle.ShouldLog("timeout"))
            {
                LogCollectorTimedOut(logger, collector.Id, CollectorTimeout.TotalMilliseconds);
            }

            return new MetricSnapshot(
                collector.Id,
                CollectorStatus.Unavailable,
                FormattableString.Invariant(
                    $"the source didn't respond within {CollectorTimeout.TotalMilliseconds} ms and was skipped for this round"),
                []);
        }
#pragma warning disable CA1031 // A collector that blows up must degrade one tile, not bring
        catch (Exception ex) // down the sampling of all the other metrics.
#pragma warning restore CA1031
        {
            // The TYPE of the exception as the reason, not the message: a message that carries
            // a path or a counter inside changes on every round and would throttle nothing.
            if (throttle.ShouldLog(ex.GetType().FullName ?? "?"))
            {
                LogCollectorFaulted(logger, collector.Id, ex);
            }

            return new MetricSnapshot(collector.Id, CollectorStatus.Faulted, ex.Message, []);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Collector {CollectorId} threw an exception: its metric is degraded, everything else continues.")]
    private static partial void LogCollectorFaulted(ILogger logger, string collectorId, Exception exception);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "A sampling round took {ElapsedMs} ms, longer than the {IntervalMs} ms period: at least one sample was skipped, and a skipped sample is indistinguishable from a machine that was off.")]
    private static partial void LogRoundTooLong(ILogger logger, double elapsedMs, double intervalMs);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Collector {CollectorId} exceeded {TimeoutMs} ms: skipped for this round, the other metrics continue.")]
    private static partial void LogCollectorTimedOut(ILogger logger, string collectorId, double timeoutMs);

    // Warning and not Information, and this is not pedantry: UseWindowsService registers the
    // event log provider, which passes Warning and above. At Information these lines would
    // NEVER reach the Windows log, and the log would be left with the start of the fault
    // and no end - that is, exactly the misunderstanding they are there to remove.
    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "Sampling rounds are back within the period ({Silenced} late rounds were not logged).")]
    private static partial void LogRoundsBackOnTime(ILogger logger, int silenced);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "Collector {CollectorId} is answering again ({Silenced} failures were not logged).")]
    private static partial void LogCollectorRecovered(ILogger logger, string collectorId, int silenced);
}