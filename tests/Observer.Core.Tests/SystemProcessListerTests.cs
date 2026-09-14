using Observer.Core.Processes;

namespace Observer.Core.Tests;

/// <summary>
/// The seam between the real process list and the I/O reader.
/// </summary>
/// <remarks>
/// On the REAL process running the test, with a fake reader: what has to be proved is that the
/// counter that was read lands on the right row, and that a refusal from the reader leaves the
/// row in place, without I/O, instead of removing it. It is the branch no other test exercises:
/// the ranking uses a fake list, and the readers are tested on their own.
/// </remarks>
public class SystemProcessListerTests
{
    [Fact]
    public void TheCounterLandsOnTheRightProcessRow()
    {
        int ownPid = Environment.ProcessId;
        FakeIoReader reader = new(ownPid, 12_345);

        Assert.True(new SystemProcessLister(reader).TryList(out IReadOnlyList<ProcessTimes> processes));

        ProcessTimes row = Assert.Single(processes, process => process.Pid == ownPid);
        Assert.Equal(12_345UL, row.IoBytes);

        // The reader refuses to read the other processes: they stay in the list, without I/O.
        Assert.Contains(processes, process => process.Pid != ownPid && process.IoBytes is null);
    }

    [Fact]
    public void WithoutAReaderTheRowsAreStillThereWithoutIo()
    {
        Assert.True(new SystemProcessLister().TryList(out IReadOnlyList<ProcessTimes> processes));

        Assert.Contains(processes, process => process.Pid == Environment.ProcessId);
        Assert.All(processes, process => Assert.Null(process.IoBytes));
    }

    private sealed class FakeIoReader : IProcessIoReader
    {
        private readonly int knownPid;
        private readonly ulong value;

        public FakeIoReader(int knownPid, ulong value)
        {
            this.knownPid = knownPid;
            this.value = value;
        }

        public bool TryRead(int pid, out ulong bytes)
        {
            bytes = pid == knownPid ? value : 0;

            return pid == knownPid;
        }
    }
}