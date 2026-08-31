using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Observer.Core.Metrics.Disk;

namespace Observer.Core.Platform.Windows;

/// <summary>
/// Adattatore Windows dei contatori di attivita' dei dischi, via
/// <c>IOCTL_DISK_PERFORMANCE</c> su <c>\\.\PhysicalDriveN</c>.
/// </summary>
/// <remarks>
/// Scelto invece dei contatori di prestazione per due motivi misurati. Il primo: non serve
/// alcun pacchetto in piu' — <c>System.Diagnostics.PerformanceCounter</c> e' un NuGet a parte
/// su .NET, e una dipendenza va motivata, non aggiunta di sfuggita. Il secondo: i nomi delle
/// categorie di quei contatori sono TRADOTTI, e su una macchina italiana cercare
/// "PhysicalDisk" non trova niente — un guasto che non si vede finche' non si prova su una
/// macchina localizzata.
/// <para>
/// Il dispositivo si apre con accesso <b>zero</b>, non in lettura: e' quanto basta a questo
/// IOCTL, e con zero funziona senza privilegi di amministratore. Verificato su questa
/// macchina, processo non elevato: <c>PhysicalDrive0</c> e <c>PhysicalDrive1</c> rispondono,
/// il terzo da' ERROR_FILE_NOT_FOUND perche' non esiste.
/// </para>
/// </remarks>
public sealed partial class WindowsDiskActivityProvider : IDiskActivityProvider
{
    // Windows numera i dischi fisici a partire da zero, con buchi possibili: un numero non
    // trovato non chiude la ricerca. Il limite e' dichiarato invece che silenzioso — una
    // macchina con piu' di 32 dischi fisici mostrerebbe solo i primi 32, e questa riga e'
    // l'unico posto in cui si vede.
    private const int DischiEsaminati = 32;

    private const uint IoctlDiskPerformance = 0x00070020;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    /// <inheritdoc />
    /// <remarks>
    /// Vero sempre, anche fuori da Windows: la piattaforma e' un parametro della
    /// composizione, non una lettura dell'ambiente, e i test devono poter costruire questo
    /// provider dal runner Linux. Chi non e' su Windows non arriva mai a costruirlo, perche'
    /// <c>ObserverMetrics.CreateCollectors</c> sceglie un altro ramo.
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

        List<DiskActivityReading> trovati = [];

        for (int numero = 0; numero < DischiEsaminati; numero++)
        {
            if (TryLeggiDisco(numero, out DiskActivityReading lettura))
            {
                trovati.Add(lettura);
            }
        }

        readings = trovati;

        // Zero dischi non e' un fallimento della LETTURA: e' una risposta, e il collector la
        // sa distinguere da "non sono riuscito a leggere".
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryLeggiDisco(int numero, out DiskActivityReading lettura)
    {
        lettura = default;

        string quale = numero.ToString(CultureInfo.InvariantCulture);

        using SafeFileHandle dispositivo = CreateFileW(
            $@"\\.\PhysicalDrive{quale}",
            dwDesiredAccess: 0,
            FileShareReadWrite,
            IntPtr.Zero,
            OpenExisting,
            dwFlagsAndAttributes: 0,
            IntPtr.Zero);

        if (dispositivo.IsInvalid)
        {
            return false;
        }

        DiskPerformance prestazioni = default;

        if (!DeviceIoControl(
                dispositivo,
                IoctlDiskPerformance,
                IntPtr.Zero,
                0,
                ref prestazioni,
                (uint)Marshal.SizeOf<DiskPerformance>(),
                out _,
                IntPtr.Zero))
        {
            return false;
        }

        // I contatori tornano come interi con segno ma non sono mai negativi; se una versione
        // di Windows ne restituisse uno negativo, saltarlo e' meglio che pubblicare un numero
        // enorme dopo la conversione a senza segno.
        if (prestazioni.BytesRead < 0 || prestazioni.BytesWritten < 0 || prestazioni.IdleTime < 0)
        {
            return false;
        }

        lettura = DiskActivityReading.ConTempoInattivo(
            $"Disk {quale}",
            (ulong)prestazioni.BytesRead,
            (ulong)prestazioni.BytesWritten,
            TimeSpan.FromTicks(prestazioni.IdleTime));

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
    /// <c>DISK_PERFORMANCE</c>, 88 byte.
    /// </summary>
    /// <remarks>
    /// Gli ultimi 16 byte sono gli 8 <c>WCHAR</c> di <c>StorageManagerName</c>: non servono a
    /// niente qui, ma la loro DIMENSIONE si'. Con una struct piu' corta — quella che viene
    /// scrivendo il campo come stringa ANSI — l'IOCTL risponde <c>ERROR_INSUFFICIENT_BUFFER</c>
    /// (122) e non legge nulla. Misurato: il primo tentativo falliva esattamente cosi', su
    /// tutti i dischi, e la struct corretta ne misura 88.
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
        public uint NomeParte0;
        public uint NomeParte1;
        public uint NomeParte2;
        public uint NomeParte3;
    }
}