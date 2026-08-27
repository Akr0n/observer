using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;

namespace Observer.Service.LocalChannel;

/// <summary>
/// Stabilisce se il chiamante di una named pipe e' davvero locale, e chi e'.
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
    private static partial bool GetNamedPipeClientComputerName(nint pipe, ref byte nome, uint lunghezzaInByte);

    /// <summary>Classifica il chiamante della pipe.</summary>
    /// <param name="pipe">Il flusso della connessione in corso.</param>
    /// <returns>L'origine del chiamante.</returns>
    public static CallerOrigin Classifica(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        // Buffer di BYTE e non di char: char non e' blittabile e il generatore di
        // [LibraryImport] pretenderebbe DisableRuntimeMarshalling sull'intero assembly. Qui il
        // contenuto non serve, serve solo sapere se la chiamata riesce: 512 byte sono 256
        // caratteri UTF-16, abbondanti per un nome di macchina.
        Span<byte> buffer = stackalloc byte[512];

        bool riuscito = GetNamedPipeClientComputerName(
            pipe.SafePipeHandle.DangerousGetHandle(),
            ref MemoryMarshal.GetReference(buffer),
            (uint)buffer.Length);

        int errore = Marshal.GetLastWin32Error();

        if (riuscito || errore != ErrorPipeLocal)
        {
            // Riuscito: la connessione e' passata da SMB, e il buffer contiene il nome del
            // chiamante. Fallito per un motivo diverso da ERROR_PIPE_LOCAL: non sappiamo dire
            // che sia locale, e nel dubbio non lo e'.
            return new CallerOrigin(
                CallerKind.ArrivatoDallaRete,
                null,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"GetNamedPipeClientComputerName ok={riuscito} win32={errore}"));
        }

        return LeggiIdentita(pipe);
    }

    private static CallerOrigin LeggiIdentita(NamedPipeServerStream pipe)
    {
        Cattura cattura = new();

        try
        {
            pipe.RunAsClient(cattura.Esegui);
        }
        catch (SecurityException ex)
        {
            // Il caso di ATTACCO: il client ha scelto TokenImpersonationLevel.Anonymous e si e'
            // reso unilateralmente non identificabile. HRESULT 0x80070543,
            // ERROR_BAD_IMPERSONATION_LEVEL. Senza questo catch il servizio risponde 500.
            return NonIdentificabile(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return NonIdentificabile(ex);
        }
        catch (IOException ex)
        {
            return NonIdentificabile(ex);
        }

        return cattura.Sid is { } sid
            ? new CallerOrigin(CallerKind.LocaleIdentificato, sid, "local caller identified")
            : new CallerOrigin(CallerKind.NonIdentificabile, null, "the caller token carried no user SID");
    }

    private static CallerOrigin NonIdentificabile(Exception ex) =>
        new(
            CallerKind.NonIdentificabile,
            null,
            string.Create(CultureInfo.InvariantCulture, $"{ex.GetType().Name} 0x{ex.HResult:X8}"));

    /// <summary>Il corpo eseguito sotto impersonation.</summary>
    /// <remarks>
    /// Un metodo di istanza di una classe annotata, e NON una lambda: [SupportedOSPlatform] non
    /// copre il corpo di una lambda e CA1416 farebbe fallire la build. Passato a RunAsClient
    /// come gruppo di metodi.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private sealed class Cattura
    {
        public string? Sid { get; private set; }

        public void Esegui()
        {
            using WindowsIdentity? chiamante = WindowsIdentity.GetCurrent(ifImpersonating: true);
            Sid = chiamante?.User?.Value;
        }
    }
}