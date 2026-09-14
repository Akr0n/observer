using Observer.Core.Processes;
using Observer.Core.Units;

namespace Observer.Core.Tests;

/// <summary>
/// Who uses what, and the ways that number can lie.
/// </summary>
/// <remarks>
/// A process's memory is read and shown. CPU is not: it is a rate, so it comes out of two
/// samples, and a wrong number here does not look like an error — it looks like a culprit.
/// Blaming the wrong program for 90% of the CPU gets the wrong thing killed.
/// </remarks>
public class ProcessRankingTests
{
    private const int Cores = 4;

    private static ProcessTimes ProcessSample(
        int pid, string name, double cpuSeconds, long memoryBytes, ulong? ioBytes = null) =>
        new(pid, name, TimeSpan.FromSeconds(cpuSeconds), ByteSize.FromBytes(memoryBytes), ioBytes);

    [Fact]
    public void OnTheFirstRoundMemoryIsThereAndCpuIsNot()
    {
        // Zero would be a lie: it is not "this process is not working", it is "I do not know
        // yet". On screen the difference is between a dash and a zero, and those are two
        // different things.
        Bench harness = new([ProcessSample(10, "notepad", 5, 100_000)]);

        Assert.True(harness.Ranking.TryRead(out IReadOnlyList<ProcessUsage> first));

        ProcessUsage single = Assert.Single(first);
        Assert.Null(single.CpuPercent);
        Assert.Equal(100_000L, single.WorkingSet.Bytes);
    }

    [Fact]
    public void FromTheSecondRoundCpuIsAcrossTheWholeMachine()
    {
        // Half a second of processor in one second, on four cores, is 12.5% of the machine -
        // not 50%. If it were relative to a single core, a process at full tilt on a 16-core
        // machine would say 100% and the machine would look saturated.
        Bench harness = new([ProcessSample(10, "notepad", 5, 100_000)]);

        harness.Ranking.TryRead(out _);
        harness.Advance([ProcessSample(10, "notepad", 5.5, 100_000)]);

        Assert.True(harness.Ranking.TryRead(out IReadOnlyList<ProcessUsage> second));
        Assert.Equal(12.5d, Assert.Single(second).CpuPercent!.Value, 6);
    }

    [Fact]
    public void AReusedPidDoesNotInheritTheDeadProcessCpuTime()
    {
        // The real trap. The system hands the numbers out again: if PID 10 was "notepad" with 5
        // seconds of processor time and is now "chrome" with 300, the difference would credit
        // chrome with time spent by someone else - and would put it at the top of the list,
        // which is exactly where whoever is looking decides what to kill.
        Bench harness = new([ProcessSample(10, "notepad", 5, 100_000)]);

        harness.Ranking.TryRead(out _);
        harness.Advance([ProcessSample(10, "chrome", 300, 100_000)]);

        Assert.True(harness.Ranking.TryRead(out IReadOnlyList<ProcessUsage> second));
        Assert.Null(Assert.Single(second).CpuPercent);
    }

    [Fact]
    public void ATimeThatGoesBackwardsProducesNoPercentage()
    {
        Bench harness = new([ProcessSample(10, "notepad", 5, 100_000)]);

        harness.Ranking.TryRead(out _);
        harness.Advance([ProcessSample(10, "notepad", 4, 100_000)]);

        Assert.True(harness.Ranking.TryRead(out IReadOnlyList<ProcessUsage> second));
        Assert.Null(Assert.Single(second).CpuPercent);
    }

    [Fact]
    public void ThePercentageDoesNotGoAbove100()
    {
        // Eight seconds of processor in one second on four cores: twice what is possible. It
        // happens because the sampling clock and the counters' clock are not the same clock,
        // and it is the same reason the disks' busy time is clamped.
        Bench harness = new([ProcessSample(10, "build", 0, 100_000)]);

        harness.Ranking.TryRead(out _);
        harness.Advance([ProcessSample(10, "build", 8, 100_000)]);

        Assert.True(harness.Ranking.TryRead(out IReadOnlyList<ProcessUsage> second));
        Assert.Equal(100d, Assert.Single(second).CpuPercent!.Value);
    }

    [Fact]
    public void AFailedReadClearsTheHistory()
    {
        Bench harness = new([ProcessSample(10, "notepad", 5, 100_000)]);

        harness.Ranking.TryRead(out _);

        harness.Lister.Readable = false;
        harness.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.False(harness.Ranking.TryRead(out IReadOnlyList<ProcessUsage> broken));
        Assert.Empty(broken);

        harness.Lister.Readable = true;
        harness.Advance([ProcessSample(10, "notepad", 500, 100_000)]);

        // Without the reset, the 495 seconds accumulated during the gap would be divided by the
        // last second and notepad would come out as the culprit for everything.
        Assert.True(harness.Ranking.TryRead(out IReadOnlyList<ProcessUsage> resumed));
        Assert.Null(Assert.Single(resumed).CpuPercent);
    }

