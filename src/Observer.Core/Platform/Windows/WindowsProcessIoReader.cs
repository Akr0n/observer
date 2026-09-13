using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Observer.Core.Processes;

namespace Observer.Core.Platform.Windows;

/// <summary>
/// Windows adapter of the per-process I/O counter, via <c>GetProcessIoCounters</c>.
/// </summary>
/// <remarks>
/// The process is opened with <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, which is the minimum
/// right for this call and the one that other users' processes grant too; the ones protected
/// by the system refuse even this, even to LocalSystem, and for them the column stays unknown
/// instead of failing the row.
/// <para>
/// Read and write bytes are summed, not the "other" operations: those are ioctls and the like,
/// and their bytes are not transferred data. The counter counts the process's CALLS — cache
/// included, network included — because Windows has no per-process counter of the bytes that
/// reached the disk alone; that is the reason why on Linux <c>rchar</c> and <c>wchar</c> are
/// read and not <c>read_bytes</c>, so the two systems say the same thing.
/// </para>
/// </remarks>
public sealed partial class WindowsProcessIoReader : IProcessIoReader
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <inheritdoc />
    public bool TryRead(int pid, out ulong bytes)
    {
        bytes = 0;

        return OperatingSystem.IsWindows() && Read(pid, out bytes);
    }

    [SupportedOSPlatform("windows")]
    private static bool Read(int pid, out ulong bytes)
    {
        bytes = 0;

        if (pid < 0)
        {
            return false;
        }

        using SafeProcessHandle process = OpenProcess(
            ProcessQueryLimitedInformation, bInheritHandle: false, (uint)pid);

        if (process.IsInvalid || !GetProcessIoCounters(process, out IoCounters counters))
        {
            return false;
        }

        ulong sum = counters.ReadTransferCount + counters.WriteTransferCount;

        // Same guard as the Linux reader: a wrap of the 64 bits is not a total.
        if (sum < counters.ReadTransferCount)
        {
            return false;
        }

        bytes = sum;

        return true;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static partial SafeProcessHandle OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool GetProcessIoCounters(SafeProcessHandle hProcess, out IoCounters lpIoCounters);

    /// <summary><c>IO_COUNTERS</c>: six <c>ULONGLONG</c>, 48 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
}