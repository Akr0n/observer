using Observer.Core.Units;

namespace Observer.Core.Metrics.Disk;

/// <summary>
/// The space of ONE mounted volume.
/// </summary>
/// <param name="Instance">
/// What the volume is called for whoever is looking: <c>C:</c> on Windows, the mount point on
/// Linux. It is also the instance the points are published under, so it must stay stable from
/// one sample to the next: if it changed, the row on screen would be rebuilt every time and the
/// history would break into two different series.
/// </param>
/// <param name="Total">Capacity of the volume.</param>
/// <param name="Free">
/// Space available <b>to this user</b>. On a volume with quotas it does not coincide with the
/// disk's free space, and it is the right number anyway: it says how much can still be written
/// there, which is the question whoever is looking is asking.
/// </param>
public readonly record struct DiskReading(string Instance, ByteSize Total, ByteSize Free)
{
    /// <summary>Space used, saturated to zero if "free" were to exceed "total".</summary>
    /// <remarks>
    /// The subtraction saturates for the same reason as memory: on a volume with quotas or with
    /// reserved blocks the two numbers come from different counters, and a negative difference
    /// would produce an absurd percentage instead of a missing number.
    /// </remarks>
    public ByteSize Used => Total.SaturatingSubtract(Free);

    /// <summary>How full it is, from 0 to 1, or null if the capacity is not known.</summary>
    /// <remarks>
    /// Null and not zero when the total is zero: a volume of zero capacity is not "empty", it is
    /// a volume whose size is not known — it happens on special mounts and on devices that
    /// unmount while they are being read. Zero would say "there is all the space in the world",
    /// which is exactly the opposite.
    /// </remarks>
    public double? Fraction => Total.Bytes > 0L ? (double)Used.Bytes / Total.Bytes : null;
}

/// <summary>
/// Reading port for disk space.
/// </summary>
/// <remarks>
/// Returns a list because there is more than one volume and they change while the program runs:
/// a USB stick appears, a network disk disappears. A volume that cannot be queried does not make
/// the others fail — the list comes back with the ones that were read, and what is missing is
/// missing.
/// </remarks>
public interface IDiskReadingProvider
{
    /// <summary>False when on this platform nothing is measured at all.</summary>
    bool IsSupported { get; }

    /// <summary>Why it is not measured, when it is not measured.</summary>
    string? UnsupportedReason { get; }

    /// <summary>Reads the volumes. False when the reading fails entirely.</summary>
    bool TryRead(out IReadOnlyList<DiskReading> readings);
}