    [Fact]
    public void AProcessWithoutAPercentageYetGoesToTheBottom()
    {
        List<ProcessUsage> all =
        [
            new(1, "unknown", null, ByteSize.FromBytes(10)),
            new(2, "idle", 0d, ByteSize.FromBytes(10)),
            new(3, "hungry", 80d, ByteSize.FromBytes(10)),
        ];

        IReadOnlyList<ProcessUsage> sorted = ProcessRanking.TopByCpu(all, 3);

        Assert.Equal(["hungry", "idle", "unknown"], sorted.Select(process => process.Name));
    }

    [Fact]
    public void OnTheFirstRoundIoIsNotThere()
    {
        Bench harness = new([ProcessSample(10, "copy", 5, 100_000, ioBytes: 1_000_000)]);

        Assert.True(harness.Ranking.TryRead(out IReadOnlyList<ProcessUsage> first));
        Assert.Null(Assert.Single(first).IoBytesPerSecond);
    }

    [Fact]
    public void FromTheSecondRoundIoIsARateInBytesPerSecond()
    {
        // Half a megabyte more in one second: 500,000 bytes per second, whatever the number of
        // cores - unlike CPU, here the whole machine divides nothing.
        Bench harness = new([ProcessSample(10, "copy", 5, 100_000, ioBytes: 1_000_000)]);

        harness.Ranking.TryRead(out _);
        harness.Advance([ProcessSample(10, "copy", 5, 100_000, ioBytes: 1_500_000)]);

        Assert.True(harness.Ranking.TryRead(out IReadOnlyList<ProcessUsage> second));
        Assert.Equal(500_000d, Assert.Single(second).IoBytesPerSecond!.Value, 6);
    }

    [Fact]
    public void AReusedPidDoesNotInheritTheDeadProcessIo()
    {
        // The same trap as CPU, guarded the same way: the name. A dead "backup" with a
        // terabyte transferred and an "editor" that took its place must not produce a rate.
        Bench harness = new([ProcessSample(10, "backup", 5, 100_000, ioBytes: 1_000_000_000_000)]);

        harness.Ranking.TryRead(out _);
        harness.Advance([ProcessSample(10, "editor", 5, 100_000, ioBytes: 1_000_000_000_500)]);

        Assert.True(harness.Ranking.TryRead(out IReadOnlyList<ProcessUsage> second));
        Assert.Null(Assert.Single(second).IoBytesPerSecond);
    }

    [Fact]
    public void AnIoCounterThatGoesBackwardsProducesNoRate()
    {
        Bench harness = new([ProcessSample(10, "copy", 5, 100_000, ioBytes: 1_000_000)]);

        harness.Ranking.TryRead(out _);
        harness.Advance([ProcessSample(10, "copy", 5, 100_000, ioBytes: 900_000)]);

        Assert.True(harness.Ranking.TryRead(out IReadOnlyList<ProcessUsage> second));
        Assert.Null(Assert.Single(second).IoBytesPerSecond);
    }

    [Theory]
    [InlineData(null, 1_000_000UL)]
    [InlineData(1_000_000UL, null)]
    [InlineData(null, null)]
    public void AMissingCounterOnEitherSideLeavesTheRateUnknown(ulong? before, ulong? after)
    {
        // On Linux the counter of other users' processes cannot be read, and a process can
        // become readable - or stop being readable - between one round and the next. A single
        // sample is not enough.
        Bench harness = new([ProcessSample(10, "copy", 5, 100_000, before)]);

        harness.Ranking.TryRead(out _);
        harness.Advance([ProcessSample(10, "copy", 5, 100_000, after)]);

        Assert.True(harness.Ranking.TryRead(out IReadOnlyList<ProcessUsage> second));
        Assert.Null(Assert.Single(second).IoBytesPerSecond);
    }

    [Fact]
    public void IoDoesNotTouchCpuAndCpuDoesNotTouchIo()
    {
        // The two rates are computed from the same history but do not affect each other: a
        // missing I/O counter must not wipe out a valid CPU percentage.
        Bench harness = new([ProcessSample(10, "copy", 5, 100_000)]);

        harness.Ranking.TryRead(out _);
        harness.Advance([ProcessSample(10, "copy", 5.5, 100_000, ioBytes: 10)]);

        Assert.True(harness.Ranking.TryRead(out IReadOnlyList<ProcessUsage> second));

        ProcessUsage single = Assert.Single(second);
        Assert.Equal(12.5d, single.CpuPercent!.Value, 6);
        Assert.Null(single.IoBytesPerSecond);
    }

    [Fact]
    public void AProcessWithoutAnIoRateYetGoesToTheBottom()
    {
        List<ProcessUsage> all =
        [
            new(1, "unknown", 0d, ByteSize.FromBytes(10), null),
            new(2, "idle", 0d, ByteSize.FromBytes(10), 0d),
            new(3, "busy", 0d, ByteSize.FromBytes(10), 5_000_000d),
        ];

        IReadOnlyList<ProcessUsage> sorted = ProcessRanking.TopByIo(all, 3);

        Assert.Equal(["busy", "idle", "unknown"], sorted.Select(process => process.Name));
    }

