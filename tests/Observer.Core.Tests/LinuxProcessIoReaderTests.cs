using Observer.Core.Platform;
using Observer.Core.Platform.Linux;

namespace Observer.Core.Tests;

/// <summary>
/// Reading <c>/proc/PID/io</c>, against a fake reader, so these tests run on both runners.
/// </summary>
/// <remarks>
/// The rule that matters is WHICH lines are summed. <c>read_bytes</c> and <c>write_bytes</c> are
/// right there in the same file, they look like the better fit for a panel opened from a disk gauge, and
/// they are the wrong choice: Windows has no equivalent, and the two systems must say the same
/// thing.
/// </remarks>
public class LinuxProcessIoReaderTests
{
    // A real file, with the seven fields in the order the kernel writes them.
    private const string ProcIo =
        """
        rchar: 3000
        wchar: 500
        syscr: 40
        syscw: 10
        read_bytes: 8192
        write_bytes: 4096
        cancelled_write_bytes: 0
        """;

    [Fact]
    public void SumsRcharAndWcharNotTheBytesOnDisk()
    {
        FakeFileTextReader reader = new();
        reader.Set("/proc/42/io", ProcIo);

        Assert.True(new LinuxProcessIoReader(reader).TryRead(42, out ulong ioBytes));
        Assert.Equal(3500UL, ioBytes);
    }

    [Fact]
    public void AProcessThatCannotBeReadHasNoCounter()
    {
        // On Linux this is the normal case: another user's process, without CAP_SYS_PTRACE.
        // The reader sees a file it cannot open, and the answer is "I don't know", not zero.
        FakeFileTextReader reader = new();

        Assert.False(new LinuxProcessIoReader(reader).TryRead(42, out ulong ioBytes));
        Assert.Equal(0UL, ioBytes);
    }

    [Fact]
    public void ThePathUsesTheRequestedPid()
    {
        FakeFileTextReader reader = new();
        reader.Set("/proc/7/io", ProcIo);

        Assert.False(new LinuxProcessIoReader(reader).TryRead(42, out _));
        Assert.True(new LinuxProcessIoReader(reader).TryRead(7, out _));
    }

    [Theory]
    [InlineData("wchar: 500\nsyscr: 1")]
    [InlineData("rchar: 3000\nsyscr: 1")]
    [InlineData("rchar: lots\nwchar: 500")]
    [InlineData("rchar: -1\nwchar: 500")]
    [InlineData("")]
    public void WithoutBothIntegerFieldsThereIsNoCounter(string content)
    {
        Assert.False(LinuxProcessIoReader.TryParse(content, out ulong ioBytes));
        Assert.Equal(0UL, ioBytes);
    }

    [Fact]
    public void ASumThatWrapsAround64BitsIsNotATotal()
    {
        string content = "rchar: 18446744073709551615\nwchar: 1";

        Assert.False(LinuxProcessIoReader.TryParse(content, out _));
    }

    private sealed class FakeFileTextReader : IFileTextReader
    {
        private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);

        public void Set(string path, string content) => files[path] = content;

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