using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Disk;
using Observer.Core.Metrics.Memory;
using Observer.Core.Units;

namespace Observer.Core.Platform.Windows;

/// <summary>
/// Windows adapter for the CPU counters, through kernel32's GetSystemTimes.
/// No NuGet package, no administrator permission.
/// </summary>
/// <remarks>
/// PerformanceCounter is not usable here: Observer.Core has TFM net10.0 (not
/// net10.0-windows) and with TreatWarningsAsErrors the mere presence of that type breaks the
/// build with CA1416. It would also cost seconds in the constructor, unacceptable at the
/// start-up of a service.
/// </remarks>
public sealed partial class WindowsCpuTimesProvider : ICpuTimesProvider
{
    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public string? UnsupportedReason =>
        IsSupported ? null : "the kernel32 CPU counters exist only on Windows";

    /// <inheritdoc />
    public bool TryRead(out CpuTimes times)
    {
        times = default;

        // The guard is not discipline: without it, CA1416 breaks the compilation. It is the
        // compiler that stops you from forgetting it.
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!GetSystemTimes(out long idle, out long kernel, out long user))
        {
            return false;
        }

        // WARNING: KernelTime ALREADY INCLUDES IdleTime. The classic bug is to write
        // busy = kernel + user, which counts idleness as work and produces percentages
        // constantly close to 100%. With Total = kernel + user and Idle = idle, the
        // Total - Idle subtraction done downstream gives the correct value.
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

/// <summary>Windows adapter for the memory values, through kernel32's GlobalMemoryStatusEx.</summary>
public sealed partial class WindowsMemoryReadingProvider : IMemoryReadingProvider
{
    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public string? UnsupportedReason =>
        IsSupported ? null : "GlobalMemoryStatusEx exists only on Windows";

    /// <inheritdoc />
    public bool TryRead(out MemoryReading value)
    {
        value = default;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        MemoryStatusEx status = default;

        // dwLength is a versioning contract for the struct: without it, the API fails.
        status.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();

        if (!GlobalMemoryStatusEx(ref status))
        {
            return false;
        }

        // Swap is NOT published on Windows, deliberately. ullTotalPageFile is the
        // COMMIT LIMIT (RAM + pagefile), not the size of the swap file: exposing it as
        // "swap" would give a plausible and wrong number. The exact value needs WMI,
        // which costs ~272 ms per call and is incompatible with sampling at 1 Hz.
        // Swap at zero makes the points be omitted, that is, it says "not applicable" instead of lying.
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


/// <summary>Windows adapter for the space on the volumes, through DriveInfo.</summary>
/// <remarks>
/// <c>DriveInfo</c> and not WMI, and not the performance counters: both answer, but it was
/// measured that they cost too much for sampling once a second — WMI from 306 to 2041 ms, and the
/// first registration of a performance counter 2377 ms. <c>DriveInfo</c> costs 0.41 ms for
/// three volumes, and for space it says everything that is needed.
/// </remarks>
public sealed class WindowsDiskReadingProvider : IDiskReadingProvider
{
    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public string? UnsupportedReason =>
        IsSupported ? null : "drive letters and their volumes are a Windows notion";

    /// <inheritdoc />
    public bool TryRead(out IReadOnlyList<DiskReading> readings)
    {
        readings = [];

        if (!IsSupported)
        {
            return false;
        }

        List<DiskReading> found = [];

        DriveInfo[] drives;

        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        foreach (DriveInfo volume in drives)
        {
            // One volume at a time inside the try: an empty optical drive, a network drive that
            // has gone down or a USB stick pulled out while it is being read all throw, and they
            // must take themselves out of the way, not the whole list.
            try
            {
                if (!volume.IsReady || volume.DriveType == DriveType.Ram)
                {
                    continue;
                }

                found.Add(new DiskReading(
                    volume.Name.TrimEnd(Path.DirectorySeparatorChar),
                    ByteSize.FromBytes(volume.TotalSize),
                    ByteSize.FromBytes(volume.AvailableFreeSpace)));
            }
            catch (IOException)
            {
                // The volume vanished between IsReady and the read: it really happens.
            }
            catch (UnauthorizedAccessException)
            {
                // The service runs as LocalSystem and this volume is none of its business.
            }
        }

        readings = found;

        return true;
    }
}