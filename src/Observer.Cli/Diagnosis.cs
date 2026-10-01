using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Observer.Core.Security;
using Observer.Service.Credentials;

namespace Observer.Cli;

/// <summary>The answers the <c>doctor</c> verb prints, one after another.</summary>
public static class Diagnosis
{
    /// <summary>How well protected the store is, in one sentence.</summary>
    /// <param name="storePath">The path of the store.</param>
    /// <returns>The verdict, with the reason inside it.</returns>
    public static string DescribeProtection(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        if (Path.GetDirectoryName(storePath) is not { Length: > 0 } directory)
        {
            return "UNKNOWN - the store path has no directory.";
        }

        if (!OperatingSystem.IsWindows())
        {
            return Directory.Exists(directory)
                ? "the directory exists; on Linux the mode is enforced at 0700 by the service."
                : "ABSENT - the service has not created it yet.";
        }

        DirectoryVerdict verdict = WindowsDirectoryTrust.VerdictFor(directory);

        if (verdict == DirectoryVerdict.Unknown && Directory.Exists(directory))
        {
            // This is what a WELL PROTECTED store shows to any account, and it is the most
            // common case of all: reading a directory's permissions requires a permission on
            // that directory, and a properly protected store grants none.
            return
                "UNREADABLE FROM HERE - the directory exists but this account can't read its " +
                "permissions. That is what a correctly protected store looks like from an " +
                "ordinary account, and it rules out the common failure: a directory that merely " +
                "inherits its parent's permissions is readable by every user on the machine. " +
                "It does NOT prove the owner is right. Run this from an elevated terminal for a " +
                "definitive verdict.";
        }

        return DescribeVerdict(verdict);
    }

    /// <summary>The machine certificate's fingerprint, in one sentence.</summary>
    /// <param name="storePath">The path of the token store.</param>
    /// <returns>The readable fingerprint, or the reason it cannot be seen.</returns>
    /// <remarks>
    /// It tells the three cases apart the way <see cref="DescribeProtection"/> does, and for the
    /// same reason: "I cannot read it from here" and "it does not exist" call for two different
    /// actions, and confusing them sends the reader hunting for a fault that is not there.
    /// </remarks>
    public static string DescribeCertificate(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        string certificatePath = MachineCertificate.PathNextTo(storePath);

        try
        {
            // LoadForInspection and not Load: what is needed here is the fingerprint, not a
            // certificate to serve with, and Load would leave the private key in the key store
            // of whoever ran the command.
            using X509Certificate2 certificate =
                MachineCertificate.LoadForInspection(StoreFile.ReadAllBytes(certificatePath));

            return CertificateFingerprint.ForHumans(MachineCertificate.Fingerprint(certificate));
        }
        catch (StoreNotSafeToReadException error)
        {
            // Root does not read what the service account may have put where the certificate
            // belongs, and it says so instead of printing the fingerprint of whatever that is.
            return ("NOT READ - " + DescribeRefusal(error) + " " + CostOfRemovingCertificate(error)).TrimEnd();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            // "I did not find it" is not "it is not there". On a directory this account cannot
            // even list - that is, on a correctly protected store - the two are
            // indistinguishable from the outside, and passing the first off as the second
            // would send the reader hunting for a fault that does not exist.
            return CanListDirectory(certificatePath)
                ? "not created yet - the service writes it the first time it starts."
                : "NOT VISIBLE FROM HERE - this account can't list the directory, so this is " +
                  "not proof that the certificate is missing. Run this from an elevated terminal.";
        }
        catch (UnauthorizedAccessException)
        {
            return "UNREADABLE FROM HERE - run this from an elevated terminal.";
        }
        catch (CryptographicException error)
        {
            return "DAMAGED - " + StoreFile.Printable(error.Message);
        }
    }

