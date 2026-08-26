using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Memory;
using Observer.Core.Units;

namespace Observer.Core.Platform.Windows;

/// <summary>
/// Adattatore Windows dei contatori CPU, via GetSystemTimes di kernel32.
/// Nessun package NuGet, nessun permesso di amministratore.
/// </summary>
/// <remarks>
/// PerformanceCounter non e' utilizzabile qui: Observer.Core ha TFM net10.0 (non
/// net10.0-windows) e con TreatWarningsAsErrors la sola presenza di quel tipo rompe la
/// build con CA1416. Costerebbe inoltre secondi nel costruttore, inaccettabili all'avvio
/// di un servizio.
/// </remarks>
public sealed partial class WindowsCpuTimesProvider : ICpuTimesProvider
{
    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public string? UnsupportedReason =>
        IsSupported ? null : "i contatori CPU di kernel32 esistono solo su Windows";

    /// <inheritdoc />
    public bool TryRead(out CpuTimes times)
    {
        times = default;

        // Il guard non e' disciplina: senza, CA1416 rompe la compilazione. E' il compilatore
        // a impedire di dimenticarlo.
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!GetSystemTimes(out long idle, out long kernel, out long user))
        {
            return false;
        }

        // ATTENZIONE: KernelTime INCLUDE GIA' IdleTime. Il bug classico e' scrivere
        // busy = kernel + user, che conta l'inattivita' come lavoro e produce percentuali
        // costantemente vicine al 100%. Con Total = kernel + user e Idle = idle, la
        // sottrazione Total - Idle fatta a valle da' il valore corretto.
        times = new CpuTimes(idle, kernel + user);
        return true;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool GetSystemTimes(
        out long lpIdleTime,
        out long lpKernelTime,
        out long lpUserTime);
}

/// <summary>Adattatore Windows dei valori di memoria, via GlobalMemoryStatusEx di kernel32.</summary>
public sealed partial class WindowsMemoryReadingProvider : IMemoryReadingProvider
{
    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public string? UnsupportedReason =>
        IsSupported ? null : "GlobalMemoryStatusEx esiste solo su Windows";

    /// <inheritdoc />
    public bool TryRead(out MemoryReading value)
    {
        value = default;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        MemoryStatusEx status = default;

        // dwLength e' un contratto di versioning della struct: senza, l'API fallisce.
        status.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();

        if (!GlobalMemoryStatusEx(ref status))
        {
            return false;
        }

        // Lo swap NON viene pubblicato su Windows, di proposito. ullTotalPageFile e' il
        // COMMIT LIMIT (RAM + pagefile), non la dimensione del file di swap: esporlo come
        // "swap" darebbe un numero plausibile e sbagliato. Il valore esatto richiede WMI,
        // che costa ~272 ms a chiamata ed e' incompatibile con un campionamento a 1 Hz.
        // Swap a zero fa omettere i punti, cioe' dichiara "non applicabile" invece di mentire.
        value = new MemoryReading(
            ByteSize.FromBytes((long)status.TotalPhys),
            ByteSize.FromBytes((long)status.AvailPhys),
            ByteSize.FromBytes(0L),
            ByteSize.FromBytes(0L),
            AvailableWasEstimated: false);

        return true;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
