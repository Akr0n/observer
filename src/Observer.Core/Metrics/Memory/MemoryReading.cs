using Observer.Core.Units;

namespace Observer.Core.Metrics.Memory;

/// <summary>
/// A memory reading, independent of the platform that produced it.
/// </summary>
/// <param name="Total">Total physical memory.</param>
/// <param name="Available">
/// Memory that is really available for new allocations. It is not "free" memory: on
/// Linux the reusable cache counts as available, and that is the difference between saying
/// "50% used" and "99% used" on the very same machine.
/// </param>
/// <param name="SwapTotal">Total swap space. Zero is a legitimate configuration.</param>
/// <param name="SwapFree">Free swap space.</param>
/// <param name="AvailableWasEstimated">
/// True when <paramref name="Available"/> is an estimate and not a measurement, because the
/// platform does not expose it directly. It must be carried all the way to the UI: presenting
/// an estimate as a measurement is a silent lie.
/// </param>
public readonly record struct MemoryReading(
    ByteSize Total,
    ByteSize Available,
    ByteSize SwapTotal,
    ByteSize SwapFree,
    bool AvailableWasEstimated)
{
    /// <summary>Memory in use, saturated to zero if "available" were greater than "total".</summary>
    public ByteSize Used => Total.SaturatingSubtract(Available);
}