    /// <summary>What to tell an operator whose store or certificate root refused to read.</summary>
    /// <param name="error">The refusal.</param>
    /// <returns>The sentence: what the file is, why root does not read it, how to look at it.</returns>
    /// <remarks>
    /// It names the reason and the way to look, and it does not say "elevated terminal": the account
    /// IS elevated, and blaming the terminal for what somebody put in the folder sends the reader
    /// to the wrong place.
    /// </remarks>
    public static string DescribeRefusal(StoreNotSafeToReadException error)
    {
        ArgumentNullException.ThrowIfNull(error);

        string look = "Look at it with: sudo ls -l " + error.FilePath;

        // The last two kinds are not something somebody put there, and saying so would accuse an
        // account of what it did not do.
        return error.Kind switch
        {
            StoreRefusalKind.Machine => error.Message
                + " Nothing is known to be wrong with the file: this machine could not check it, "
                + "so root did not read it. " + look,
            StoreRefusalKind.Busy => error.Message
                + " It may well be the service writing it, so root did not wait for it. " + look,
            _ => error.Message
                + " The account that runs the service owns that folder and can put anything in it, so "
                + "root does not follow, wait on or read what it finds there. " + look,
        };
    }

    /// <summary>What to do about a refusal, in lines to print under the sentence that names it.</summary>
    /// <param name="error">The refusal.</param>
    /// <returns>The lines. They say what removing the file costs when removing it is the way out,
    /// and say it is NOT when the machine, and not the folder, is what is wrong.</returns>
    public static IReadOnlyList<string> DescribeWayOut(StoreNotSafeToReadException error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return error.Kind switch
        {
            StoreRefusalKind.Machine =>
            [
                "Nothing is known to be wrong with the file, so do not delete it.",
                "Look at it: sudo ls -li " + error.FilePath,
                // Bounded: "could not check" is also "not known to be no FIFO", and a plain cat
                // would wait on one, or print a file of any size.
                "Read a little of it: sudo timeout 5 head -c 4096 " + error.FilePath,
                "If that works and the file looks right, it is this machine that could",
                "not check it: report what \"uname -m\" and \"uname -r\" print.",
            ],
            StoreRefusalKind.Busy =>
            [
                "Try again in a moment: something holds the file, often the service writing it.",
            ],
            StoreRefusalKind.SymbolicLink =>
            [
                "If you made that link yourself, read what it points at with:",
                "        sudo cat <the file it points to>",
                .. RemovalAdvice,
            ],
            StoreRefusalKind.SecondName =>
            [
                "See every name the file has: sudo find / -xdev -samefile " + error.FilePath,
                "If the other name is a copy you made yourself, remove that copy.",
                .. RemovalAdvice,
            ],
            _ => RemovalAdvice,
        };
    }

    /// <summary>The way out when somebody put something in the folder, and what it costs.</summary>
    internal static readonly string[] RemovalAdvice =
    [
        "Fix   : otherwise stop the service (sudo systemctl stop observer), remove what does",
        "        not belong and start it again. Without credentials.json the service creates",
        "        a NEW token, and every dashboard that holds the old one stops connecting",
        "        until it is given that one.",
    ];

    /// <summary>What removing a refused certificate costs, or nothing when it should not be removed.</summary>
    /// <param name="error">The refusal.</param>
    /// <returns>The sentence, or an empty string for a refusal that is not somebody's doing.</returns>
    public static string CostOfRemovingCertificate(StoreNotSafeToReadException error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return error.Kind is StoreRefusalKind.Machine or StoreRefusalKind.Busy
            ? string.Empty
            : "Removing it makes the service create a new certificate: its fingerprint changes, "
                + "and \"observer share\" then shows the new one.";
    }

