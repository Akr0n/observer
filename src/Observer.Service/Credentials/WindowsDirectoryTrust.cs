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
    /// </remarks>
    public static void Prepare(string path)
    {
        DirectoryVerdict verdict = VerdictFor(path);

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
            ConfirmSafe(path);
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

        Repair(path, verdict);
        ConfirmSafe(path);
    }

    /// <summary>Looks at the directory again after touching it, and refuses if it is not safe.</summary>
    /// <param name="path">The path just created or repaired.</param>
    /// <remarks>
    /// Closes a real race condition. Between the observation and the creation a standard user
    /// can slip in and create the directory themselves; at that point the creation with a
    /// descriptor does NOT fail, it is a silent no-op, and without this re-check we would carry
    /// on and deposit the token in a hostile directory, believing it had just been created.
    /// </remarks>
    private static void ConfirmSafe(string path)
    {
        DirectoryVerdict verdict = VerdictFor(path);

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