using System.Globalization;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// The two decisions that make no noise when they are wrong: consolidating a bucket that is
/// still open (false averages for ever, because the raw data disappears afterwards) and deleting
/// raw data that nothing has aggregated yet (a hole in the history nobody can rebuild).
/// </summary>
public class RetentionPolicyTests
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FiveSecondGrace = TimeSpan.FromSeconds(5);

    private static long Ms(string isoInstant) =>
        DateTimeOffset.Parse(isoInstant, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUnixTimeMilliseconds();

    [Fact]
    public void Horizon_DoesNotConsolidateTheInProgressBucket()
    {
        // At 12:03:47 the 12:03 minute is not over: aggregating it now would write an average
        // over 47 samples instead of 60, and since the raw data will be deleted that number
        // would stay wrong for ever.
        long horizon = RollupMath.AlignToBucketStart(
            RetentionPolicy.ConsolidationHorizon(Ms("2026-08-26T12:03:47Z"), OneMinute, TimeSpan.Zero),
            OneMinute);

        Assert.Equal(Ms("2026-08-26T12:03:00Z"), horizon);
    }

    [Fact]
    public void Horizon_WaitsOutTheGraceAfterTheBucketCloses()
    {
        // The 12:02 minute closed at 12:03:00, that is two seconds ago. The samples from its
        // last instants are still in the in-memory buffer: consolidating it now means losing
        // them.
        long horizon = RetentionPolicy.ConsolidationHorizon(
            Ms("2026-08-26T12:03:02Z"), OneMinute, FiveSecondGrace);

        Assert.Equal(Ms("2026-08-26T12:02:00Z"), horizon);
    }

    [Fact]
    public void Horizon_ConsolidatesABucketClosedLongerThanTheGrace()
    {
        long horizon = RetentionPolicy.ConsolidationHorizon(
            Ms("2026-08-26T12:03:07Z"), OneMinute, FiveSecondGrace);

        Assert.Equal(Ms("2026-08-26T12:03:00Z"), horizon);
    }

    [Fact]
    public void Horizon_WithNoGraceStopsAtTheCurrentBucketStart()
    {
        long horizon = RetentionPolicy.ConsolidationHorizon(
            Ms("2026-08-26T12:03:07Z"), OneMinute, TimeSpan.Zero);

        Assert.Equal(Ms("2026-08-26T12:03:00Z"), horizon);
    }

    [Fact]
    public void Horizon_RejectsANegativeGrace()
    {
        // A negative grace period would consolidate buckets from the FUTURE, which are still empty.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RetentionPolicy.ConsolidationHorizon(0L, OneMinute, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Cutoff_WithNothingConsolidatedDeletesNothing()
    {
        // If the rollup has never run, every deletion is a dead loss: no aggregate holds those
        // numbers. A file that grows is better than a hole in the history.
        long? cutoff = RetentionPolicy.PurgeCutoff(
            Ms("2026-08-26T12:00:00Z"), TimeSpan.FromHours(6), consolidatedThroughMs: null);

        Assert.Null(cutoff);
    }

    [Fact]
    public void Cutoff_NeverGoesPastWhatHasBeenConsolidated()
    {
        // THE retention test. The rollup has fallen behind (service stopped, slow disk,
        // restart): retention alone would delete up to 06:00, but from 03:00 onwards nothing
        // has been aggregated yet. Deleting there just loses that data, with no error and
        // nothing in the log.
        long? cutoff = RetentionPolicy.PurgeCutoff(
            Ms("2026-08-26T12:00:00Z"),
            TimeSpan.FromHours(6),
            Ms("2026-08-26T03:00:00Z"));

        Assert.Equal(Ms("2026-08-26T03:00:00Z"), cutoff);
    }

    [Fact]
    public void Cutoff_UsesTheRetentionWindowWhenConsolidationIsAhead()
    {
        long? cutoff = RetentionPolicy.PurgeCutoff(
            Ms("2026-08-26T12:00:00Z"),
            TimeSpan.FromHours(6),
            Ms("2026-08-26T11:00:00Z"));

        Assert.Equal(Ms("2026-08-26T06:00:00Z"), cutoff);
    }

    [Fact]
    public void Cutoff_RejectsANonPositiveRetention()
    {
        // A retention of zero would delete the data at the same instant it writes it: the
        // service would run, the file would stay small and the history would always be empty.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RetentionPolicy.PurgeCutoff(0L, TimeSpan.Zero, 0L));
    }
}
