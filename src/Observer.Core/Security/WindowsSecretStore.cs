using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Observer.Core.Security;

/// <summary>
/// I token delle macchine remote nel Credential Manager di Windows.
/// </summary>
/// <remarks>
/// Scelto perche' e' l'unica opzione in cui <b>non esiste un file</b>. Contro un attaccante
/// che gira gia' come l'utente, questo deposito e DPAPI si equivalgono — entrambi
/// restituiscono il segreto a chi lo chiede con quell'identita' — ma un file, anche cifrato,
/// e' una cosa che si puo' copiare, sincronizzare, allegare o fotografare per sbaglio. Qui
/// non c'e' niente da mandare via per errore.
/// <para>
/// La persistenza e' <c>CRED_PERSIST_LOCAL_MACHINE</c> e non <c>ENTERPRISE</c>, ed e' una
/// decisione di sicurezza: la seconda fa viaggiare la credenziale insieme al profilo su ogni
/// macchina del dominio, che e' esattamente cio' che questa modifica esiste per evitare.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSecretStore : ISecretStore
{
    /// <summary>Il prefisso dei target, per non collidere con le credenziali di nessun altro.</summary>
    public const string Prefisso = "Observer:machine:";

    private const uint TipoGenerico = 1;
    private const uint PersistenzaLocale = 2;
    private const int ErroreNonTrovato = 1168;

    /// <inheritdoc />
    public string Descrizione => "the Windows Credential Manager";

    /// <inheritdoc />
    public bool TryRead(string nome, out string segreto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);

        segreto = string.Empty;

        if (!CredReadW(Prefisso + nome, TipoGenerico, 0, out IntPtr puntatore))
        {
            int errore = Marshal.GetLastWin32Error();

            if (errore == ErroreNonTrovato)
            {
                return false;
            }

            throw new SecretStoreException(
                "The Windows Credential Manager refused to return the token for " + nome + ".",
                new Win32Exception(errore));
        }

        try
        {
            Credenziale credenziale = Marshal.PtrToStructure<Credenziale>(puntatore);

            if (credenziale.CredentialBlobSize == 0 || credenziale.CredentialBlob == IntPtr.Zero)
            {
                return false;
            }

            byte[] byteDelSegreto = new byte[credenziale.CredentialBlobSize];

            try
            {
                Marshal.Copy(credenziale.CredentialBlob, byteDelSegreto, 0, byteDelSegreto.Length);
                segreto = Encoding.UTF8.GetString(byteDelSegreto);
            }
            finally
            {
                // I byte si azzerano; la stringa no, perche' in .NET e' immutabile e resta nel
                // heap gestito finche' il garbage collector non la ricicla. E' un limite noto
                // della piattaforma, non una svista: SecureString non lo risolve, e fuori da
                // Windows non cifra nemmeno.
                CryptographicOperations.ZeroMemory(byteDelSegreto);
            }

            return true;
        }
        finally
        {
            CredFree(puntatore);
        }
    }

    /// <inheritdoc />
    public void Write(string nome, string segreto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        ArgumentException.ThrowIfNullOrWhiteSpace(segreto);

        byte[] byteDelSegreto = Encoding.UTF8.GetBytes(segreto);
        byte[] zeri = new byte[byteDelSegreto.Length];

        IntPtr blob = Marshal.AllocHGlobal(byteDelSegreto.Length);
        IntPtr target = Marshal.StringToHGlobalUni(Prefisso + nome);
        IntPtr utente = Marshal.StringToHGlobalUni(Environment.UserName);

        try
        {
            Marshal.Copy(byteDelSegreto, 0, blob, byteDelSegreto.Length);

            Credenziale credenziale = new()
            {
                Type = TipoGenerico,
                TargetName = target,
                CredentialBlobSize = (uint)byteDelSegreto.Length,
                CredentialBlob = blob,
                Persist = PersistenzaLocale,
                UserName = utente,
            };

            if (!CredWriteW(ref credenziale, 0))
            {
                throw new SecretStoreException(
                    "The Windows Credential Manager refused to store the token for " + nome + ".",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
        finally
        {
            // Anche la copia non gestita si sovrascrive prima di liberarla: la memoria
            // liberata resta leggibile finche' qualcun altro non la riusa.
            Marshal.Copy(zeri, 0, blob, zeri.Length);
            CryptographicOperations.ZeroMemory(byteDelSegreto);

            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(utente);
        }
    }

    /// <inheritdoc />
    public bool Delete(string nome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);

        if (CredDeleteW(Prefisso + nome, TipoGenerico, 0))
        {
            return true;
        }

        int errore = Marshal.GetLastWin32Error();

        if (errore == ErroreNonTrovato)
        {
            return false;
        }

        throw new SecretStoreException(
            "The Windows Credential Manager refused to remove the token for " + nome + ".",
            new Win32Exception(errore));
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(
        string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(ref Credenziale credential, uint flags);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(IntPtr buffer);

    /// <summary><c>CREDENTIALW</c>, coi puntatori lasciati tali per restare blittable.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Credenziale
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
