using Observer.Core.Units;

namespace Observer.Core.Metrics.Cpu;

/// <summary>
/// Cumulative CPU time counters read from the platform. The unit of the ticks is deliberately
/// not declared: on Linux they are jiffies, on Windows 100 ns intervals, and in the ratio
/// between two differences the unit cancels out. That is what makes the maths identical on
/// the two platforms.
/// </summary>
/// <param name="Idle">Cumulative time spent idle.</param>
/// <param name="Total">Total cumulative time, idle included.</param>
public readonly record struct CpuTimes(long Idle, long Total);

/// <summary>
/// Pure CPU usage maths. It opens no file, calls no OS and waits for nothing: two samples
/// in, one percentage out. This is the point where testability without hardware is bought.
/// </summary>
public static class CpuUsage
{
    /// <summary>
    /// Computes the percentage of busy CPU between two cumulative samples.
    /// Returns false, setting <paramref name="failure"/>, when the window is not usable:
    /// a missing value with a reason is preferable to an invented number.
    /// </summary>
    public static bool TryComputePercent(
        CpuTimes previous,
        CpuTimes current,
        out Percent percent,
        out SampleFailure failure)
    {
        long deltaTotal = current.Total - previous.Total;
        long deltaIdle = current.Idle - previous.Idle;

        if (deltaTotal < 0L || deltaIdle < 0L)
        {
            percent = default;
            failure = SampleFailure.CounterWentBackwards;
            return false;
        }

        if (deltaTotal == 0L)
        {
            percent = default;
            failure = SampleFailure.NoElapsedTime;
            return false;
        }

        long busy = deltaTotal - deltaIdle;

        // Both deltas can be positive and the window still be inconsistent: if idle grows more
        // than the total, "busy" is negative. It happens when "steal" goes backwards after a
        // live migration (iowait cancels out on both sides and escapes the guard above), or on
        // Windows because the per-processor aggregation of GetSystemTimes is not atomic.
        // Without this check a -3% marked Ok would be published, which on a chart passes for
        // noise: a wrong and credible number, which is the worst case.
        if (busy < 0L)
        {
            percent = default;
            failure = SampleFailure.CounterWentBackwards;
            return false;
        }

        if (!Percent.TryFromRatio((double)busy / deltaTotal, out percent))
        {
            percent = default;
            failure = SampleFailure.NotFinite;
            return false;
        }

        failure = SampleFailure.Unknown;
        return true;
    }
}