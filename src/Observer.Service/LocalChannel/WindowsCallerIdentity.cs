using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;

namespace Observer.Service.LocalChannel;

/// <summary>
/// Stabilisce se il caller di una named pipe e' davvero locale, e chi e'.
/// </summary>
/// <remarks>
/// La domanda "sono locale?" NON si risponde guardando il trasporto: una named pipe e'
/// raggiungibile da remoto via SMB sulla porta 445. E non si risponde nemmeno guardando il
/// token: verso la macchina stessa Windows restituisce il token interattivo ORIGINALE, con gli
/// stessi SID di gruppo della via locale, e il SID NETWORK assente in entrambi i casi.
/// <para>
/// Si risponde con GetNamedPipeClientComputerName, che fallisce con ERROR_PIPE_LOCAL quando la
/// connessione e' locale e riesce quando e' passata da SMB. Misurato su tre vie: "." locale,
/// indirizzo di rete remoto, "localhost" REMOTO. E funziona anche quando il token non e'
/// leggibile, cioe' proprio nel caso di attacco.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class WindowsCallerIdentity
{
    /// <summary>ERROR_PIPE_LOCAL: la connessione arriva dalla stessa macchina, non da SMB.</summary>
    private const int ErrorPipeLocal = 229;

    [LibraryImport("kernel32.dll", EntryPoint = "GetNamedPipeClientComputerNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientComputerName(nint pipe, ref byte name, uint sizeInBytes);

    /// <summary>Classify il caller della pipe.</summary>
    /// <param name="pipe">Il flusso della connessione in corso.</param>
    /// <returns>L'origine del caller.</returns>
    public static CallerOrigin Classify(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        // Buffer di BYTE e non di char: char non e' blittabile e il generatore di
        // [LibraryImport] pretenderebbe DisableRuntimeMarshalling sull'intero assembly. Qui il
        // contenuto non serve, serve solo sapere se la chiamata riesce: 512 byte sono 256
        // caratteri UTF-16, abbondanti per un name di macchina.
        Span<byte> buffer = stackalloc byte[512];

        bool succeeded = GetNamedPipeClientComputerName(
            pipe.SafePipeHandle.DangerousGetHandle(),
            ref MemoryMarshal.GetReference(buffer),
            (uint)buffer.Length);

        int win32Error = Marshal.GetLastWin32Error();

        if (succeeded || win32Error != ErrorPipeLocal)
        {
            // Riuscito: la connessione e' passata da SMB, e il buffer contiene il name del
            // caller. Fallito per un motivo diverso da ERROR_PIPE_LOCAL: non sappiamo dire
            // che sia locale, e nel dubbio non lo e'.
            return new CallerOrigin(
                CallerKind.FromNetwork,
                null,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"GetNamedPipeClientComputerName ok={succeeded} win32={win32Error}"));
        }

        return ReadIdentity(pipe);
    }

    private static CallerOrigin ReadIdentity(NamedPipeServerStream pipe)
    {
        SidCapture capture = new();

        try
        {
            pipe.RunAsClient(capture.Run);
        }
        catch (SecurityException ex)
        {
            // Il caso di ATTACCO: il client ha scelto TokenImpersonationLevel.Anonymous e si e'
            // reso unilateralmente non identificabile. HRESULT 0x80070543,
            // ERROR_BAD_IMPERSONATION_LEVEL. Senza questo catch il servizio risponde 500.
            return UnidentifiedOrigin(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return UnidentifiedOrigin(ex);
        }
        catch (IOException ex)
        {
            return UnidentifiedOrigin(ex);
        }

        return capture.Sid is { } sid
            ? new CallerOrigin(CallerKind.LocalIdentified, sid, "local caller identified")
            : new CallerOrigin(CallerKind.Unidentified, null, "the caller token carried no user SID");
    }

    private static CallerOrigin UnidentifiedOrigin(Exception ex) =>
        new(
            CallerKind.Unidentified,
            null,
            string.Create(CultureInfo.InvariantCulture, $"{ex.GetType().Name} 0x{ex.HResult:X8}"));

    /// <summary>Il corpo eseguito sotto impersonation.</summary>
    /// <remarks>
    /// Un metodo di istanza di una classe annotata, e NON una lambda: [SupportedOSPlatform] non
    /// copre il corpo di una lambda e CA1416 farebbe fallire la build. Passato a RunAsClient
    /// come gruppo di metodi.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private sealed class SidCapture
    {
        public string? Sid { get; private set; }

        public void Run()
        {
            using WindowsIdentity? caller = WindowsIdentity.GetCurrent(ifImpersonating: true);
            Sid = caller?.User?.Value;
        }
    }
}