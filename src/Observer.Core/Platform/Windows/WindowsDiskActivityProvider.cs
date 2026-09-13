using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Observer.Core.Metrics.Disk;

namespace Observer.Core.Platform.Windows;

/// <summary>
/// Windows adapter for the disk activity counters, through
/// <c>IOCTL_DISK_PERFORMANCE</c> on <c>\\.\PhysicalDriveN</c>.
/// </summary>
/// <remarks>
/// Chosen over the performance counters for two measured reasons. The first: no extra package
/// is needed — <c>System.Diagnostics.PerformanceCounter</c> is a separate NuGet on .NET, and a
/// dependency has to be argued for, not added in passing. The second: the category names of
/// those counters are TRANSLATED, and on an Italian machine looking for "PhysicalDisk" finds
/// nothing — a fault that stays invisible until it is tried on a localized machine.
/// <para>
/// The device is opened with <b>zero</b> access, not read access: that is all this IOCTL needs,
/// and with zero it works without administrator privileges. Verified on this machine, on an
/// unelevated process: <c>PhysicalDrive0</c> and <c>PhysicalDrive1</c> answer, the third gives
/// ERROR_FILE_NOT_FOUND because it does not exist.
/// </para>
/// </remarks>
public sealed partial class WindowsDiskActivityProvider : IDiskActivityProvider
{
    // Windows numbers physical disks from zero, with possible holes: a number that is not found
    // does not end the search. The limit is declared instead of silent — a machine with more
    // than 32 physical disks would show only the first 32, and this line is the only place
    // where that shows.
    private const int DisksExamined = 32;

    private const uint IoctlDiskPerformance = 0x00070020;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    /// <inheritdoc />
    /// <remarks>
    /// Always true, even outside Windows: the platform is a parameter of the composition, not a
    /// reading of the environment, and the tests must be able to build this provider from the
    /// Linux runner. Whoever is not on Windows never gets to build it, because
    /// <c>ObserverMetrics.CreateCollectors</c> picks another branch.
    /// </remarks>
    public bool IsSupported => true;

    /// <inheritdoc />
    public string? UnsupportedReason => null;

    /// <inheritdoc />
    public bool TryRead(out IReadOnlyList<DiskActivityReading> readings)
    {
        if (!OperatingSystem.IsWindows())
        {
            readings = [];

            return false;
        }

        List<DiskActivityReading> found = [];

        for (int number = 0; number < DisksExamined; number++)
        {
            if (TryReadDisk(number, out DiskActivityReading reading))
            {
                found.Add(reading);
            }
        }

        readings = found;

        // Zero disks is not a READ failure: it is an answer, and the collector can tell it
        // apart from "I could not read".
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReadDisk(int number, out DiskActivityReading reading)
    {
        reading = default;

        string numberText = number.ToString(CultureInfo.InvariantCulture);

        using SafeFileHandle device = CreateFileW(
            $@"\\.\PhysicalDrive{numberText}",
            dwDesiredAccess: 0,
            FileShareReadWrite,
            IntPtr.Zero,
            OpenExisting,
            dwFlagsAndAttributes: 0,
            IntPtr.Zero);

        if (device.IsInvalid)
        {
            return false;
        }

        DiskPerformance performance = default;

        if (!DeviceIoControl(
                device,
                IoctlDiskPerformance,
                IntPtr.Zero,
                0,
                ref performance,
                (uint)Marshal.SizeOf<DiskPerformance>(),
                out _,
                IntPtr.Zero))
        {
            return false;
        }

        // The counters come back as signed integers but are never negative; if some version of
        // Windows returned a negative one, skipping it is better than publishing a huge number
        // after the conversion to unsigned.
        if (performance.BytesRead < 0 || performance.BytesWritten < 0 || performance.IdleTime < 0)
        {
            return false;
        }

        reading = DiskActivityReading.WithIdleTime(
            $"Disk {numberText}",
            (ulong)performance.BytesRead,
            (ulong)performance.BytesWritten,
            TimeSpan.FromTicks(performance.IdleTime));

        return true;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [SupportedOSPlatform("windows")]
    private static partial SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        ref DiskPerformance lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>
    /// <c>DISK_PERFORMANCE</c>, 88 bytes.
    /// </summary>
    /// <remarks>
    /// The last 16 bytes are the 8 <c>WCHAR</c>s of <c>StorageManagerName</c>: they are of no
    /// use here, but their SIZE is. With a shorter struct — the one you get by writing that
    /// field as an ANSI string — the IOCTL answers <c>ERROR_INSUFFICIENT_BUFFER</c> (122) and
    /// reads nothing. Measured: the first attempt failed exactly like that, on every disk, and
    /// the correct struct measures 88.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct DiskPerformance
    {
        public long BytesRead;
        public long BytesWritten;
        public long ReadTime;
        public long WriteTime;
        public long IdleTime;
        public uint ReadCount;
        public uint WriteCount;
        public uint QueueDepth;
        public uint SplitCount;
        public long QueryTime;
        public uint StorageDeviceNumber;
        public uint NamePart0;
        public uint NamePart1;
        public uint NamePart2;
        public uint NamePart3;
    }
}