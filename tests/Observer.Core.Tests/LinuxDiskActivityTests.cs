using Observer.Core.Metrics.Disk;
using Observer.Core.Platform;
using Observer.Core.Platform.Linux;

namespace Observer.Core.Tests;

/// <summary>
/// Reading /proc/diskstats and, above all, who stays out.
/// </summary>
/// <remarks>
/// Reading the counters is the easy part. The part that fails silently is deciding
/// WHOSE they are: /proc/diskstats lists whole disks, partitions and virtual devices together,
/// and taking them all would count the same byte two or three times — once on the disk, once
/// on the partition, once on the logical volume. On screen you would see a number bigger than
/// the truth, and nobody would recognize it as wrong.
/// <para>
/// It runs on the Windows runner as well as on the Linux one: the provider does not open files,
/// it asks <see cref="IFileTextReader"/> for them, and here the reader is a fake.
/// </para>
/// </remarks>
public class LinuxDiskActivityTests
{
    // Real lines shortened to the 14 fields that matter: major, minor, name, then the reads
    // (completed, merged, SECTORS, ms), the writes (same), the requests in flight, the busy
    // milliseconds and the weighted time.
    private const string DiskStats =
        """
        259       0 nvme0n1 1000 0 4000 100 500 0 2000 50 0 750 900
        259       1 nvme0n1p1 900 0 3600 90 400 0 1600 40 0 700 800
          8       0 sda 10 0 20 5 3 0 8 2 0 40 45
          8       1 sda1 9 0 18 4 2 0 6 1 0 35 40
          7       0 loop0 5 0 10 1 0 0 0 0 0 3 3
        """;

    private const int SampleLineCount = 5;

    [Fact]
    public void SectorsAreAlways512Bytes()
    {
        // It is not the physical block size: it is a documented kernel contract. A "4K
        // native" disk counts them as 512 all the same, and multiplying by the real size
        // would publish numbers eight times larger.
        DiskStatsLine line = ProcDiskStatsParser.Read(DiskStats).Single(r => r.Device == "nvme0n1");

        Assert.Equal(4_000UL * 512UL, line.BytesRead);
        Assert.Equal(2_000UL * 512UL, line.BytesWritten);
    }

    [Fact]
    public void BusyTimeIsTheTickFieldNotTheSumOfReadAndWrite()
    {
        // On nvme0n1 the sum of the read and write milliseconds is 150; the right field is
        // 750. Taking the sum is the error that on a real window gave 843%.
        DiskStatsLine line = ProcDiskStatsParser.Read(DiskStats).Single(r => r.Device == "nvme0n1");

        Assert.Equal(TimeSpan.FromMilliseconds(750), line.Busy);
    }

    [Theory]
    [InlineData("8 0 sda 1 2 3")]
    [InlineData("8 0 xxx a b c d e f g h i j k l")]
    [InlineData("")]
    public void AnUnparsableLineIsSkippedWithoutDroppingTheOthers(string brokenLine)
    {
        IReadOnlyList<DiskStatsLine> lines = ProcDiskStatsParser.Read(brokenLine + "\n" + DiskStats);

        Assert.Equal(SampleLineCount, lines.Count);
        Assert.Contains(lines, r => r.Device == "sda");
    }

    [Fact]
    public void PartitionsStayOut()
    {
        // A partition counts the same bytes as the disk that holds it. The criterion is not
        // the NAME — "nvme0n1p1" and "sda1" do not even resemble each other, and at the first
        // new naming scheme a list of suffixes would fail silently — but where the
        // kernel puts it: a partition does not appear under /sys/block, it lives inside its
        // disk's directory.
        IReadOnlyList<DiskActivityReading> readings = ReadDisks();

        Assert.DoesNotContain(readings, reading => reading.Instance == "nvme0n1p1");
        Assert.DoesNotContain(readings, reading => reading.Instance == "sda1");
    }

    [Fact]
    public void VirtualDevicesStayOut()
    {
        // loop0 is a whole device in every respect: it has its own /sys/block/loop0/stat.
        // What it does not have is a physical device behind it, and that is the right
        // question to ask.
        IReadOnlyList<DiskActivityReading> readings = ReadDisks();

        Assert.DoesNotContain(readings, reading => reading.Instance == "loop0");
    }

    [Fact]
    public void RealDisksStayIn()
    {
        IReadOnlyList<DiskActivityReading> readings = ReadDisks();

        Assert.Equal(2, readings.Count);
        Assert.Contains(readings, reading => reading.Instance == "nvme0n1");
        Assert.Contains(readings, reading => reading.Instance == "sda");
    }

    [Fact]
    public void TimeIsReportedAsBusyNotAsIdle()
    {
        // Linux counts busy ticks, Windows idle ones. If this provider filled the wrong
        // field, the percentage would come out inverted: an idle disk would show as 100%.
        DiskActivityReading disk = ReadDisks().Single(reading => reading.Instance == "sda");

        Assert.Equal(TimeSpan.FromMilliseconds(40), disk.Busy);
        Assert.Null(disk.Idle);
    }

    [Fact]
    public void WithoutProcDiskstatsTheReadFailsInsteadOfFakingZeroDisks()
    {
        // "I could not read it" and "this machine has no disks" are two different things, and
        // the collector treats them differently: the first one clears the history.
        FakeTextReader reader = new();

        Assert.False(new LinuxDiskActivityProvider(reader)
            .TryRead(out IReadOnlyList<DiskActivityReading> readings));

        Assert.Empty(readings);
    }

    private static IReadOnlyList<DiskActivityReading> ReadDisks()
    {
        FakeTextReader reader = new();
        reader.Put("/proc/diskstats", DiskStats);

        // Here to match reality, not because the filter looks at them: whole devices
        // have their own /sys/block/NAME/stat, and partitions do not appear under /sys/block
        // at all. That is exactly why a check on this file would exclude nothing more — a
        // mutation proved it, removing the check without making anything fail, and it was
        // taken out.
        reader.Put("/sys/block/nvme0n1/stat", "");
        reader.Put("/sys/block/sda/stat", "");
        reader.Put("/sys/block/loop0/stat", "");

        // The real filter: only what has a physical device behind it has device/uevent.
        reader.Put("/sys/block/nvme0n1/device/uevent", "DEVTYPE=nvme");
        reader.Put("/sys/block/sda/device/uevent", "DEVTYPE=scsi_device");

        Assert.True(new LinuxDiskActivityProvider(reader)
            .TryRead(out IReadOnlyList<DiskActivityReading> readings));

        return readings;
    }

    private sealed class FakeTextReader : IFileTextReader
    {
        private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);

        public void Put(string path, string content) => files[path] = content;

        public bool TryReadAllText(string path, out string content)
        {
            if (files.TryGetValue(path, out string? found))
            {
                content = found;

                return true;
            }

            content = string.Empty;

            return false;
        }
    }
}