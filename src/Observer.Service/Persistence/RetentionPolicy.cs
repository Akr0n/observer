namespace Observer.Service.Persistence;

/// <summary>
/// Decides WHAT can be consolidated and WHAT can be deleted. Pure logic, no database: these
/// are the two questions whose wrong answers make nothing fail — one consolidates a bucket
/// halfway through and produces false averages for ever, the other deletes data nobody had
/// aggregated yet.
/// </summary>
public static class RetentionPolicy
{
    /// <summary>
    /// First instant that can NOT be consolidated: the exclusive upper bound of the window
    /// to aggregate now.
    /// </summary>
    /// <param name="nowMs">Now, in milliseconds since the Unix epoch (UTC).</param>
    /// <param name="bucketWidth">Width of the buckets to produce.</param>
    /// <param name="grace">
    /// Extra wait after a bucket closes. It is needed because the last second's samples go
    /// through an in-memory queue and may not be on disk yet.
    /// </param>
    /// <returns>The start of the first bucket that is still untouchable.</returns>
    /// <exception cref="ArgumentOutOfRangeException">If the grace is negative.</exception>
    public static long ConsolidationHorizon(long nowMs, TimeSpan bucketWidth, TimeSpan grace)
    {
        // A negative grace would look into the future and consolidate buckets still empty.
        ArgumentOutOfRangeException.ThrowIfLessThan(grace, TimeSpan.Zero);

        // Aligning "now minus the grace" to the width does two things at once: it excludes
        // the bucket in progress (which is incomplete by definition) and, if the grace
        // reaches back into the previous bucket, it excludes that one too (complete, but it
        // may still have samples in the queue).
        return RollupMath.AlignToBucketStart(nowMs - (long)grace.TotalMilliseconds, bucketWidth);
    }

    /// <summary>
    /// Instant below which deleting is allowed. Everything earlier than it can be removed.
    /// </summary>
    /// <param name="nowMs">Now, in milliseconds since the Unix epoch (UTC).</param>
    /// <param name="retention">How long this level is meant to be kept.</param>
    /// <param name="consolidatedThroughMs">
    /// How far the NEXT level has already aggregated, or null if it has never run.
    /// </param>
    /// <returns>The threshold, or null if nothing must be deleted.</returns>
    /// <exception cref="ArgumentOutOfRangeException">If the retention is not positive.</exception>
    public static long? PurgeCutoff(long nowMs, TimeSpan retention, long? consolidatedThroughMs)
    {
        // A retention of zero would delete at the very instant of writing: the service
        // would run, the file would stay small and the history would always be empty.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);

        if (consolidatedThroughMs is not { } consolidated)
        {
            // The next level has never aggregated anything: there is no summarised copy of
            // this data here yet, so deleting it is a straight loss.
            return null;
        }

        // The constraint that counts is the tighter of the two. Retention says "they are old
        // enough", consolidation says "they are already summarised elsewhere": BOTH are
        // needed, or a rollup that fell behind makes never-aggregated data get deleted.
        return Math.Min(nowMs - (long)retention.TotalMilliseconds, consolidated);
    }
}
