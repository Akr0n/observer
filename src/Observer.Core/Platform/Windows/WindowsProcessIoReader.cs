using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Observer.Core.Processes;

namespace Observer.Core.Platform.Windows;

/// <summary>
/// Adattatore Windows del contatore di I/O per processo, via <c>GetProcessIoCounters</c>.
/// </summary>
/// <remarks>
/// Il processo si apre con <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, che e' il diritto minimo
/// per questa chiamata e quello che anche i processi degli altri utenti concedono; quelli
/// protetti dal sistema rifiutano perfino questo, anche a LocalSystem, e per loro la colonna
/// resta sconosciuta invece di far saltare la riga.
/// <para>
/// Si sommano i byte delle letture e delle scritture, non le operazioni "altre": quelle sono
/// ioctl e simili, e i loro byte non sono dati trasferiti. Il contatore conta le CHIAMATE del
/// processo — cache compresa, rete compresa — perche' Windows non ha un contatore per processo
/// dei soli byte arrivati al disco; e' la ragione per cui su Linux si leggono <c>rchar</c> e
/// <c>wchar</c> e non <c>read_bytes</c>, cosi' i due sistemi dicono la stessa cosa.
/// </para>
/// </remarks>
public sealed partial class WindowsProcessIoReader : IProcessIoReader
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <inheritdoc />
    public bool TryRead(int pid, out ulong bytes)
    {
        bytes = 0;

        return OperatingSystem.IsWindows() && Leggi(pid, out bytes);
    }

    [SupportedOSPlatform("windows")]
    private static bool Leggi(int pid, out ulong bytes)
    {
        bytes = 0;

        if (pid < 0)
        {
            return false;
        }

        using SafeProcessHandle processo = OpenProcess(
            ProcessQueryLimitedInformation, bInheritHandle: false, (uint)pid);

        if (processo.IsInvalid || !GetProcessIoCounters(processo, out IoCounters contatori))
        {
            return false;
        }

        bytes = contatori.ReadTransferCount + contatori.WriteTransferCount;

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

    /// <summary><c>IO_COUNTERS</c>: sei <c>ULONGLONG</c>, 48 byte.</summary>
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