    /// <summary>What to say when the store cannot be read for a reason that is not a refusal.</summary>
    /// <param name="error">An access denial, or a store that exists and is not usable.</param>
    /// <param name="storePath">The store.</param>
    /// <param name="windows">Whether this is Windows, where the answer is an elevated terminal.</param>
    /// <returns>The lines to print on standard error.</returns>
    /// <remarks>
    /// The Windows text says "Run as administrator" and names SYSTEM, and on Linux it sent the
    /// reader looking for a terminal setting that does not exist: there it is sudo, or a file that
    /// is damaged, and the two are told apart.
    /// </remarks>
    public static IReadOnlyList<string> DescribeUnreadable(Exception error, string storePath, bool windows)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (windows)
        {
            return
            [
                "Can't read the machine token: an elevated terminal is required.",
                "Store : " + storePath,
                "Why   : the file grants access only to SYSTEM and to local",
                "        administrators. That is deliberate — this token is",
                "        valid FROM THE NETWORK and does not expire.",
                "You   : " + CurrentAccountName() + ", elevated: " + ElevatedAsText(),
                "Fix   : reopen the terminal with 'Run as administrator'.",
                string.Empty,
                "Note  : to watch THIS machine you need no token at all.",
                "        The dashboard connects through the local channel.",
                "Detail: " + StoreFile.Printable(error.Message),
            ];
        }

        if (error is UnauthorizedAccessException || error.InnerException is UnauthorizedAccessException)
        {
            return
            [
                "Can't read the machine token: this account is not allowed to.",
                "Store : " + storePath,
                "Why   : the file is closed to everyone but the account that owns it and",
                "        root. That is deliberate: this token is valid FROM THE NETWORK",
                "        and does not expire.",
                "You   : " + CurrentAccountName() + ", root: " + ElevatedAsText(),
                "Fix   : run the same command with sudo.",
                string.Empty,
                "Note  : to watch THIS machine you need no token at all.",
                "        The dashboard connects through the local channel.",
                "Detail: " + StoreFile.Printable(error.Message),
            ];
        }

