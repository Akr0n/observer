using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Observer.Service.Credentials;

/// <summary>
/// Gathers the facts about a directory from Windows, and puts it into a safe state.
/// </summary>
/// <remarks>
/// A separate, annotated class because CA1416, with TreatWarningsAsErrors, fails the build on
/// BOTH runners: it is static analysis and does not depend on the OS that compiles.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsDirectoryTrust
{
    private static readonly DirectoryFacts MissingDirectory = new(false, false, true, null, false, []);

    /// <summary>SYSTEM, the administrators, and the account running this process.</summary>
    /// <returns>The SIDs to trust as owners and inside the DACL.</returns>
    /// <remarks>
    /// In production the service runs as LocalSystem, so the third one coincides with the first
    /// and widens nothing. Launched by hand in development it is what lets it trust the
    /// directory it created itself.
    /// </remarks>
    public static IReadOnlyList<string> TrustedSids()
    {
        using WindowsIdentity current = WindowsIdentity.GetCurrent();

        return current.User is { } account
            ? [DirectoryTrust.SystemSid, DirectoryTrust.AdministratorsSid, account.Value]
            : DirectoryTrust.DefaultTrustedSids;
    }

    /// <summary>The verdict on this directory, with this process's trusted principals.</summary>
    /// <param name="path">The path to examine.</param>
    /// <returns>The verdict.</returns>
    public static DirectoryVerdict VerdictFor(string path) =>
        DirectoryTrust.Evaluate(Observe(path), TrustedSids());

    /// <summary>Observes the directory without judging it.</summary>
    /// <param name="path">The path to examine.</param>
    /// <returns>The facts, to be passed to <see cref="DirectoryTrust.Evaluate"/>.</returns>
    public static DirectoryFacts Observe(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        DirectoryInfo info = new(path);

        if (!info.Exists)
        {
            return MissingDirectory;
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // Detected BEFORE reading any ACL: .NET does not follow the reparse point to read
            // the attributes, so what is being looked at here is the link and not its target.
            // Verified with a non-existent target too: Exists stays true.
            return new DirectoryFacts(true, true, true, null, false, []);
        }

        try
        {
            DirectorySecurity security =
                info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);

            string? owner =
                (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;

            List<string> sids = security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Select(rule => ((SecurityIdentifier)rule.IdentityReference).Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new DirectoryFacts(true, false, true, owner, security.AreAccessRulesProtected, sids);
        }
        catch (UnauthorizedAccessException)
        {
            // Covers PrivilegeNotHeldException, which derives from it.
            return Unreadable();
        }
        catch (IOException)
        {
            return Unreadable();
        }
    }

    /// <summary>The security to apply: protected, SYSTEM and administrators only.</summary>
    /// <returns>The descriptor.</returns>
    public static DirectorySecurity SecurityDescriptor()
    {
        DirectorySecurity security = new();

        // Cuts inheritance. Without it, the directory inherits from C:\ProgramData the ACE that
        // grants BUILTIN\Users read access, and the secret is readable by every user on the
        // machine without there being any attacker at all.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (WellKnownSidType sidType in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sidType, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        using WindowsIdentity current = WindowsIdentity.GetCurrent();

        if (current.User is { } account && !account.IsWellKnown(WellKnownSidType.LocalSystemSid))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                account,
                FileSystemRights.FullControl,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>Brings the directory into a state where it can hold a secret.</summary>
    /// <param name="path">The path to prepare.</param>
    /// <exception cref="InvalidOperationException">If that is not possible, with the reason inside.</exception>
    /// <remarks>
    /// A junction is NOT repaired: it is a security incident and not a hiccup, and "fixing it"
    /// would mean applying the corrections to the directory of whoever planted it.
    /// <para>
    /// A DIRECTORY NOTHING VOUCHES FOR AND NOT EMPTY IS NOT REPAIRED EITHER, and for the same
    /// reason. Repairing secures the directory from now on; it says nothing about who wrote what is
    /// already inside, and once the repair is done nothing can tell the two apart — so whatever was
    /// planted there is read back as if the service had written it. See
    /// <see cref="CredentialProvisioning"/> for what that bought an attacker, and for why the refusal
    /// has to come BEFORE the repair rather than after it.
    /// </para>
    /// <para>
    /// AND WHEN IT IS EMPTY, the container is REPLACED rather than repaired — see
    /// <see cref="Replace"/>. Repairing an untrusted directory in place needed two calls in a fixed
    /// order, and the gap between them was a race that adopted whatever was created in it. Replacing
    /// leaves no such gap. Only a directory whose owner is already trusted is still repaired, with
    /// its contents left alone.
    /// </para>
    /// </remarks>
    public static void Prepare(string path) => Prepare(path, TrustedSids());

    /// <summary>The same, judged against an explicit set of trusted principals.</summary>
    /// <param name="path">The path to prepare.</param>
    /// <param name="trustedSids">The SIDs that may own the directory and appear in its DACL.</param>
    /// <exception cref="InvalidOperationException">If that is not possible, with the reason inside.</exception>
    /// <remarks>
    /// The twin of <see cref="DirectoryTrust.Evaluate(DirectoryFacts, IReadOnlyList{string})"/>'s
    /// two-argument form, and it exists for the same reason: <see cref="TrustedSids"/> always
    /// contains the account that is RUNNING, so no process can observe a directory it created
    /// itself as <see cref="DirectoryVerdict.UntrustedOwner"/> — and the refusal below could
    /// therefore not be exercised at all. Handed
    /// <see cref="DirectoryTrust.DefaultTrustedSids"/> it reproduces exactly what the service sees,
    /// which is what the tests do. It widens nothing: in production the only caller is the overload
    /// above, which passes <see cref="TrustedSids"/>.
    /// </remarks>
    public static void Prepare(string path, IReadOnlyList<string> trustedSids)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(trustedSids);

        DirectoryVerdict verdict = DirectoryTrust.Evaluate(Observe(path), trustedSids);

        if (verdict.CanHoldSecret())
        {
            return;
        }

        if (verdict == DirectoryVerdict.Missing)
        {
            Create(path);
            ConfirmSafe(path, trustedSids);
            return;
        }

        if (verdict == DirectoryVerdict.ReparsePoint)
        {
            throw new InvalidOperationException(
                $"The credential directory '{path}' is a junction or symbolic link. " +
                "Observer will not follow it: a standard user can create one without any " +
                "privilege, which would place the machine token wherever they choose. " +
                "Remove it and restart the service.");
        }

        // A directory that already holds something no trusted account can be shown to have written
        // is NOT repaired at all: see the type's remarks for what gets adopted otherwise. The rule
        // itself is MayAdoptContents and not this method's business, for the same reason the verdict
        // is decided by a pure function - it is load-bearing, and the cases that matter cannot all
        // be built on an ordinary machine.
        //
        // AFTER the reparse-point check and never before it: enumerating a junction would follow it
        // and report on the contents of whoever planted the link.
        if (!verdict.MayAdoptContents(IsEmpty(path)))
        {
            throw new InvalidOperationException(
                $"The credential directory '{path}' is not empty, and it is not owned by SYSTEM or " +
                $"the administrators ({verdict}) - where the verdict is Unknown, its owner could " +
                "not be read at all. Observer will not secure it and will not read what is in it: " +
                "a machine token or certificate found in a directory whose owner cannot be " +
                "accounted for was chosen by whoever does own it, and the token is valid FROM THE " +
                "NETWORK. Nothing has been changed here, so the files are exactly as you left " +
                "them. If you did not put them there, delete them and restart, and the service " +
                "will generate its own. If you did - a store copied in by hand or restored from a " +
                "backup is owned by the account that copied it, not by Administrators - give the " +
                "directory back to SYSTEM or Administrators AND protect its permissions so no other " +
                "account is granted - handing back the ownership alone leaves the DACL inheriting " +
                "from ProgramData, which every account on the machine can write - and the service " +
                "will adopt them.");
        }

        // Only an empty directory reaches here, and the two branches differ in ONE thing: whether
        // anything inside has to survive.
        //
        // An untrusted OWNER, or an owner that could not even be read, is not repaired IN PLACE. It
        // used to be, and that was a race: Repair has to set the owner first and the DACL second -
        // the other order achieves nothing, because an owner holds implicit WRITE_DAC - so until the
        // second call landed, whoever owned the directory could still create a file in it, and
        // ConfirmSafe would not notice, because it re-reads the owner and the DACL, which are
        // exactly the two things Repair just fixed. Replacing the container removes the window
        // instead of narrowing it.
        if (!verdict.ContentsHaveTrustedAuthor())
        {
            Replace(path);
        }
        else
        {
            // A trusted owner whose DACL merely drifted: the contents stay, so the permissions are
            // rewritten in place. This is the only verdict that still reaches Repair.
            Repair(path);
        }

        ConfirmSafe(path, trustedSids);
    }

    /// <summary>Creates the directory, already protected, in one operation.</summary>
    /// <param name="path">The directory to create.</param>
    /// <remarks>
    /// One shot, because creating and then applying would leave a window in which the directory
    /// inherits. The extension on the descriptor is the only form that does it;
    /// <c>Directory.CreateDirectory(path, mode)</c> is the Unix twin and has nothing to do with this.
    /// <para>
    /// It does NOT fail on a directory that already exists, and does not apply the descriptor
    /// either — measured: a silent no-op that leaves the DACL inheriting. That is why every caller
    /// follows it with <see cref="ConfirmSafe"/>, which is the thing that actually decides.
    /// </para>
    /// </remarks>
    private static void Create(string path)
    {
        try
        {
            SecurityDescriptor().CreateDirectory(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Observer could not create its credential directory at '{path}'. The usual cause " +
                "is that something which is NOT a directory sits at that path: a standard user can " +
                "create a file directly in C:\\ProgramData, measured, and a file there stops the " +
                "service at every start. Check what is at that path and remove it.",
                error);
        }
    }

    /// <summary>Throws away an untrusted EMPTY container and puts a protected one in its place.</summary>
    /// <param name="path">The directory to replace.</param>
    /// <remarks>
    /// Why replace instead of repair: see the call site. What makes it safe is not the delete but
    /// <see cref="ConfirmSafe"/> afterwards. Walk the interleavings. A file present before we start
    /// never gets here, because the guard refused. A file that appears between the emptiness check
    /// and the delete makes <c>RemoveDirectory</c> fail, and nothing has been touched — the delete
    /// is NON-RECURSIVE precisely so that it cannot destroy a store, and the operating system, not
    /// our discipline, is what enforces that. After the delete succeeds the name does not exist, so
    /// to plant anything the attacker must first create a container, and any container an
    /// unprivileged account can create is one it OWNS: our create is then a no-op and
    /// <see cref="ConfirmSafe"/> reads that hostile owner and refuses. A junction planted in the gap
    /// is caught by the reparse check inside the same re-reading. The gap cannot be removed —
    /// Windows has no atomic rename-over-a-directory — and it does not need to be: the OWNER is what
    /// condemns whatever appears there, and a standard user cannot forge a trusted owner (measured:
    /// SetOwner rejects every SID in their token).
    /// <para>
    /// THE READ-ONLY BIT IS CLEARED FIRST, and that line is the difference between this being a fix
    /// and being an outage. MEASURED, unelevated: an EMPTY directory carrying
    /// <see cref="FileAttributes.ReadOnly"/> makes <c>RemoveDirectory</c> fail with access denied,
    /// and it keeps failing even under a DACL that grants the caller full control — the read-only
    /// bit is a rule in the delete disposition, not an access check, so no permission fixes it. A
    /// standard user creates the directory, is its CREATOR OWNER and so holds WRITE_ATTRIBUTES, runs
    /// <c>attrib +r</c>, and walks away: no privilege, no running process, and the service would
    /// never start again. Only that one bit is cleared, leaving every other attribute — including
    /// the reparse flag, measured — untouched. The honest cost: the attacker can set the bit again
    /// in a race, so a permanent passive denial of service becomes an active one.
    /// </para>
    /// </remarks>
    private static void Replace(string path)
    {
        try
        {
            DirectoryInfo info = new(path);

            if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                info.Attributes &= ~FileAttributes.ReadOnly;
            }

            Directory.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Three causes and not two, because they cannot be told apart from here: measured,
            // "not empty" arrives as HRESULT 0x80070091 and a sharing violation as 0x80070020, but
            // access denied arrives as 0x80131620, a bare COR_E_IO with no Win32 code inside - so a
            // read-only bit reinstated in a race, a missing delete right and an explicit denial are
            // one indistinguishable bucket.
            throw new InvalidOperationException(
                $"The credential directory '{path}' is not owned by SYSTEM or the administrators, " +
                "and Observer could not replace it with one that is. NOTHING WAS DELETED: the " +
                "removal is not recursive, so it cannot remove a file. Three causes are possible " +
                "and cannot be told apart from here: a file appeared in the directory while this " +
                "was running; a process is holding the directory open, including any process whose " +
                "current directory is inside it; or the directory is marked read-only again, or " +
                "SYSTEM lacks the right to remove it. Look at the directory, then restart.",
                error);
        }

        Create(path);
    }

    /// <summary>Whether the directory holds nothing at all.</summary>
    /// <param name="path">The directory to look into.</param>
    /// <returns>True only when it is demonstrably empty.</returns>
    /// <remarks>
    /// Files AND directories, because a subdirectory in there is as unexplained as a file. A
    /// directory that cannot be enumerated counts as NOT empty: the point of the question is
    /// whether anything unproven might be adopted, and "I could not look" is not an answer that
    /// should authorize it.
    /// <para>
    /// Not <c>File.Exists</c> on the store, which is what this replaced in the first draft:
    /// <see cref="CredentialStore.Read"/> explains why that probe lies on a genuinely protected
    /// file, and a leftover <c>credentials.json.new</c> or a planted <c>certificate.pfx</c> is just
    /// as unproven as the store itself.
    /// </para>
    /// </remarks>
    private static bool IsEmpty(string path)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Looks at the directory again after touching it, and refuses if it is not safe.</summary>
    /// <param name="path">The path just created or repaired.</param>
    /// <param name="trustedSids">The same set the verdict before the repair was judged against.</param>
    /// <remarks>
    /// Closes a real race condition. Between the observation and the creation a standard user
    /// can slip in and create the directory themselves; at that point the creation with a
    /// descriptor does NOT fail, it is a silent no-op, and without this re-check we would carry
    /// on and write the token into a hostile directory, believing it had just been created.
    /// </remarks>
    private static void ConfirmSafe(string path, IReadOnlyList<string> trustedSids)
    {
        DirectoryVerdict verdict = DirectoryTrust.Evaluate(Observe(path), trustedSids);

        if (!verdict.CanHoldSecret())
        {
            throw new InvalidOperationException(
                $"The credential directory '{path}' is still not safe after being prepared " +
                $"({verdict}). Another process may have created it first. The machine token " +
                "will not be written.");
        }
    }

    /// <summary>Rewrites the permissions of a trusted-owner directory, leaving its contents.</summary>
    /// <param name="path">The directory to repair.</param>
    /// <remarks>
    /// One call now, where there used to be two. It used to take the OWNERSHIP first and then the
    /// DACL, because fixing the DACL under an untrusted owner achieves nothing — the owner holds
    /// implicit WRITE_DAC and undoes it at once — and the gap between those two calls was the race
    /// this whole file was rewritten to close. An untrusted owner no longer comes here at all; see
    /// <see cref="Replace"/>. So the ordering problem does not need mitigating, it is gone, and what
    /// is left is the one case where the owner is already trusted and only the permissions drifted.
    /// </remarks>
    private static void Repair(string path)
    {
        try
        {
            new DirectoryInfo(path).SetAccessControl(SecurityDescriptor());
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"The credential directory '{path}' can't hold a secret, and this process lacks the " +
                "rights to repair its permissions. The machine token would be readable by other " +
                $"accounts on this machine. Run the service as LocalSystem, or delete '{path}' and " +
                "let the service recreate it.",
                error);
        }
    }

    private static DirectoryFacts Unreadable() => new(true, false, false, null, false, []);
}