    [Fact]
    public void MemoryIsSortedByBytesUsed()
    {
        List<ProcessUsage> all =
        [
            new(1, "small", 0d, ByteSize.FromBytes(1_000)),
            new(2, "grosso", 0d, ByteSize.FromBytes(9_000)),
            new(3, "medio", 0d, ByteSize.FromBytes(5_000)),
        ];

        IReadOnlyList<ProcessUsage> sorted = ProcessRanking.TopByMemory(all, 2);

        Assert.Equal(["grosso", "medio"], sorted.Select(process => process.Name));
    }

    [Fact]
    public void TwoConcurrentReadsDoNotTrampleEachOther()
    {
        // TryRead empties and rewrites a dictionary, and the /processes endpoint calls it
        // directly inside the HTTP request. The window polls it once a second while the panel
        // is open: two dashboards on the same machine are enough to have two of those calls
        // running at the same time. The worst case of a Dictionary written by two threads is
        // not an exception, it is a loop inside Insert: one core at 100% for ever, the request
        // that never comes back, and no error anywhere.
        //
        // Two real Threads and not the pool: on a runner with a single processor Parallel.For
        // runs the iterations one after the other on the caller, and the test would pass green
        // even without the lock. And a rendezvous instead of a timed wait: whoever gets in
        // first waits for the other one, which with the lock in the right place will never
        // arrive. That way the green does not depend on how the race went.
        using Rendezvous rendezvous = new();
        SpyLister lister = new(rendezvous)
        {
            Processes = [.. Enumerable.Range(1, 200).Select(i => ProcessSample(i, $"p{i}", i, i * 1000))],
        };
        ProcessRanking ranking = new(lister, new SpyClock(rendezvous), Cores);

        Exception? error = null;

        void Run()
        {
            try
            {
                ranking.TryRead(out IReadOnlyList<ProcessUsage> _);
            }
#pragma warning disable CA1031 // Here the exception IS the result: it has to reach the test,
            catch (Exception ex) // not be left to kill the process running the tests.
#pragma warning restore CA1031
            {
                Interlocked.CompareExchange(ref error, ex, null);
            }
        }

        Thread one = new(Run);
        Thread two = new(Run);

        one.Start();
        two.Start();
        one.Join();
        two.Join();

        Assert.Null(error);

        // Not "it did not throw": the broken code would pass that too, four times out of ten.
        // The test measures how many reads were inside at the same time, and it must be one.
        Assert.Equal(1, rendezvous.MaxConcurrent);
    }

    /// <summary>The rendezvous where two reads meet, if the code lets them.</summary>
    private sealed class Rendezvous : IDisposable
    {
        private readonly ManualResetEventSlim bothInside = new();
        private int insideCount;
        private int maxConcurrent;

        public int MaxConcurrent => Volatile.Read(ref maxConcurrent);

        public void Arrive()
        {
            int count = Interlocked.Increment(ref insideCount);

            int seen;
            while (count > (seen = Volatile.Read(ref maxConcurrent)))
            {
                Interlocked.CompareExchange(ref maxConcurrent, count, seen);
            }

            if (count >= 2)
            {
                bothInside.Set();
            }
            else
            {
                // With the lock in the right place the other one never arrives: we leave when
                // the wait times out, and the maximum stays one.
                bothInside.Wait(TimeSpan.FromMilliseconds(250));
            }

            Interlocked.Decrement(ref insideCount);
        }

        public void Dispose() => bothInside.Dispose();
    }

    private sealed class SpyClock(Rendezvous rendezvous) : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            rendezvous.Arrive();

            return 0;
        }
    }

    private sealed class SpyLister(Rendezvous rendezvous) : IProcessLister
    {
        public IReadOnlyList<ProcessTimes> Processes { get; set; } = [];

        public bool TryList(out IReadOnlyList<ProcessTimes> processes)
        {
            rendezvous.Arrive();
            processes = Processes;

            return true;
        }
    }

    /// <summary>Ranking, fake lister and fake clock kept together, one per test.</summary>
    private sealed class Bench
    {
        public Bench(IReadOnlyList<ProcessTimes> processes)
        {
            Lister = new FakeLister { Processes = processes };
            Clock = new FakeClock();
            Ranking = new ProcessRanking(Lister, Clock, Cores);
        }

        public FakeLister Lister { get; }

        public FakeClock Clock { get; }

        public ProcessRanking Ranking { get; }

        public void Advance(IReadOnlyList<ProcessTimes> processes)
        {
            Lister.Processes = processes;
            Clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private long now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => now;

        public void Advance(TimeSpan delta) => now += delta.Ticks;
    }

    private sealed class FakeLister : IProcessLister
    {
        public IReadOnlyList<ProcessTimes> Processes { get; set; } = [];

        public bool Readable { get; set; } = true;

        public bool TryList(out IReadOnlyList<ProcessTimes> processes)
        {
            processes = Processes;

            return Readable;
        }
    }
}
