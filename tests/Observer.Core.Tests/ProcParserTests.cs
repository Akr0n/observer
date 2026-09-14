using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Memory;
using Observer.Core.Platform.Linux;

namespace Observer.Core.Tests;

/// <summary>
/// Pure /proc parsers: a string in, a reading out. They do not touch the filesystem, so they
/// run identically on the Windows runner and on the Linux one and CI is green on both.
/// Every test here pins a SILENT bug: none of these errors would crash the service, all of
/// them would produce credible, wrong numbers.
/// </summary>
public class ProcParserTests
{
    [Fact]
    public void ProcStat_AggregateLineWithDoubleSpace_DoesNotMisalignTheColumns()
    {
        // Two traps in a single test:
        // (a) the aggregate "cpu" line has TWO spaces, the "cpuN" lines have ONE. A naive
        //     split shifts the columns and reads "user" where "nice" is.
        // (b) guest and guest_nice (the last two) are ALREADY counted inside user and nice:
        //     adding them again inflates the denominator and underestimates the CPU.
        //           user nice system  idle iowait irq softirq steal guest guest_nice
        const string stat = "cpu  95 0 530 17966 170 0 119 0 0 0\n";

        bool succeeded = ProcStatParser.TryParseAggregate(stat, out CpuTimes times);

        Assert.True(succeeded);
        Assert.Equal(18136L, times.Idle);   // idle 17966 + iowait 170
        Assert.Equal(18880L, times.Total);  // sum of the first EIGHT fields, guest excluded
    }

    [Fact]
    public void ProcStat_OldKernelWithFourColumns_DoesNotThrow()
    {
        // A real case: MSYS2's emulated /proc exposes only four columns. Indexing on the
        // assumption of a fixed length would crash the service at start-up.
        const string stat = "cpu 100 0 200 300\n";

        bool succeeded = ProcStatParser.TryParseAggregate(stat, out CpuTimes times);

        Assert.True(succeeded);
        Assert.Equal(300L, times.Idle);
        Assert.Equal(600L, times.Total);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("cpu\n")]
    [InlineData("cpu foo bar\n")]
    [InlineData("intr 12345 0 0\n")]
    public void ProcStat_DegenerateInput_ReturnsFalseWithoutThrowing(string content)
    {
        // The parser sits at the boundary with data we do not control. It must degrade, not
        // throw: an exception here would bring down the sampling of ALL the metrics.
        bool succeeded = ProcStatParser.TryParseAggregate(content, out CpuTimes _);

        Assert.False(succeeded);
    }

    [Fact]
    public void ProcStat_WithWindowsLineEndings_LeavesNoCarriageReturnInTheNumber()
    {
        // The parsers rely on EnumerateLines stripping the \r from Windows line endings. That
        // is an assumption about somebody else's code: if it stopped holding, the last
        // number of every line would arrive as "0\r", long.TryParse would fail and the CPU
        // would be permanently Unavailable without anything crashing.
        const string stat = "cpu  95 0 530 17966 170 0 119 0 0 0\r\ncpu0 12 0 209 4245 23 0 85 0 0 0\r\n";

        bool succeeded = ProcStatParser.TryParseAggregate(stat, out CpuTimes times);

        Assert.True(succeeded);
        Assert.Equal(18136L, times.Idle);
        Assert.Equal(18880L, times.Total);
    }

    [Fact]
    public void ProcMeminfo_WithWindowsLineEndings_LeavesNoCarriageReturnInTheNumber()
    {
        const string meminfo = "MemTotal:        1048576 kB\r\nMemAvailable:     524288 kB\r\n";

        bool succeeded = ProcMeminfoParser.TryParse(meminfo, out MemoryReading reading);

        Assert.True(succeeded);
        Assert.Equal(1048576L * 1024L, reading.Total.Bytes);
        Assert.Equal(524288L * 1024L, reading.Available.Bytes);
    }

    [Fact]
    public void ProcMeminfo_UsesMemAvailableNotMemFree()
    {
        // The most insidious RAM bug on Linux. MemFree ignores the reusable cache, so using
        // it makes almost every machine look 95-99% full and produces permanent false
        // alarms. Here MemFree would say 99%, MemAvailable says 50%.
        const string meminfo = """
            MemTotal:        1048576 kB
            MemFree:           10240 kB
            MemAvailable:     524288 kB
            """;

        bool succeeded = ProcMeminfoParser.TryParse(meminfo, out MemoryReading reading);

        Assert.True(succeeded);
        Assert.Equal(1048576L * 1024L, reading.Total.Bytes);
        Assert.Equal(524288L * 1024L, reading.Available.Bytes);
        Assert.Equal(524288L * 1024L, reading.Used.Bytes);
        Assert.False(reading.AvailableWasEstimated);
    }

    [Fact]
    public void ProcMeminfo_WithoutMemAvailable_EstimatesAndDeclaresItEstimated()
    {
        // Kernels < 3.14 and partial /proc do not expose MemAvailable. The point is not just
        // to estimate: it is to DECLARE that the reading is an estimate, so the UI can write "approximate"
        // instead of presenting an estimate as a measurement.
        // estimate = Free 10240 + Buffers 2048 + Cached 500000 + SReclaimable 12000 - Shmem 4288
        const string meminfo = """
            MemTotal:        1048576 kB
            MemFree:           10240 kB
            Buffers:            2048 kB
            Cached:           500000 kB
            SReclaimable:      12000 kB
            Shmem:              4288 kB
            """;

        bool succeeded = ProcMeminfoParser.TryParse(meminfo, out MemoryReading reading);

        Assert.True(succeeded);
        Assert.Equal(520000L * 1024L, reading.Available.Bytes);
        Assert.True(reading.AvailableWasEstimated);
    }

