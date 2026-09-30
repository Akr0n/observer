using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Observer.Service.Credentials;

/// <summary>Makes a file belong to whoever owns the directory it sits in.</summary>
/// <remarks>
/// A new file belongs to whoever creates it, and the credential store is not always written by
/// the account that reads it. The .deb runs the service as an account of its own and creates
/// /etc/observer for it, but <c>sudo observer rotate-key</c> writes the store as ROOT. Without
/// this, the rotation left a credentials.json that only root could read inside a directory owned
/// by the service, and the service died at its next start on an exception nobody handles, in a
/// restart loop (measured under systemd). The store belongs to its directory's owner, whoever is
/// the one writing it.
/// <para>
/// The FILE is handled by descriptor and never by path (only the directory, which is just read, is
/// looked up by path), and that is not a detail. The directory belongs to the service account,
/// which faces the network, and root works inside it: a chown by path follows a symbolic link, so a
/// compromised service could put one where the file was created and have root hand over whatever it
/// pointed at. Every other thing root does there (create exclusively, rename, unlink) is safe
/// against that, and this must not be the exception.
/// </para>
/// <para>
/// It looks first and changes only what differs, so where the owner already matches, which is
/// whenever the service writes its own store, the only calls are two read-only statx and never a
/// chown. Only ROOT writes on behalf of someone else, and only for root is a file that cannot be
/// handed over an error: the caller gets an <see cref="IOException"/> and the write does not happen,
/// because a store the service cannot read is worse than a rotation that did not take place, since
/// the old store is still there and still works. Any other account writes for itself, is left as
/// it is when it cannot hand the file over, and never refused for it. A difference in the GROUP
/// alone is corrected if possible and ignored if not, because the file is 0600 and its group has no
/// access. When the owner cannot even be READ (a system without statx, or one that filters it) there
/// is nothing to hand over, and refusing would stop a service from starting where it starts today:
/// that is unknown, not a mismatch.
/// </para>
/// <para>
/// The rule takes for granted that whoever owns the directory is entitled to the secret. That is
/// true of /etc/observer, where it is the service account. A directory chosen with
/// <c>Observer:CredentialStorePath</c> is the operator's responsibility, as
/// <see cref="CredentialDirectory.Prepare"/> already says: pointed at one owned by someone else, a
/// service that runs as root now gives that account its own token and certificate key, which it
/// could already replace.
/// </para>
/// <para>
/// statx(2) and not stat(2), because the layout of <c>struct stat</c> depends on the architecture
/// and that of <c>struct statx</c> does not, which is what lets two fields be read without
/// declaring the structure. glibc has exported it since 2.28, which is years older than the Ubuntu
/// this package is built and tested on.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public static partial class UnixOwnership
{
    /// <summary>AT_FDCWD: paths are resolved from the working directory. Ours are absolute.</summary>
    private const int AtFdcwd = -100;

    /// <summary>AT_EMPTY_PATH: with an empty path, ask about the descriptor itself.</summary>
    private const int AtEmptyPath = 0x1000;

    /// <summary>STATX_UID | STATX_GID: the two fields asked for.</summary>
    private const uint UidAndGid = 0x8u | 0x10u;

    /// <summary>sizeof(struct statx), fixed by the kernel interface.</summary>
    private const int StatxSize = 256;

    /// <summary>Where <c>stx_mask</c>, <c>stx_uid</c> and <c>stx_gid</c> sit inside it.</summary>
    private const int MaskOffset = 0;

    private const int UidOffset = 20;

    private const int GidOffset = 24;

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatX(int directoryDescriptor, string path, int flags, uint mask, ref byte buffer);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatX(SafeFileHandle file, string path, int flags, uint mask, ref byte buffer);

    [LibraryImport("libc", EntryPoint = "fchown", SetLastError = true)]
    private static partial int FChOwn(SafeFileHandle file, uint owner, uint group);

    /// <summary>Hands the open file to the owner of its directory, if it is not theirs already.</summary>
    /// <param name="file">The file just created, still open.</param>
    /// <exception cref="IOException">If root has to change the account the file belongs to and cannot.</exception>
    public static void FollowDirectory(FileStream file)
    {
        string directory = Path.GetDirectoryName(file.Name)
            ?? throw new ArgumentException("The file has no directory.", nameof(file));

        // Unknown is not a mismatch: without the answers there is nothing to hand over.
        if (OwnerOfDirectory(directory) is not { } wanted
            || OwnerOfFile(file.SafeFileHandle) is not { } current
            || current == wanted)
        {
            return;
        }

        if (FChOwn(file.SafeFileHandle, wanted.Uid, wanted.Gid) == 0)
        {
            return;
        }

        int error = Marshal.GetLastPInvokeError();

        // Same account, other group: a 0600 file is closed to its group, so this is a label. And
        // an account that is not root writes for ITSELF: it cannot give files away and has nobody
        // to give them to, so refusing it would only stop a write that works today.
        if (current.Uid == wanted.Uid || !Environment.IsPrivilegedProcess)
        {
            return;
        }

        throw new IOException(
            "Could not hand '" + file.Name + "' to the owner of its directory (uid "
            + wanted.Uid + ", gid " + wanted.Gid + "): " + Marshal.GetPInvokeErrorMessage(error) + ".");
    }

    private static (uint Uid, uint Gid)? OwnerOfDirectory(string directory)
    {
        Span<byte> buffer = stackalloc byte[StatxSize];

        try
        {
            return StatX(AtFdcwd, directory, 0, UidAndGid, ref MemoryMarshal.GetReference(buffer)) == 0
                ? Owner(buffer)
                : null;
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    private static (uint Uid, uint Gid)? OwnerOfFile(SafeFileHandle file)
    {
        Span<byte> buffer = stackalloc byte[StatxSize];

        try
        {
            return StatX(file, string.Empty, AtEmptyPath, UidAndGid, ref MemoryMarshal.GetReference(buffer)) == 0
                ? Owner(buffer)
                : null;
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    private static (uint Uid, uint Gid)? Owner(ReadOnlySpan<byte> statx)
    {
        // The kernel says WHICH fields it filled in, and one it did not must not be read as uid 0.
        if ((BitConverter.ToUInt32(statx[MaskOffset..]) & UidAndGid) != UidAndGid)
        {
            return null;
        }

        return (BitConverter.ToUInt32(statx[UidOffset..]), BitConverter.ToUInt32(statx[GidOffset..]));
    }
}