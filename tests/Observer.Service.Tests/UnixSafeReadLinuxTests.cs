using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Primitives;
using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>What root reads in a folder that the service account controls.</summary>
/// <remarks>
/// The service account owns /etc/observer and can put anything there, while root reads the store
/// and the certificate from it. Measured on 0.24.4: a FIFO made the command wait for ever, a link
/// to /dev/zero grew its memory until the process was killed, and a link to a file shaped like the
/// store had its secret printed.
/// <para>
/// Anything that could hang is run through <see cref="Patience"/>: a regression must cost five
/// seconds and a red test, never a held runner. The old read is never run against /dev/zero or a
/// FIFO here: it would exhaust memory or wait for ever, and those reds were measured by hand.
/// </para>
/// </remarks>
[Collection(StrictStoreReads.Name)]
[SupportedOSPlatform("linux")]
public sealed class UnixSafeReadLinuxTests : IDisposable
{
    private const string Token = "GOODTOKEN";

    private readonly string directory;

    public UnixSafeReadLinuxTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "obs-safe-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);
    }

    private string StorePath => Path.Combine(directory, CredentialDirectory.FileName);

    [LinuxOnlyTheory]
    [InlineData(2, typeof(FileNotFoundException), "Could not find file", null)]
    [InlineData(20, typeof(DirectoryNotFoundException), "Could not find a part of the path", null)]
    [InlineData(13, typeof(UnauthorizedAccessException), "is denied", null)]
    [InlineData(1, typeof(UnauthorizedAccessException), "is denied", null)]
    [InlineData(40, typeof(StoreNotSafeToReadException), "is a symbolic link", StoreRefusalKind.SymbolicLink)]
    [InlineData(6, typeof(StoreNotSafeToReadException), "socket", StoreRefusalKind.NotAPlainFile)]
    [InlineData(11, typeof(StoreNotSafeToReadException), "lease", StoreRefusalKind.Busy)]
    [InlineData(24, typeof(StoreNotSafeToReadException), "could not be opened safely", StoreRefusalKind.Machine)]
    [InlineData(75, typeof(StoreNotSafeToReadException), "could not be opened safely", StoreRefusalKind.Machine)]
    [InlineData(5, typeof(StoreNotSafeToReadException), "could not be opened safely", StoreRefusalKind.Machine)]
    public void TheErrnoOfAFailedOpenBecomesTheRightException(int errno, Type expected, string fragment, StoreRefusalKind? kind)
    {
        // What the callers already handle keeps its type (a missing store is a first run, not a
        // refusal), and everything else in strict mode is a refusal with a sentence: a bare
        // IOException would reach the operator as a stack trace. The KIND says whether somebody
        // put the file there or this machine could not check it: the advice that follows differs.
        Exception error = UnixSafeRead.ExceptionFor("/etc/observer/credentials.json", errno);

        Assert.Equal(expected, error.GetType());
        Assert.Contains(fragment, error.Message, StringComparison.Ordinal);
        Assert.Contains("/etc/observer/credentials.json", error.Message, StringComparison.Ordinal);

        if (kind is { } wanted)
        {
            Assert.Equal(wanted, ((StoreNotSafeToReadException)error).Kind);
        }
    }

    [LinuxOnlyTheory]
    [InlineData(UnixSafeRead.RegularFileType, 1u, 100ul, 1000u, 1000u, null)]
    [InlineData(UnixSafeRead.RegularFileType, 1u, 100ul, 0u, 1000u, null)]
    [InlineData(UnixSafeRead.RegularFileType, 2u, 100ul, 1000u, 1000u, null)]
    [InlineData(UnixSafeRead.RegularFileType, 2u, 100ul, 0u, 1000u, StoreRefusalKind.SecondName)]
    [InlineData(UnixSafeRead.RegularFileType, 2u, 100ul, 1000u, null, StoreRefusalKind.SecondName)]
    [InlineData(UnixSafeRead.RegularFileType, 1u, 65537ul, 1000u, 1000u, StoreRefusalKind.TooLarge)]
    [InlineData(UnixSafeRead.RegularFileType, 1u, 65536ul, 1000u, 1000u, null)]
    [InlineData(UnixSafeRead.FifoType, 1u, 0ul, 1000u, 1000u, StoreRefusalKind.NotAPlainFile)]
    [InlineData(UnixSafeRead.DirectoryType, 2u, 4096ul, 1000u, 1000u, StoreRefusalKind.NotAPlainFile)]
    [InlineData(UnixSafeRead.SocketType, 1u, 0ul, 1000u, 1000u, StoreRefusalKind.NotAPlainFile)]
    [InlineData(UnixSafeRead.CharacterDeviceType, 1u, 0ul, 0u, 0u, StoreRefusalKind.NotAPlainFile)]
    [InlineData(UnixSafeRead.BlockDeviceType, 1u, 0ul, 0u, 0u, StoreRefusalKind.NotAPlainFile)]
    [InlineData(UnixSafeRead.SymbolicLinkType, 1u, 12ul, 1000u, 1000u, StoreRefusalKind.SymbolicLink)]
    public void WhatIsRefusedDependsOnWhatTheFileIsAndWhoOwnsIt(
        uint type, uint links, ulong size, uint owner, uint? directoryOwner, StoreRefusalKind? expected)
    {
        // A second name counts only for an inode that belongs to someone other than the folder's
        // owner, because that is the attack: a name for a root-owned file. The service's own
        // second names, and a backup made with "cp -al" of its files, belong to the folder's owner
        // and harm nobody. An owner nobody can name is refused, as the safe side.
        StoreNotSafeToReadException? refusal =
            UnixSafeRead.Judge("/etc/observer/credentials.json", new StoreShape(type, links, size, owner), directoryOwner);

        Assert.Equal(expected, refusal?.Kind);
    }

    [LinuxOnly]
    public void TheFlagsAreProvenOnThisMachineAndAWrongValueIsCaught()
    {
        // O_NOFOLLOW is not one number across architectures, and a wrong one is not a crash: it is
        // some other flag, and the link is followed in silence. So the value is proved by opening
        // a link the kernel always has and requiring it to be refused.
        Architecture architecture = RuntimeInformation.ProcessArchitecture;

        if (architecture is Architecture.X64 or Architecture.Arm64)
        {
            int flag = UnixSafeRead.NoFollowFlag;

            Assert.NotEqual(0, flag);
            Assert.Null(UnixSafeRead.ProveFlags(flag));

            // The other family's value is, on this one, a flag that changes nothing about links.
            int other = architecture == Architecture.X64 ? 0x8000 : 0x20000;
            string? problem = UnixSafeRead.ProveFlags(other);

            Assert.NotNull(problem);
            Assert.Contains("did not refuse a symbolic link", problem, StringComparison.Ordinal);
        }

        Assert.NotNull(UnixSafeRead.ProveFlags(0));
    }

    [LinuxOnlyTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3000)]
    [InlineData(UnixSafeRead.MaxBytes)]
    public void APlainFileIsReadByteForByte(int size)
    {
        string path = Path.Combine(directory, "plain");
        byte[] content = RandomNumberGenerator.GetBytes(size);
        File.WriteAllBytes(path, content);

        Assert.Equal(content, UnixSafeRead.ReadAllBytes(path));
    }

    [LinuxOnly]
    public void TheTextIsDecodedTheWayFileReadAllTextDecodesIt()
    {
        string path = Path.Combine(directory, "with-bom");
        byte[] text = Encoding.UTF8.GetBytes("{\"current\":\"caffè\"}");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. text]);

        using StrictScope strict = new();

        string read = StoreFile.ReadAllText(path);

        Assert.Equal(File.ReadAllText(path), read);
        Assert.DoesNotContain((char)0xFEFF, read);
    }

    [LinuxOnly]
    public void ALinkIsRefusedAndTheReadItReplacesWouldHaveFollowedIt()
    {
        string target = Path.Combine(directory, "target");
        File.WriteAllText(target, "TARGET");
        string link = Path.Combine(directory, "link");
        File.CreateSymbolicLink(link, target);

        // The control: the ordinary read goes straight through, which is what the attacker relies on.
        Assert.Equal("TARGET", File.ReadAllText(link));

        StoreNotSafeToReadException error =
            Assert.Throws<StoreNotSafeToReadException>(() => UnixSafeRead.ReadAllBytes(link));

        Assert.Contains("is a symbolic link", error.Message, StringComparison.Ordinal);
        Assert.Equal(link, error.FilePath);
        Assert.Equal(StoreRefusalKind.SymbolicLink, error.Kind);
    }

    [LinuxOnly]
    public void ALinkToDevZeroIsRefusedBeforeAnythingIsRead()
    {
        string link = Path.Combine(directory, "zero");
        File.CreateSymbolicLink(link, "/dev/zero");

        long before = GC.GetAllocatedBytesForCurrentThread();

        Assert.Throws<StoreNotSafeToReadException>(() => UnixSafeRead.ReadAllBytes(link));

        // Refused at open(): not a byte of /dev/zero was read, so nothing grew.
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 200_000);
    }

    [LinuxOnly]
    public async Task AFifoIsRefusedAtOnceAndNothingWaitsOnIt()
    {
        string fifo = Path.Combine(directory, "fifo");
        Run("mkfifo", fifo);

        Exception? error = await Patience.Patiently(
            () => Record.Exception(() => UnixSafeRead.ReadAllBytes(fifo)),
            fifo);

        StoreNotSafeToReadException refusal = Assert.IsType<StoreNotSafeToReadException>(error);

        Assert.Contains("FIFO", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(StoreRefusalKind.NotAPlainFile, refusal.Kind);
    }

    [LinuxOnly]
    public void ASecondNameOfTheFoldersOwnFileIsReadAsItAlwaysWas()
    {
        // The service account can make a second name for its own store with one "ln", and a backup
        // made with "cp -al" keeps its inode: both belong to the folder's owner. Refusing them
        // would let that account block "share" and "rotate-key --now" for free, and would send an
        // administrator to delete a healthy store over a backup.
        string first = Path.Combine(directory, "first");
        string second = Path.Combine(directory, "second");
        File.WriteAllText(first, "x");
        Run("ln", first, second);

        Assert.Equal("x"u8.ToArray(), UnixSafeRead.ReadAllBytes(second));
    }

    [LinuxOnly]
    public void ASecondNameOfAnotherAccountsFileIsRefused()
    {
        // The other account is played by telling the read who owns the folder, because only root
        // can make a file that belongs to somebody else (that case is proved as root below).
        string first = Path.Combine(directory, "first");
        string second = Path.Combine(directory, "second");
        File.WriteAllText(first, "x");
        Run("ln", first, second);

        uint me = uint.Parse(Run("stat", "-c", "%u", first), CultureInfo.InvariantCulture);

        StoreNotSafeToReadException error = Assert.Throws<StoreNotSafeToReadException>(
            () => UnixSafeRead.ReadAllBytes(second, me + 1, UnixSafeRead.NoFollowFlag));

        Assert.Equal(StoreRefusalKind.SecondName, error.Kind);
        Assert.Contains("2 names", error.Message, StringComparison.Ordinal);
        Assert.Contains("another account", error.Message, StringComparison.Ordinal);
        Assert.Equal("x"u8.ToArray(), UnixSafeRead.ReadAllBytes(second, me, UnixSafeRead.NoFollowFlag));
    }

    [RootOnlyFact]
    public void ForRootASecondNameOfAnInodeThatBelongsToAnotherAccountIsRefused()
    {
        // Proved by hand as root (podman): GitHub's runner is not root. The folder belongs to
        // 4242; root's file with two names is refused, and the same file handed to 4242 is read.
        string first = Path.Combine(directory, "first");
        string second = Path.Combine(directory, "second");
        File.WriteAllText(first, "x");
        Run("ln", first, second);
        Run("chown", "4242:4242", directory);

        StoreNotSafeToReadException error =
            Assert.Throws<StoreNotSafeToReadException>(() => UnixSafeRead.ReadAllBytes(second));

        Assert.Equal(StoreRefusalKind.SecondName, error.Kind);

        Run("chown", "4242:4242", first);

        Assert.Equal("x"u8.ToArray(), UnixSafeRead.ReadAllBytes(second));
    }

    [LinuxOnly]
    public void ReadingProvesTheFlagItOpensWithAndNotOnlyTheTable()
    {
        // The proof has to sit in the read itself: a refactor that dropped the call would leave
        // every test green on the architecture where the table is right, and ship a read that
        // follows links on the one where it is wrong. So it is shown with a flag that is wrong here.
        Architecture architecture = RuntimeInformation.ProcessArchitecture;

        if (architecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return;
        }

        string path = Path.Combine(directory, "plain");
        File.WriteAllText(path, "x");

        int wrong = architecture == Architecture.X64 ? 0x8000 : 0x20000;

        StoreNotSafeToReadException error = Assert.Throws<StoreNotSafeToReadException>(
            () => UnixSafeRead.ReadAllBytes(path, null, wrong));

        Assert.Equal(StoreRefusalKind.Machine, error.Kind);
        Assert.Contains("cannot be read safely on this machine", error.Message, StringComparison.Ordinal);

        // And a proof that was made for one flag is not taken for another.
        Assert.Equal("x"u8.ToArray(), UnixSafeRead.ReadAllBytes(path, null, UnixSafeRead.NoFollowFlag));
    }

    [LinuxOnly]
    public async Task AFileThatDeliversLessThanItSaidIsRefusedAndTheReadDoesNotSpin()
    {
        // sysfs says 4096 and delivers a few bytes: the same shape as a file that shrinks while it
        // is read, and the only thing that ends the loop. Without the check the loop would spin.
        const string Online = "/sys/devices/system/cpu/online";

        Assert.True(File.Exists(Online), "This test needs sysfs, which every Linux with CPUs has.");

        Exception? error = await Patience.Patiently(() => Record.Exception(() => UnixSafeRead.ReadAllBytes(Online)));

        StoreNotSafeToReadException refusal = Assert.IsType<StoreNotSafeToReadException>(error);

        Assert.Equal(StoreRefusalKind.Unstable, refusal.Kind);
        Assert.Contains("shrank while it was being read", refusal.Message, StringComparison.Ordinal);
    }

    [LinuxOnly]
    public void ARealReadErrorIsARefusalAndNotABareIOException()
    {
        // /proc/self/mem is a regular file of size zero whose every read fails with EIO: the read
        // error reaches the caller as IOException unless it is caught and named.
        StoreNotSafeToReadException refusal =
            Assert.Throws<StoreNotSafeToReadException>(() => UnixSafeRead.ReadAllBytes("/proc/self/mem"));

        Assert.Equal(StoreRefusalKind.Machine, refusal.Kind);
        Assert.Contains("could not be read", refusal.Message, StringComparison.Ordinal);
    }

    [LinuxOnly]
    public void WhatExamineSaysIsWhatThePathIsAndNeverWhatALinkPointsTo()
    {
        // Doctor asks this by path, without opening anything, so it may be asked of a FIFO.
        string plain = Path.Combine(directory, "plain");
        File.WriteAllText(plain, "four");
        string link = Path.Combine(directory, "link");
        File.CreateSymbolicLink(link, plain);
        string fifo = Path.Combine(directory, "fifo");
        Run("mkfifo", fifo);

        uint me = uint.Parse(Run("stat", "-c", "%u", plain), CultureInfo.InvariantCulture);

        Assert.Equal(new StoreShape(UnixSafeRead.RegularFileType, 1, 4, me), UnixSafeRead.Examine(plain));
        Assert.Equal(UnixSafeRead.SymbolicLinkType, UnixSafeRead.Examine(link)?.Type);
        Assert.Equal(UnixSafeRead.FifoType, UnixSafeRead.Examine(fifo)?.Type);
        Assert.Null(UnixSafeRead.Examine(Path.Combine(directory, "missing")));
    }

    [LinuxOnly]
    public void AFileLargerThanTheLimitIsRefusedUnreadAndOneOfExactlyTheLimitIsNot()
    {
        string large = Path.Combine(directory, "large");
        string exact = Path.Combine(directory, "exact");

        // Sparse: a few bytes on the disk, and never read because it is refused at its size.
        Run("truncate", "-s", (UnixSafeRead.MaxBytes + 1).ToString(CultureInfo.InvariantCulture), large);
        Run("truncate", "-s", UnixSafeRead.MaxBytes.ToString(CultureInfo.InvariantCulture), exact);

        StoreNotSafeToReadException error =
            Assert.Throws<StoreNotSafeToReadException>(() => UnixSafeRead.ReadAllBytes(large));

        Assert.Contains("is larger than 65536 bytes", error.Message, StringComparison.Ordinal);
        Assert.Equal(StoreRefusalKind.TooLarge, error.Kind);
        Assert.Equal(UnixSafeRead.MaxBytes, UnixSafeRead.ReadAllBytes(exact).Length);
    }

    [LinuxOnly]
    public void ADirectoryAndASocketAreNotFiles()
    {
        string folder = Path.Combine(directory, "folder");
        Directory.CreateDirectory(folder);

        Assert.Contains(
            "directory",
            Assert.Throws<StoreNotSafeToReadException>(() => UnixSafeRead.ReadAllBytes(folder)).Message,
            StringComparison.Ordinal);

        string socketPath = Path.Combine(directory, "sock");
        using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));

        Assert.Contains(
            "socket",
            Assert.Throws<StoreNotSafeToReadException>(() => UnixSafeRead.ReadAllBytes(socketPath)).Message,
            StringComparison.Ordinal);
    }

    [LinuxOnly]
    public void WhatIsMissingKeepsTheExceptionsTheStoreAlreadyHandles()
    {
        // CredentialStore.Read tells "it is not there yet" (generate one) from "I cannot read it"
        // (stop) by these two types. Losing them would regenerate the key on a path typo.
        Assert.Throws<FileNotFoundException>(() => UnixSafeRead.ReadAllBytes(Path.Combine(directory, "missing")));

        string file = Path.Combine(directory, "file");
        File.WriteAllText(file, "x");

        Assert.Throws<DirectoryNotFoundException>(() => UnixSafeRead.ReadAllBytes(Path.Combine(file, "inside")));
    }

    [UnprivilegedFact]
    public void AFileThisAccountCannotOpenIsAnAccessErrorAndNotARefusal()
    {
        string locked = Path.Combine(directory, "locked");
        File.WriteAllText(locked, "x");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        Assert.Throws<UnauthorizedAccessException>(() => UnixSafeRead.ReadAllBytes(locked));
    }

    [LinuxOnly]
    public void AFileWhoseSizeLiesIsRefusedAsGrown()
    {
        // /proc files say they are empty and are not: the same shape as a file that grows while it
        // is read, which a size check alone would miss.
        StoreNotSafeToReadException error =
            Assert.Throws<StoreNotSafeToReadException>(() => UnixSafeRead.ReadAllBytes("/proc/self/status"));

        Assert.Contains("grew while it was being read", error.Message, StringComparison.Ordinal);
    }

    [UnprivilegedFact]
    public void AnUnprivilegedReaderKeepsTheReadItAlwaysHadEvenThroughALink()
    {
        // The service is not root, reads its own folder and must read exactly as before. A store
        // that is a link (a secrets mount, say) works for it today and has to go on working.
        string real = Path.Combine(directory, "real.json");
        CredentialStore.Write(real, new MachineCredentials(Token, null, null));
        File.CreateSymbolicLink(StorePath, real);

        Assert.False(StoreFile.MustBeStrict(StorePath));
        Assert.Equal(Token, CredentialStore.Read(StorePath)!.Current);
    }

    [LinuxOnly]
    public void TheCredentialStoreRefusesALinkAndNeverShowsItsToken()
    {
        string real = Path.Combine(directory, "real.json");
        CredentialStore.Write(real, new MachineCredentials(Token, null, null));
        File.CreateSymbolicLink(StorePath, real);

        using StrictScope strict = new();

        StoreNotSafeToReadException error =
            Assert.Throws<StoreNotSafeToReadException>(() => CredentialStore.Read(StorePath));

        Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);
    }

    [LinuxOnly]
    public async Task TheCredentialStoreRefusesAFifoInsteadOfWaitingOnIt()
    {
        Run("mkfifo", StorePath);

        using StrictScope strict = new();

        Exception? error = await Patience.Patiently(
            () => Record.Exception(() => CredentialStore.Read(StorePath)),
            StorePath);

        Assert.IsType<StoreNotSafeToReadException>(error);
    }

    [LinuxOnly]
    public void AReloadThroughALinkKeepsTheKeysInForce()
    {
        // A running service that is handed a hostile store must go on serving what it has: wiping
        // the keys would lock the owner out of a machine that is otherwise healthy.
        CredentialDirectory.Prepare(StorePath);
        CredentialStore.Write(StorePath, new MachineCredentials("old-key", null, null));

        CredentialSource source = new(new ProvisionedCredentials(
            CredentialStore.Read(StorePath)!,
            CredentialOrigin.Stored,
            StorePath));

        string other = Path.Combine(directory, "other.json");
        CredentialStore.Write(other, new MachineCredentials("planted-key", null, null));
        File.Delete(StorePath);
        File.CreateSymbolicLink(StorePath, other);

        using StrictScope strict = new();

        Assert.Equal(ReloadResult.StoreUnusable, source.Reload().Result);
        Assert.True(source.IsTokenValid(new StringValues("Bearer old-key"), DateTimeOffset.UtcNow));
        Assert.False(source.IsTokenValid(new StringValues("Bearer planted-key"), DateTimeOffset.UtcNow));
    }

    [LinuxOnly]
    public void ACertificateThroughALinkIsNotAdoptedAsTheServicesIdentity()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        using X509Certificate2 foreign = MachineCertificate.Create("foreign", now);
        string foreignFile = Path.Combine(directory, "foreign.pfx");
        File.WriteAllBytes(foreignFile, MachineCertificate.Export(foreign));

        CredentialDirectory.Prepare(StorePath);
        File.CreateSymbolicLink(MachineCertificate.PathNextTo(StorePath), foreignFile);

        // The control, measured before the change: a service launched by hand as root adopted the
        // certificate the link pointed at, and served its fingerprint as the machine's own.
        ProvisionedCertificate adopted = CertificateProvisioning.Provision(StorePath, "machine", now, runningAsService: false);

        Assert.Equal(CertificateOrigin.Stored, adopted.Origin);
        Assert.Equal(MachineCertificate.Fingerprint(foreign), adopted.Fingerprint);
        adopted.Certificate.Dispose();

        using StrictScope strict = new();

        Assert.Throws<StoreNotSafeToReadException>(
            () => CertificateProvisioning.Provision(StorePath, "machine", now, runningAsService: false));
    }

    [LinuxOnly]
    public async Task AStoreThatWillNotBeReadIsNeitherReplacedNorAdopted()
    {
        // Launched by hand, a store that cannot be interpreted is never replaced: it may be the real
        // one. "Not read" is the same: not known to be empty, so not something to write over.
        Run("mkfifo", StorePath);

        using StrictScope strict = new();

        Exception? error = await Patience.Patiently(
            () => Record.Exception(() => CredentialProvisioning.Provision(null, StorePath, runningAsService: false)),
            StorePath);

        Assert.IsType<StoreNotSafeToReadException>(error);
        Assert.Contains("fifo", Run("stat", "-c", "%F", StorePath), StringComparison.Ordinal);
    }

    [RootOnlyFact]
    public void ForRootTheSwitchIsWhoOwnsTheFolderAndNothingElse()
    {
        // Proved by hand as root (podman): GitHub's runner is not root. The folder is root's, so there
        // is nobody to be protected from and the old read stands; handed to another account it is
        // strict, and a store that is root's inside it is still a plain file and still read.
        string real = Path.Combine(directory, "real.json");
        CredentialStore.Write(real, new MachineCredentials(Token, null, null));
        File.CreateSymbolicLink(StorePath, real);

        Assert.False(StoreFile.MustBeStrict(StorePath));
        Assert.Equal(Token, CredentialStore.Read(StorePath)!.Current);

        Run("chown", "4242:4242", directory);

        Assert.True(StoreFile.MustBeStrict(StorePath));
        Assert.Throws<StoreNotSafeToReadException>(() => CredentialStore.Read(StorePath));

        Assert.Equal(Token, CredentialStore.Read(real)!.Current);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Run(string tool, params string[] arguments) =>
        CredentialOwnershipLinuxTests.Run(tool, arguments);
}