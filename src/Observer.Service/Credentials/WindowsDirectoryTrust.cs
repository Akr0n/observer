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
    /// A DIRECTORY OWNED BY AN UNTRUSTED ACCOUNT AND NOT EMPTY IS NOT REPAIRED EITHER, and for the
    /// same reason. Repairing secures the directory from now on; it says nothing about who wrote
    /// what is already inside, and once the repair is done nothing can tell the two apart — so
    /// whatever was planted there is read back as if the service had written it. See
    /// <see cref="CredentialProvisioning"/> for what that bought an attacker, and for why the
    /// refusal has to come BEFORE the repair rather than after it.
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
            // Created ALREADY protected, in one shot: creating and then applying would leave a
            // window in which the directory inherits. The extension on the descriptor is the
            // only form that does it; Directory.CreateDirectory(path, mode) is the Unix twin
            // and has nothing to do with this.
            SecurityDescriptor().CreateDirectory(path);
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
                "directory back to SYSTEM or Administrators, granting no other account, and the " +
                "service will adopt them.");
        }

        // KNOWN GAP, and it is the reason MayAdoptContents' allowance for an empty directory is a
        // trade and not a proof. Getting here with an untrusted owner means the directory was empty
        // a moment ago, so it is being repaired instead of refused. But Repair has to set the OWNER
        // first and the DACL second - its own comment says why the other order achieves nothing -
        // and until that second call lands, whoever owned it still has write access. A file created
        // in that window is adopted, because ConfirmSafe re-reads the owner and the DACL, which are
        // exactly the two things Repair just fixed, and never asks again what is in there.
        //
        // Not closable by re-checking after the repair: at that point the directory really is safe,
        // so a refusal would be cured by the 5-second restart the package configures, which is the
        // same trap the whole ordering above exists to avoid. Nor by refusing on an empty directory
        // too, which is the denial of service. It needs Repair to stop repairing IN PLACE: move the
        // hostile directory aside and create a fresh one already protected, in one shot, so no
        // window exists and a failure to move is itself a durable refusal. That is a change to
        // Repair, with its own measurements (open handles, delete rights on ProgramData), and it is
        // not this one. Patience is no longer enough for an attacker; a race still is.
        Repair(path, verdict);
        ConfirmSafe(path, trustedSids);
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

    private static void Repair(string path, DirectoryVerdict verdict)
    {
        DirectoryInfo info = new(path);

        try
        {
            if (verdict == DirectoryVerdict.UntrustedOwner)
            {
                // OWNERSHIP first. Fixing the DACL while leaving the owner as it is
                // achieves nothing: it has implicit WRITE_DAC and undoes it right away.
                DirectorySecurity ownership = new();
                ownership.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                info.SetAccessControl(ownership);
            }

            info.SetAccessControl(SecurityDescriptor());
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"The credential directory '{path}' can't hold a secret ({verdict}), and " +
                "this process lacks the rights to repair it. The machine token would be " +
                "readable by other accounts on this machine. Run the service as LocalSystem, " +
                $"or delete '{path}' and let the service recreate it.",
                error);
        }
    }

    private static DirectoryFacts Unreadable() => new(true, false, false, null, false, []);
}