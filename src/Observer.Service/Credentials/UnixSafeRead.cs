using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Observer.Service.Credentials;

/// <summary>Reads a file that somebody else may have tampered with, without being tricked.</summary>
/// <remarks>
/// The service account owns /etc/observer and can put anything in it, and root reads the store and
/// the certificate from there. The ordinary read follows a symbolic link and opens a FIFO, and
/// that is what an account that has taken the service over would use. Measured on 0.24.4, run as
/// root: a FIFO made the command wait for ever, a link to /dev/zero grew its memory until the
/// process was killed, a socket ended in an unhandled exception, and a link to a file shaped like
/// the store had its secret printed.
/// <para>
/// So the file is opened ONCE, with O_NOFOLLOW and O_NONBLOCK, and everything that follows is
/// asked of the open descriptor and never of the path: what it is, how many names it has, whose it
/// is, how big it is. Only then is it read, up to <see cref="MaxBytes"/>. Nothing about the path is
/// looked at after the open, so no swap can change WHICH file is read. O_NOFOLLOW applies to the
/// last component of the path only: a link higher up is followed, and is not the account's to put
/// there, since /etc is root's.
/// </para>
/// <para>
/// A second name is refused only when the inode belongs to an account OTHER than the folder's
/// owner, because that is the attack: a name for a file of root's. The account's own second names,
/// and a backup of its files made with "cp -al", belong to the folder's owner and harm nobody; they
/// must stay readable, or one "ln" by that account would block "share" and "rotate-key --now". The
/// link count is a snapshot: with fs.protected_hardlinks=0 (not the default on Debian and Ubuntu)
/// the account can still link a root-only file and unlink its own name between the open and the
/// question, and this does not claim otherwise.
/// </para>
/// <para>
/// O_NONBLOCK is what makes a FIFO return at once instead of waiting for a writer, and it is why
/// the open needs no timeout. What it cannot cover is a file system the account serves itself
/// (FUSE with user_allow_other, which is off by default): there a read of a regular-looking file
/// can stall, and so can the answer to which uid owns the folder. That is not covered.
/// </para>
/// <para>
/// O_NOFOLLOW is not one number across architectures. A wrong value is not a crash: it is some
/// other flag, and the link is followed in silence. So the value is taken from a table, and before
/// the first read it is PROVED, with the very flags the read opens with, by opening a link the
/// kernel always has (/proc/self/cwd) and requiring the open to be refused. Only x64 was measured;
/// the other entries come from the kernel headers and are guarded only by that proof, which makes a
/// wrong one refuse to read instead of following.
/// </para>
/// <para>
/// statx(2), as in <see cref="UnixOwnership"/>, because the layout of <c>struct statx</c> does not
/// depend on the architecture. open(2) and fcntl(2) are variadic in C and are called through
/// declarations of fixed arity, which is correct for the way they are used here; that was measured
/// on x64 only.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public static partial class UnixSafeRead
{
    /// <summary>The most it will read, in bytes: the store is about a hundred, the certificate a few thousand.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The bits of the mode that say what kind of file it is.</summary>
    public const uint FileTypeMask = 0xF000;

    /// <summary>S_IFREG: a regular file, the only kind root reads.</summary>
    public const uint RegularFileType = 0x8000;

    /// <summary>S_IFDIR.</summary>
    public const uint DirectoryType = 0x4000;

    /// <summary>S_IFCHR.</summary>
    public const uint CharacterDeviceType = 0x2000;

    /// <summary>S_IFBLK.</summary>
    public const uint BlockDeviceType = 0x6000;

    /// <summary>S_IFIFO.</summary>
    public const uint FifoType = 0x1000;

    /// <summary>S_IFSOCK.</summary>
    public const uint SocketType = 0xC000;

    /// <summary>S_IFLNK.</summary>
    public const uint SymbolicLinkType = 0xA000;

    /// <summary>O_RDONLY.</summary>
    private const int ReadOnly = 0;

    /// <summary>O_NONBLOCK (04000): the same on every architecture .NET runs on Linux.</summary>
    private const int NonBlocking = 0x800;

    /// <summary>O_CLOEXEC (02000000): the same on every architecture .NET runs on Linux.</summary>
    private const int CloseOnExec = 0x80000;

    /// <summary>AT_EMPTY_PATH: with an empty path, ask about the descriptor itself.</summary>
    private const int AtEmptyPath = 0x1000;

    /// <summary>AT_SYMLINK_NOFOLLOW: with a path, ask about a link itself and not about what it points to.</summary>
    private const int AtSymlinkNoFollow = 0x100;

    /// <summary>AT_FDCWD: paths are resolved from the working directory. Ours are absolute.</summary>
    private const int AtFdcwd = -100;

    /// <summary>STATX_TYPE | STATX_MODE | STATX_NLINK | STATX_UID | STATX_SIZE: the fields asked for.</summary>
    private const uint Wanted = 0x1u | 0x2u | 0x4u | 0x8u | 0x200u;

    /// <summary>sizeof(struct statx), fixed by the kernel interface.</summary>
    private const int StatxSize = 256;

    /// <summary>Where <c>stx_mask</c>, <c>stx_nlink</c>, <c>stx_uid</c>, <c>stx_mode</c> and <c>stx_size</c> sit inside it.</summary>
    private const int MaskOffset = 0;

    private const int NlinkOffset = 16;

    private const int UidOffset = 20;

    private const int ModeOffset = 28;

    private const int SizeOffset = 40;

    /// <summary>ELOOP: what open() says to a symbolic link when it was told not to follow one.</summary>
    private const int TooManyLinks = 40;

    /// <summary>fcntl F_GETFD, and the bit FD_CLOEXEC it reports.</summary>
    private const int GetDescriptorFlags = 1;

    private const int CloseOnExecBit = 1;

    /// <summary>The value of O_NOFOLLOW that has been proved on this machine, or 0 if none has.</summary>
    private static volatile int provenFlag;

    /// <summary>The value of O_NOFOLLOW on this architecture, or 0 if none has been verified.</summary>
    /// <remarks>
    /// 0x20000 (0400000) is the generic value, used by x86, x86-64, s390x, riscv64 and loongarch64.
    /// 0x8000 (0100000) is what arm, arm64 and powerpc use instead. There 0x20000 is some other flag
    /// that refuses nothing: O_LARGEFILE on arm and arm64, O_DIRECT on powerpc.
    /// </remarks>
    public static int NoFollowFlag { get; } = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 or Architecture.X86 or Architecture.S390x or Architecture.LoongArch64
            or Architecture.RiscV64 => 0x20000,
        Architecture.Arm or Architecture.Armv6 or Architecture.Arm64 or Architecture.Ppc64le => 0x8000,
        _ => 0,
    };

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial SafeFileHandle Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatX(SafeFileHandle file, string path, int flags, uint mask, ref byte buffer);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatX(int directoryDescriptor, string path, int flags, uint mask, ref byte buffer);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int FControl(SafeFileHandle file, int command);

    /// <summary>Reads the whole file, refusing anything but a plain file that belongs where it is.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its bytes.</returns>
    /// <exception cref="FileNotFoundException">If it, or a part of the path to it, is not there.</exception>
    /// <exception cref="UnauthorizedAccessException">If this account may not open it.</exception>
    /// <exception cref="StoreNotSafeToReadException">
    /// For anything else it will not read: a link, a FIFO, a device, a directory, a socket, a file
    /// with a second name that belongs to another account, one over <see cref="MaxBytes"/>, one that
    /// changes while it is read, and every other way the open, the questions or the read can fail.
    /// It throws nothing else for a path it was given, except <see cref="ArgumentException"/> for an
    /// empty one.
    /// </exception>
    public static byte[] ReadAllBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return ReadAllBytes(path, DirectoryOwnerOf(path), NoFollowFlag);
    }

    /// <summary>Reads the whole file like <see cref="ReadAllBytes(string)"/>, with its two decisions given.</summary>
    /// <param name="path">The file.</param>
    /// <param name="directoryOwner">The uid that owns the file's DIRECTORY, or null if unknown.</param>
    /// <param name="noFollow">The value of O_NOFOLLOW to open with, and to prove.</param>
    /// <returns>Its bytes.</returns>
    /// <remarks>
    /// For the tests, which cannot be root and so cannot make a file that belongs to another
    /// account, and cannot make this architecture's flag wrong. Production calls the other overload.
    /// </remarks>
    public static byte[] ReadAllBytes(string path, uint? directoryOwner, int noFollow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Verify(path, noFollow);

        using SafeFileHandle file = Open(path, OpenFlags(noFollow));

        if (file.IsInvalid)
        {
            throw ExceptionFor(path, Marshal.GetLastPInvokeError());
        }

        StoreShape shape = Inspect(path, file);

        if (Judge(path, shape, directoryOwner) is { } refusal)
        {
            throw refusal;
        }

        try
        {
            byte[] content = new byte[(int)shape.Size];
            int done = 0;

            while (done < content.Length)
            {
                int read = RandomAccess.Read(file, content.AsSpan(done), done);

                if (read == 0)
                {
                    throw new StoreNotSafeToReadException(path, "shrank while it was being read", StoreRefusalKind.Unstable);
                }

                done += read;
            }

            // One byte past what it said it was: a file that lies about its size (a /proc file
            // says zero) or that grows while it is read is refused, and not read in part.
            Span<byte> probe = stackalloc byte[1];

            if (RandomAccess.Read(file, probe, content.Length) > 0)
            {
                throw new StoreNotSafeToReadException(path, "grew while it was being read", StoreRefusalKind.Unstable);
            }

            return content;
        }
        catch (IOException error)
        {
            // A read that fails (EIO, ESTALE) is nothing planted and nothing the caller handles:
            // named here, it reaches the operator as a sentence and not as a stack trace.
            throw new StoreNotSafeToReadException(path, "could not be read: " + error.Message, StoreRefusalKind.Machine);
        }
    }

    /// <summary>What the kernel says a file is, asked by path and WITHOUT following a link or opening it.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its shape, or null if it cannot be asked (missing, not searchable, no statx).</returns>
    /// <remarks>
    /// Nothing is opened, so this may be asked of a FIFO. It is what a diagnostic uses to say, with
    /// the same words as the read, what the read would refuse.
    /// </remarks>
    public static StoreShape? Examine(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Span<byte> buffer = stackalloc byte[StatxSize];

        try
        {
            return StatX(AtFdcwd, path, AtSymlinkNoFollow, Wanted, ref MemoryMarshal.GetReference(buffer)) == 0
                ? ShapeIn(buffer)
                : null;
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Decides whether a file of this shape may be read by root.</summary>
    /// <param name="path">The file, for the sentence.</param>
    /// <param name="shape">What it is.</param>
    /// <param name="directoryOwner">The uid that owns its DIRECTORY, or null if unknown.</param>
    /// <returns>Null if it may be read, otherwise the refusal.</returns>
    /// <remarks>
    /// One decision for the read and for the diagnostic. An owner nobody can name is not the
    /// folder's owner: a second name is refused then, as the safe side.
    /// </remarks>
    public static StoreNotSafeToReadException? Judge(string path, StoreShape shape, uint? directoryOwner)
    {
        if (shape.Type == SymbolicLinkType)
        {
            return new StoreNotSafeToReadException(path, "is a symbolic link", StoreRefusalKind.SymbolicLink);
        }

        if (shape.Type != RegularFileType)
        {
            return new StoreNotSafeToReadException(
                path, "is " + Describe(shape.Type) + ", not a regular file", StoreRefusalKind.NotAPlainFile);
        }

        if (shape.Links != 1 && directoryOwner != shape.Owner)
        {
            return new StoreNotSafeToReadException(
                path,
                "has " + shape.Links.ToString(CultureInfo.InvariantCulture)
                    + " names and belongs to another account than its folder: a hard link may lead to a file that is not the service's",
                StoreRefusalKind.SecondName);
        }

        if (shape.Size > MaxBytes)
        {
            return new StoreNotSafeToReadException(
                path,
                "is larger than " + MaxBytes.ToString(CultureInfo.InvariantCulture) + " bytes",
                StoreRefusalKind.TooLarge);
        }

        return null;
    }

    /// <summary>Checks that a value of O_NOFOLLOW really refuses a symbolic link on this machine.</summary>
    /// <param name="noFollow">The value to check.</param>
    /// <returns>Null if it is proven, otherwise what is wrong.</returns>
    /// <remarks>
    /// Nothing is created on the disk and no FIFO is ever opened, so a wrong value cannot make the
    /// proof wait. It opens with <see cref="OpenFlags"/>, the very expression the read uses, so what
    /// it proves is the read's flags and not a copy of them. It proves O_NOFOLLOW and that the
    /// descriptor is close-on-exec. It does not prove O_NONBLOCK, whose value is the same on every
    /// architecture in the table: the test that opens a FIFO is what pins it.
    /// </remarks>
    public static string? ProveFlags(int noFollow)
    {
        if (noFollow == 0)
        {
            return "no value of O_NOFOLLOW has been verified for " + RuntimeInformation.ProcessArchitecture;
        }

        // /proc/self/cwd is a symbolic link, always: the right flag refuses it with ELOOP.
        using (SafeFileHandle link = Open("/proc/self/cwd", OpenFlags(noFollow)))
        {
            if (!link.IsInvalid)
            {
                return "O_NOFOLLOW did not refuse a symbolic link";
            }

            int errno = Marshal.GetLastPInvokeError();

            if (errno != TooManyLinks)
            {
                return "a symbolic link was refused with errno "
                    + errno.ToString(CultureInfo.InvariantCulture) + " and not ELOOP";
            }
        }

        using (SafeFileHandle status = Open("/proc/self/status", OpenFlags(noFollow)))
        {
            if (status.IsInvalid)
            {
                return "/proc/self/status could not be opened: "
                    + Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError());
            }

            int flags = FControl(status, GetDescriptorFlags);

            if (flags < 0 || (flags & CloseOnExecBit) == 0)
            {
                return "the descriptor is not close-on-exec";
            }
        }

        return null;
    }

    /// <summary>The exception a failed open becomes.</summary>
    /// <param name="path">The file.</param>
    /// <param name="errno">The error number.</param>
    /// <returns>The exception.</returns>
    /// <remarks>
    /// What callers already handle keeps its type, so a missing store is still a first run and not
    /// a refusal. ENOENT is a <see cref="FileNotFoundException"/> whether the file or a folder above
    /// it is missing: the two cannot be told apart without a second question about the path, and no
    /// caller treats them differently. Everything else is a <see cref="StoreNotSafeToReadException"/>
    /// with a sentence and a kind: a bare IOException would reach the operator as a stack trace.
    /// </remarks>
    public static Exception ExceptionFor(string path, int errno) => errno switch
    {
        2 => new FileNotFoundException("Could not find file '" + path + "'.", path),
        20 => new DirectoryNotFoundException("Could not find a part of the path '" + path + "'."),
        13 or 1 => new UnauthorizedAccessException("Access to the path '" + path + "' is denied."),
        TooManyLinks => new StoreNotSafeToReadException(path, "is a symbolic link", StoreRefusalKind.SymbolicLink),
        6 => new StoreNotSafeToReadException(
            path, "is a socket, or a device with nothing behind it", StoreRefusalKind.NotAPlainFile),
        11 => new StoreNotSafeToReadException(
            path, "is held by another process with a lease, and was not waited for", StoreRefusalKind.Busy),
        _ => new StoreNotSafeToReadException(
            path, "could not be opened safely: " + Marshal.GetPInvokeErrorMessage(errno), StoreRefusalKind.Machine),
    };

    /// <summary>The flags of the one open, and the ones the proof opens with: the same expression.</summary>
    private static int OpenFlags(int noFollow) => ReadOnly | NonBlocking | CloseOnExec | noFollow;

    /// <summary>The uid that owns the folder the file is in, or null if that cannot be read.</summary>
    private static uint? DirectoryOwnerOf(string path) =>
        Path.GetDirectoryName(Path.GetFullPath(path)) is { } directory
            ? UnixOwnership.OwnerOfPath(directory)?.Uid
            : null;

    /// <summary>Makes sure, once, that the flag means on this machine what the table says.</summary>
    /// <remarks>
    /// Only a success is remembered, and for the flag that was proved: a failure caused by a moment
    /// (no descriptors left, no memory) must not stay with a service that is launched by hand and
    /// keeps running, and a proof made for one value says nothing about another.
    /// </remarks>
    private static void Verify(string path, int noFollow)
    {
        if (noFollow != 0 && provenFlag == noFollow)
        {
            return;
        }

        string? problem;

        try
        {
            problem = ProveFlags(noFollow);
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            problem = "the C library cannot be used here: " + error.Message;
        }

        if (problem is not null)
        {
            throw new StoreNotSafeToReadException(
                path, "cannot be read safely on this machine: " + problem, StoreRefusalKind.Machine);
        }

        provenFlag = noFollow;
    }

    /// <summary>Asks the open descriptor what it is.</summary>
    private static StoreShape Inspect(string path, SafeFileHandle file)
    {
        Span<byte> buffer = stackalloc byte[StatxSize];

        try
        {
            if (StatX(file, string.Empty, AtEmptyPath, Wanted, ref MemoryMarshal.GetReference(buffer)) != 0)
            {
                throw new StoreNotSafeToReadException(
                    path,
                    "cannot be inspected: " + Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError()),
                    StoreRefusalKind.Machine);
            }
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            throw new StoreNotSafeToReadException(
                path, "cannot be inspected: this system has no statx", StoreRefusalKind.Machine);
        }

        return ShapeIn(buffer)
            ?? throw new StoreNotSafeToReadException(
                path, "cannot be inspected: the file system does not say what it is", StoreRefusalKind.Machine);
    }

    /// <summary>The shape a statx answer describes, or null if the kernel did not fill in every field asked for.</summary>
    private static StoreShape? ShapeIn(ReadOnlySpan<byte> statx)
    {
        // The kernel says WHICH fields it filled in, and one it did not must not be read as a value.
        if ((BitConverter.ToUInt32(statx[MaskOffset..]) & Wanted) != Wanted)
        {
            return null;
        }

        return new StoreShape(
            (uint)BitConverter.ToUInt16(statx[ModeOffset..]) & FileTypeMask,
            BitConverter.ToUInt32(statx[NlinkOffset..]),
            BitConverter.ToUInt64(statx[SizeOffset..]),
            BitConverter.ToUInt32(statx[UidOffset..]));
    }

    private static string Describe(uint type) => type switch
    {
        FifoType => "a FIFO",
        DirectoryType => "a directory",
        CharacterDeviceType => "a character device",
        BlockDeviceType => "a block device",
        SocketType => "a socket",
        _ => "of a kind nobody expects in this folder",
    };
}