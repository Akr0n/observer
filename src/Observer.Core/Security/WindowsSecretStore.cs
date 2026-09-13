using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Observer.Core.Security;

/// <summary>
/// The remote machines' tokens in the Windows Credential Manager.
/// </summary>
/// <remarks>
/// Chosen because it is the only option in which <b>no file exists</b>. Against an attacker
/// already running as the user, this store and DPAPI are equivalent — both hand the secret to
/// whoever asks for it with that identity — but a file, even an encrypted one, is something
/// that can be copied, synchronized, attached or photographed by mistake. Here there is
/// nothing to send away by accident.
/// <para>
/// Persistence is <c>CRED_PERSIST_LOCAL_MACHINE</c> and not <c>ENTERPRISE</c>, and it is a
/// security decision: the latter makes the credential travel along with the profile to every
/// machine in the domain, which is exactly what this change exists to avoid.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSecretStore : ISecretStore
{
    /// <summary>The prefix of the targets, so as not to collide with anyone else's credentials.</summary>
    public const string Prefix = "Observer:machine:";

    private const uint GenericType = 1;
    private const uint LocalPersistence = 2;
    private const int NotFoundError = 1168;

    /// <inheritdoc />
    public string Description => "the Windows Credential Manager";

    /// <inheritdoc />
    public bool TryRead(string name, out string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        secret = string.Empty;

        if (!CredReadW(Prefix + name, GenericType, 0, out IntPtr pointer))
        {
            int error = Marshal.GetLastWin32Error();

            if (error == NotFoundError)
            {
                return false;
            }

            throw new SecretStoreException(
                "The Windows Credential Manager refused to return the token for " + name + ".",
                new Win32Exception(error));
        }

        try
        {
            CredentialW credential = Marshal.PtrToStructure<CredentialW>(pointer);

            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return false;
            }

            byte[] secretBytes = new byte[credential.CredentialBlobSize];

            try
            {
                Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
                secret = Encoding.UTF8.GetString(secretBytes);
            }
            finally
            {
                // The bytes are zeroed; the string is not, because in .NET it is immutable and
                // stays in the managed heap until the garbage collector recycles it. It is a
                // known limit of the platform, not an oversight: SecureString does not solve it,
                // and outside Windows it does not even encrypt.
                CryptographicOperations.ZeroMemory(secretBytes);
            }

            return true;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    /// <inheritdoc />
    public void Write(string name, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        byte[] zeros = new byte[secretBytes.Length];

        IntPtr blob = Marshal.AllocHGlobal(secretBytes.Length);
        IntPtr target = Marshal.StringToHGlobalUni(Prefix + name);
        IntPtr user = Marshal.StringToHGlobalUni(Environment.UserName);

        try
        {
            Marshal.Copy(secretBytes, 0, blob, secretBytes.Length);

            CredentialW credential = new()
            {
                Type = GenericType,
                TargetName = target,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = blob,
                Persist = LocalPersistence,
                UserName = user,
            };

            if (!CredWriteW(ref credential, 0))
            {
                throw new SecretStoreException(
                    "The Windows Credential Manager refused to store the token for " + name + ".",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
        finally
        {
            // The unmanaged copy is overwritten too before it is freed: freed memory stays
            // readable until somebody else reuses it.
            Marshal.Copy(zeros, 0, blob, zeros.Length);
            CryptographicOperations.ZeroMemory(secretBytes);

            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(user);
        }
    }

    /// <inheritdoc />
    public bool Delete(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (CredDeleteW(Prefix + name, GenericType, 0))
        {
            return true;
        }

        int error = Marshal.GetLastWin32Error();

        if (error == NotFoundError)
        {
            return false;
        }

        throw new SecretStoreException(
            "The Windows Credential Manager refused to remove the token for " + name + ".",
            new Win32Exception(error));
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(
        string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(ref CredentialW credential, uint flags);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(IntPtr buffer);

    /// <summary><c>CREDENTIALW</c>, with the pointers left as such so it stays blittable.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CredentialW
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