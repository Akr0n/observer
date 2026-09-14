using Observer.Core.Platform.Windows;

namespace Observer.Core.Tests;

/// <summary>
/// <c>GetProcessIoCounters</c> on the real process that is running the test.
/// </summary>
/// <remarks>
/// Not a fake reader: what has to be verified is that the P/Invoke is declared correctly -
/// access right, 48-byte structure - and only Windows can tell you that. The counter must GROW
/// after a write: reading it only once would prove that the call does not fail, not that it
/// reads the right number.
/// </remarks>
public class WindowsProcessIoTests
{
    [WindowsOnly]
    public void OwnCounterGrowsAfterAWrite()
    {
        WindowsProcessIoReader reader = new();
        int pid = Environment.ProcessId;

        Assert.True(reader.TryRead(pid, out ulong before));

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            File.WriteAllBytes(path, new byte[1 << 20]);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.True(reader.TryRead(pid, out ulong after));
        Assert.True(after >= before + (1UL << 20), $"before {before}, after {after}");
    }

    [WindowsOnly]
    public void APidThatDoesNotExistCannotBeRead()
    {
        Assert.False(new WindowsProcessIoReader().TryRead(2147483646, out ulong ioBytes));
        Assert.Equal(0UL, ioBytes);
    }
}