        return
        [
            "Can't read the machine token: the store exists but is not usable.",
            "Store : " + storePath,
            "Detail: " + StoreFile.Printable(error.Message),
            "Look  : sudo cat -v " + storePath,
            "Fix   : if it is damaged beyond repair, stop the service (sudo systemctl stop",
            "        observer), remove the file and start it again. The service then creates",
            "        a NEW token, and every dashboard that holds the old one stops connecting",
            "        until it is given that one.",
        ];
    }

    /// <summary>Whether this account can even list the store's directory.</summary>
    private static bool CanListDirectory(string certificatePath)
    {
        try
        {
            string directory = Path.GetDirectoryName(certificatePath) ?? ".";

            _ = Directory.EnumerateFileSystemEntries(directory).GetEnumerator().MoveNext();

            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Who is running this command.</summary>
    /// <returns>The account name.</returns>
    public static string CurrentAccountName() =>
        OperatingSystem.IsWindows() ? WindowsAccountName() : Environment.UserName;

    /// <summary>Whether the command is running with administrative privileges.</summary>
    /// <returns>"True" or "False": Administrators on Windows, root elsewhere.</returns>
    public static string ElevatedAsText() =>
        OperatingSystem.IsWindows()
            ? IsElevatedOnWindows().ToString()
            : (Environment.UserName == "root").ToString();

    /// <summary>Whether the store file belongs to the account that owns its directory.</summary>
    /// <param name="file">Who owns the store file, or null if that cannot be read.</param>
    /// <param name="directory">Who owns the store's directory, or null if that cannot be read.</param>
    /// <returns>The verdict.</returns>
    /// <remarks>
    /// Pure, so its table is the specification. Only the ACCOUNT counts: the .deb runs the service
    /// as whoever owns the directory, and the file is 0600, so a file of another account is one
    /// the service cannot read, while a file of the same account in another group is read just
    /// the same. The NAMES never count, because one side may have none. And a missing answer is
    /// <see cref="OwnershipVerdict.Unknown"/> and never a mismatch: uid 0 is root, not "nothing".
    /// </remarks>
    public static OwnershipVerdict JudgeOwnership(StoreOwner? file, StoreOwner? directory)
    {
        if (file is not { } actual || directory is not { } expected)
        {
            return OwnershipVerdict.Unknown;
        }

        return actual.Uid == expected.Uid ? OwnershipVerdict.Matches : OwnershipVerdict.Mismatch;
    }

    /// <summary>Who owns the store and its directory, read from this machine, in lines to print.</summary>
    /// <param name="storePath">The path of the store file.</param>
    /// <returns>The lines from <see cref="DescribeOwnership(string, StoreOwner?, StoreOwner?)"/>.</returns>
    /// <remarks>
    /// Nothing but stat: the file is never opened, so it works as any account. What it cannot do
    /// as another account is reach the file, because /etc/observer is 0700: there the file's owner
    /// is unknown and the directory's is not, and the lines say so.
    /// </remarks>
    public static IReadOnlyList<string> DescribeOwnership(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        // Before any owner is read: statx follows a link, so the owner of a link's target would
        // be judged and, when it is root's, answered with a chown BY PATH that follows it too.
        // The directory belongs to the service account, which faces the network, so it can put a
        // link there; the package leaves a link alone for the same reason and so does this.
        if (IsSymbolicLink(storePath))
        {
            return
            [
                "NOT A PLAIN FILE - the store is a symbolic link.",
                "The package leaves a link alone, and so does doctor: changing",
                "the owner by path would change whatever the link points to.",
                "Look at it by hand:",
                "sudo ls -l " + storePath,
            ];
        }

        string? directory = Path.GetDirectoryName(storePath);
        StoreOwner? directoryOwner = directory is { Length: > 0 } ? OwnerOf(directory) : null;

        // Before the owner is judged, for the same reason as the link above: a chown changes the
        // INODE, so a second name of a file that belongs to another account must not be answered
        // with one. The shape is judged by the very rule the read applies, so what doctor calls
        // wrong is what share would refuse.
        if (DescribeShape(storePath, directoryOwner?.Uid) is { } notPlain)
        {
            return notPlain;
        }

        return DescribeOwnership(storePath, OwnerOf(storePath), directoryOwner);
    }

    /// <summary>What the store is, when it is something root will not read.</summary>
    /// <param name="storePath">The store.</param>
    /// <param name="directoryOwner">The uid that owns its folder, or null if that cannot be read.</param>
    /// <returns>The lines, or null when the store is an ordinary file or cannot be asked about.</returns>
    private static IReadOnlyList<string>? DescribeShape(string storePath, uint? directoryOwner)
    {
        if (!OperatingSystem.IsLinux()
            || UnixSafeRead.Examine(storePath) is not { } shape
            || UnixSafeRead.Judge(storePath, shape, directoryOwner) is not { } refusal)
        {
            return null;
        }

        string[] look = ["Look at it by hand:", "sudo ls -li " + storePath];

        return refusal.Kind switch
        {
            StoreRefusalKind.SecondName =>
            [
                "NOT A PLAIN FILE - the store has a second name.",
                "It belongs to another account than its folder, so the other",
                "name may lead to a file that is not the service's, and a",
                "change of owner would change that file too.",
                "See every name it has:",
                "sudo find / -xdev -samefile " + storePath,
                .. look,
            ],
            StoreRefusalKind.TooLarge =>
            [
                "TOO LARGE - the store is larger than 64 KiB.",
                "A real one is a few hundred bytes. Root does not read it.",
                .. look,
            ],
            _ =>
            [
                "NOT A PLAIN FILE - the store is not a regular file.",
                "Root does not read what the service account can put in its",
                "folder, so \"observer share\" refuses it. doctor offers no",
                "change of owner for it either.",
                .. look,
            ],
        };
    }

    /// <summary>The lines that say who owns the store and what to do if that is wrong.</summary>
    /// <param name="storePath">The path of the store file, as it appears in the command to run.</param>
    /// <param name="file">Who owns the store file, or null if that cannot be read.</param>
    /// <param name="directory">Who owns the store's directory, or null if that cannot be read.</param>
    /// <returns>The first line answers the question; the ones after it explain and, for a
    /// mismatch, are the two commands that repair it, last and in the order to run them.</returns>
    /// <remarks>
    /// The account to hand the file to is the directory's owner, read here, and not a name the
    /// package ships: it is the account the service's own code hands a store to
    /// (<see cref="UnixOwnership.FollowDirectory"/>). A number stands in for a name that
    /// /etc/passwd and /etc/group do not list, which chown accepts. The commands are quoted in a
    /// test, and were run by hand on an installed package before that: the unit is called
    /// observer. <paramref name="storePath"/> is printed as it is, without quoting, because the
    /// only caller passes the default path, a constant with no space in it: a path taken from
    /// configuration would have to be quoted first. The text is about the default layout and
    /// judges nothing else: file and directory both root's read as OK although the unit's
    /// account could not reach them.
    /// </remarks>
    public static IReadOnlyList<string> DescribeOwnership(string storePath, StoreOwner? file, StoreOwner? directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        // Every line is at most 62 columns, because "doctor" indents the lines after the first by
        // the width of its labels and the terminal it is read in is often 80 wide.
        string fileLine = "file      : " + Show(file);
        string directoryLine = "directory : " + Show(directory);

        switch (JudgeOwnership(file, directory))
        {
            case OwnershipVerdict.Matches:
                return ["OK - the file belongs to the account that owns its directory.", fileLine, directoryLine];

            case OwnershipVerdict.Mismatch when directory is { } wanted:
                return
                [
                    "WRONG OWNER - the file belongs to another account.",
                    fileLine,
                    directoryLine,
                    "The service runs as the account that owns the directory, so",
                    "it can't read a file that is closed to everyone else. A",
                    "running service carries on; its next start fails and systemd",
                    "keeps restarting it.",
                    "\"sudo observer rotate-key\" left files like this before 0.24.2.",
                    "Hand the file back and restart the service:",
                    // -h: the change is made to the path itself and never to what a link there points
                    // to. Root runs this inside a directory the service account owns and can put a
                    // link in, and for a plain file the flag changes nothing.
                    "sudo chown -h " + (wanted.User ?? Number(wanted.Uid)) + ":" + (wanted.Group ?? Number(wanted.Gid)) +
                    " " + storePath,
                    "sudo systemctl restart observer",
                ];

            default:
                return
                [
                    "UNKNOWN - an owner can't be read from here.",
                    fileLine,
                    directoryLine,
                    "The file may not exist yet, or this account can't search its",
                    "directory, which is closed to other accounts.",
                    "\"sudo observer doctor\" can read them.",
                ];
        }
    }

    /// <summary>The name a numeric id has in a system database, if the database lists one.</summary>
    /// <param name="databasePath">/etc/passwd for an account, /etc/group for a group.</param>
    /// <param name="id">The numeric id.</param>
    /// <returns>The name on the first line that carries the id, or null.</returns>
    /// <remarks>
    /// Both files are "name:x:id:..." and System.IO reads them, so there is no P/Invoke for a name.
    /// What this does not see is an account the system gets from somewhere else (LDAP, SSSD): its
    /// name stays a number, which is what "if resolvable" means, and the number is enough to act.
    /// </remarks>
    public static string? NameIn(string databasePath, uint id)
    {
        string wanted = id.ToString(CultureInfo.InvariantCulture);

        try
        {
            return File.ReadLines(databasePath)
                .Select(line => line.Split(':'))
                .Where(fields => fields.Length >= 3 && fields[0].Length > 0 && fields[0][0] != '#'
                    && fields[2] == wanted)
                .Select(fields => fields[0])
                .FirstOrDefault();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A diagnostic that cannot look says nothing about a link: the owners are still asked.
            return false;
        }
    }

    private static StoreOwner? OwnerOf(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        return UnixOwnership.OwnerOfPath(path) is { } owner
            ? new StoreOwner(owner.Uid, owner.Gid, NameIn("/etc/passwd", owner.Uid), NameIn("/etc/group", owner.Gid))
            : null;
    }

    private static string Show(StoreOwner? owner) => owner is not { } known
        ? "not readable from here"
        : "uid " + Number(known.Uid) + (known.User is null ? string.Empty : " (" + known.User + ")")
            + ", gid " + Number(known.Gid) + (known.Group is null ? string.Empty : " (" + known.Group + ")");

    private static string Number(uint id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>The sentence that explains a verdict to whoever is reading the screen.</summary>
    /// <param name="verdict">The outcome of examining the directory.</param>
    /// <returns>The sentence shown on screen.</returns>
    public static string DescribeVerdict(DirectoryVerdict verdict) => verdict switch
    {
        DirectoryVerdict.Safe =>
            "PROTECTED - as this account sees it: owned by SYSTEM, Administrators or this very " +
            "account, and nobody else is granted access. The installed service trusts only SYSTEM " +
            "and Administrators, so if it still refuses to start, believe the service.",
        DirectoryVerdict.Missing =>
            "ABSENT - the service has not created it yet. Start it once.",
        DirectoryVerdict.OpenDacl =>
            "NOT PROTECTED - the permissions name an account other than SYSTEM and Administrators, " +
            "or they inherit from ProgramData, which on a default machine lets every account " +
            "WRITE. Whoever can write the token can use it from anywhere FROM THE NETWORK. If the " +
            "directory is NOT empty the service will refuse to start and will change nothing, " +
            "because a token or certificate found here cannot be shown to be its own. Reset the " +
            "permissions and lock them to SYSTEM and Administrators - the README, section Packages " +
            "at github.com/Akr0n/observer, has the exact icacls lines - and only if the files " +
            "inside are yours, because that makes the service trust them. If the directory IS " +
            "empty the service replaces it with a protected one and starts (unless its owner " +
            "took SYSTEM out of the permissions: then the service cannot list it and refuses).",
        DirectoryVerdict.UntrustedOwner =>
            "FALSELY PROTECTED - the permissions may name only SYSTEM and Administrators, but the " +
            "OWNER is neither SYSTEM nor the Administrators group (an individual administrator's " +
            "own account counts as untrusted), and an owner can grant itself access again " +
            "whenever it likes. This looks safe and is not. If the directory is NOT empty the " +
            "service will refuse to start and will change nothing, because a token or " +
            "certificate found here cannot be shown to be its own: the folder was recreated by " +
            "hand, restored without its permissions, or left by a service run by hand. Make the " +
            "Administrators group the owner AND lock the permissions to SYSTEM and " +
            "Administrators; the README, section Packages at github.com/Akr0n/observer, has the " +
            "exact icacls lines. Do it only if the files inside are yours. If the directory IS " +
            "empty the service replaces it with a protected one and starts (unless its owner " +
            "took SYSTEM out of the permissions: then the service cannot list it and refuses).",
        DirectoryVerdict.ReparsePoint =>
            "HIJACKED - the path is a junction or symbolic link, so the token would be written " +
            "wherever it points. A standard user can create one without any privilege. Remove it.",
        _ => "UNKNOWN - the directory can't be examined from here. Try an elevated terminal.",
    };

    [SupportedOSPlatform("windows")]
    private static string WindowsAccountName()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        return identity.Name;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevatedOnWindows()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>Who owns something, as the kernel reports it, with the names /etc lists for it.</summary>
/// <param name="Uid">The numeric account: the only part that is compared.</param>
/// <param name="Gid">The numeric group.</param>
/// <param name="User">The account's name, or null where the system does not list it.</param>
/// <param name="Group">The group's name, or null where the system does not list it.</param>
public readonly record struct StoreOwner(uint Uid, uint Gid, string? User = null, string? Group = null);

/// <summary>Whether the store file belongs to the account that owns its directory.</summary>
/// <remarks>The zero value claims nothing, like <see cref="RevocationState"/>.</remarks>
public enum OwnershipVerdict
{
    /// <summary>One of the two owners could not be read.</summary>
    Unknown = 0,

    /// <summary>The same account owns both: the service reads its own store.</summary>
    Matches,

    /// <summary>Another account owns the file: the service cannot read it.</summary>
    Mismatch,
}