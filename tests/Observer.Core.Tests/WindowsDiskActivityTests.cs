using Observer.Core.Metrics.Disk;
using Observer.Core.Platform.Windows;

namespace Observer.Core.Tests;

/// <summary>A fact that is SKIPPED outside Windows instead of failing.</summary>
/// <remarks>
/// Twin of <c>SoloSuWindowsAttribute</c> in <c>Observer.Service.Tests/SoloSu.cs</c> (still
/// Italian there until that project is translated): duplicated and not shared because the two
/// test projects do not reference each other, and twenty duplicated lines are a lower price
/// than a dependency between test assemblies.
/// </remarks>
public sealed class WindowsOnlyAttribute : FactAttribute
{
    /// <summary>Skips when the system is not Windows.</summary>
    public WindowsOnlyAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "IOCTL_DISK_PERFORMANCE exists only on Windows: this runs only on windows-latest.";
        }
    }
}

/// <summary>
/// The Windows provider against this machine's REAL disks.
/// </summary>
/// <remarks>
/// It exists for a failure that no synthetic test can see. The
/// <c>DISK_PERFORMANCE</c> struct is 88 bytes, and its last 16 are a name we do not
/// need: writing them as an ANSI string instead of Unicode makes it 80 bytes, and then the IOCTL
/// fails with ERROR_INSUFFICIENT_BUFFER and reads <b>nothing</b>. The provider degrades
/// silently — zero disks, no error — and the dashboard would show an empty section that
/// reads as "this machine has no disks".
/// <para>
/// It really happened, on the first attempt, on every disk. The measurement that found it was
/// made by hand; this test makes it repeatable.
/// </para>
/// </remarks>
public class WindowsDiskActivityTests
{
    [WindowsOnly]
    public void TheProviderSeesThePhysicalDisksOfThisMachine()
    {
        WindowsDiskActivityProvider provider = new();

        Assert.True(provider.TryRead(out IReadOnlyList<DiskActivityReading> readings));

        // If the struct were the wrong size, there would be zero here — with no errors, no
        // exceptions, nothing to give it away.
        Assert.NotEmpty(readings);
    }

    [WindowsOnly]
    public void TheCountersComeFromAMachineThatHasAlreadyBeenBusy()
    {
        WindowsDiskActivityProvider provider = new();

        Assert.True(provider.TryRead(out IReadOnlyList<DiskActivityReading> readings));

        DiskActivityReading first = readings[0];

        // IDLE time is what Windows counts, and on a disk that has been powered on for a
        // while it cannot be zero. If it were, it would mean the field being read is not the
        // right one — that is, that the struct's offsets are shifted, which is the silent
        // way a P/Invoke gets it wrong.
        Assert.True(
            first.Idle > TimeSpan.Zero,
            $"idle time for {first.Instance} is {first.Idle}, which makes no sense on a machine that is powered on");

        Assert.Null(first.Busy);
        Assert.StartsWith("Disk ", first.Instance, StringComparison.Ordinal);
    }
}