    [Fact]
    public void ProcMeminfo_WithoutMemTotal_ReturnsFalse()
    {
        // A total of zero would turn the percentage into a division by zero. Better to
        // declare the sample not credible than to publish an invented one.
        const string meminfo = "MemFree:  10240 kB\nMemAvailable:  524288 kB\n";

        bool succeeded = ProcMeminfoParser.TryParse(meminfo, out MemoryReading _);

        Assert.False(succeeded);
    }

    [Fact]
    public void ProcMeminfo_WithoutSwap_ReportsSwapTotalZero()
    {
        // A machine with no swap is a legitimate configuration, not a fault.
        const string meminfo = "MemTotal: 1048576 kB\nMemAvailable: 524288 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n";

        bool succeeded = ProcMeminfoParser.TryParse(meminfo, out MemoryReading reading);

        Assert.True(succeeded);
        Assert.Equal(0L, reading.SwapTotal.Bytes);
    }
}
/// <summary>
/// Which filesystems end up on screen, read from /proc/self/mountinfo.
/// </summary>
/// <remarks>
/// This is where this metric can go wrong without making any noise. Too wide a filter shows
/// shared memory and container layers as disks — space that does not exist, presented as if it
/// did. Too narrow a filter makes a real disk disappear, and whoever is looking has no way of
/// knowing that it is missing.
/// </remarks>
public class ProcMountInfoParserTests
{
    private static readonly HashSet<string> Allowed =
        new(StringComparer.Ordinal) { "ext4", "xfs", "btrfs", "vfat" };

    [Fact]
    public void KeepsOnlyTheAllowedFilesystems()
    {
        // Real lines from a Linux machine: alongside the real mounts there are dozens of
        // virtual ones. In a container 34 were measured, of which only ONE was a real
        // filesystem.
        const string mountinfo = """
            21 27 0:20 / /sys rw,nosuid,relatime - sysfs sysfs rw
            22 27 0:5 / /proc rw,nosuid,relatime - proc proc rw
            23 27 0:6 / /dev rw,nosuid - devtmpfs udev rw,size=8130636k
            27 1 8:2 / / rw,relatime - ext4 /dev/sda2 rw,errors=remount-ro
            48 27 0:44 / /run/user/1000 rw,nosuid,relatime - tmpfs tmpfs rw,size=1631048k
            60 27 259:1 / /boot/efi rw,relatime - vfat /dev/nvme0n1p1 rw,fmask=0077
            """;

        IReadOnlyList<string> mountPoints = ProcMountInfoParser.MountPoints(mountinfo, Allowed);

        Assert.Equal(["/", "/boot/efi"], mountPoints);
    }

    [Fact]
    public void TheTypeIsLookedUpAfterTheSeparatorNotByCountingFields()
    {
        // THE trap of this format: between the seventh field and the "-" there are ZERO OR
        // MORE optional ones. Counting positions from the start works on the lines with no
        // optional fields and stops working as soon as a shared mount appears, which is the
        // normal case on any system with systemd.
        const string withOptionalFields = """
            27 1 8:2 / / rw,relatime shared:1 master:2 - ext4 /dev/sda2 rw
            """;

        Assert.Equal(["/"], ProcMountInfoParser.MountPoints(withOptionalFields, Allowed));

        // The same line without the optional fields must give the same result.
        const string withoutOptionalFields = """
            27 1 8:2 / / rw,relatime - ext4 /dev/sda2 rw
            """;

        Assert.Equal(["/"], ProcMountInfoParser.MountPoints(withoutOptionalFields, Allowed));
    }

    [Fact]
    public void TheSameFilesystemMountedTwiceAppearsOnce()
    {
        // Bind mounts are normal, and without removing them the same disk would appear twice
        // on screen with exactly the same numbers, as if they were two disks.
        const string mountinfo = """
            27 1 8:2 / /data rw,relatime - ext4 /dev/sda2 rw
            81 27 8:2 /below /data rw,relatime - ext4 /dev/sda2 rw
            """;

        Assert.Equal(["/data"], ProcMountInfoParser.MountPoints(mountinfo, Allowed));
    }

    [Fact]
    public void AMountPointWithASpaceDoesNotDisappear()
    {
        // mountinfo writes the space as \040. Without decoding it the path does not
        // exist, and DriveInfo would fail: the volume would vanish from the list with no
        // error and no reason. It happens with external disks, which often have spaces in
        // their name.
        const string mountinfo = """
            27 1 8:2 / /media/someone/External\040Disk rw,relatime - vfat /dev/sdb1 rw
            """;

        Assert.Equal(["/media/someone/External Disk"], ProcMountInfoParser.MountPoints(mountinfo, Allowed));
    }

    [Fact]
    public void AMalformedLineIsSkippedWithoutTakingTheOthersWithIt()
    {
        const string mountinfo = """
            this is not a mountinfo line
            27 1 8:2 / / rw,relatime - ext4 /dev/sda2 rw
            36 27
            """;

        Assert.Equal(["/"], ProcMountInfoParser.MountPoints(mountinfo, Allowed));
    }

    [Fact]
    public void AnEmptyFileGivesAnEmptyListNotAnError()
    {
        Assert.Empty(ProcMountInfoParser.MountPoints(string.Empty, Allowed));
    }
}