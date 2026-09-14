using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Units;

namespace Observer.Core.Tests;

/// <summary>
/// The heart of the project: two counter samples in, one percentage out.
/// No hardware, no I/O, no waiting on real time. If these tests are green the maths is
/// correct on both platforms, because the unit of the ticks cancels out in the ratio and
/// it makes no difference whether they are Linux jiffies or Windows 100 ns intervals.
/// </summary>
public class CpuUsageMathTests
{
    [Fact]
    public void TryComputePercent_HalfLoadOverAKnownWindow_Returns50()
    {
        // Window: total +1000 ticks, 500 of them idle. Busy = 500/1000 = 50%.
        CpuTimes previous = new(Idle: 1000L, Total: 2000L);
        CpuTimes current = new(Idle: 1500L, Total: 3000L);

        bool succeeded = CpuUsage.TryComputePercent(previous, current, out Percent usage, out SampleFailure _);

        Assert.True(succeeded);
        Assert.Equal(50.0, usage.Points);
    }

    [Fact]
    public void TryComputePercent_TwoIdenticalSamples_FailsWithNoElapsedTime()
    {
        // On Windows the counters only advance on the clock tick (~15.6 ms): two readings
        // close together give a delta of EXACTLY zero. Without this guard it would be
        // 0/0 = NaN, that is invalid JSON and a broken dashboard.
        CpuTimes sample = new(Idle: 1000L, Total: 2000L);

        bool succeeded = CpuUsage.TryComputePercent(sample, sample, out Percent _, out SampleFailure reason);

        Assert.False(succeeded);
        Assert.Equal(SampleFailure.NoElapsedTime, reason);
    }

    [Fact]
    public void TryComputePercent_CountersWentBackwards_FailsWithCounterWentBackwards()
    {
        // After a suspend/resume or a VM migration the delta is negative.
        CpuTimes previous = new(Idle: 5000L, Total: 9000L);
        CpuTimes current = new(Idle: 1000L, Total: 2000L);

        bool succeeded = CpuUsage.TryComputePercent(previous, current, out Percent _, out SampleFailure reason);

        Assert.False(succeeded);
        Assert.Equal(SampleFailure.CounterWentBackwards, reason);
    }

    [Fact]
    public void TryComputePercent_IdleGrowsPastTheTotal_FailsInsteadOfPublishingANegative()
    {
        // Inconsistent window: both deltas are POSITIVE, so the guard on counters going
        // backwards does not fire, but idle grows more than the total and "busy" comes out
        // negative. It really happens: on Linux "steal" going backwards after a live migration
        // is enough (iowait cancels out on both sides and escapes the first check); on
        // Windows the per-processor aggregation of GetSystemTimes is not atomic and idle can
        // end up ahead of kernel. On a chart a -3% passes for noise: it is a wrong number
        // published as valid, which is exactly what must not happen.
        CpuTimes previous = new(Idle: 500L, Total: 1000L);
        CpuTimes current = new(Idle: 520L, Total: 1010L);

        bool succeeded = CpuUsage.TryComputePercent(previous, current, out Percent usage, out SampleFailure reason);

        Assert.False(succeeded);
        Assert.Equal(SampleFailure.CounterWentBackwards, reason);
        Assert.Equal(0.0, usage.Points);
    }

    [Fact]
    public void Describe_EveryFailureReason_HasItsOwnNonEmptyExplanation()
    {
        // The dashboard has to tell the operator WHY the reading is missing, rather than leave
        // an unexplained gap. I do not check the exact words (that would be a test that breaks
        // at every rewrite of the text): I check that an explanation is there and that
        // different reasons do not all collapse onto the same generic sentence.
        string wentBackwards = SampleFailureText.Describe(SampleFailure.CounterWentBackwards);
        string noElapsedTime = SampleFailureText.Describe(SampleFailure.NoElapsedTime);

        Assert.False(string.IsNullOrWhiteSpace(wentBackwards));
        Assert.False(string.IsNullOrWhiteSpace(noElapsedTime));
        Assert.NotEqual(wentBackwards, noElapsedTime);
    }

    [Fact]
    public void SampleFailure_ZeroValue_IsUnknownAndNotARealReason()
    {
        // default(SampleFailure) must not pass itself off as a diagnosed cause: a zero that
        // means "CounterWentBackwards" would show a diagnosis that was never made.
        Assert.Equal(SampleFailure.Unknown, default(SampleFailure));
    }
}
