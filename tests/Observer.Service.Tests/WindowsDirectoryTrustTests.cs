using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// The adapter that collects the facts about a directory from Windows.
/// </summary>
/// <remarks>
/// Only what a NON-administrative session can really build is tested here: a junction and a
/// directory owned by the current user. The "safe" case needs an owner of SYSTEM or
/// Administrators and cannot be built without elevation — it is covered by the table in
/// <see cref="DirectoryTrustTests"/>, which works on the facts.
/// </remarks>
[Collection(ProcessEnvironment.Name)]
[SupportedOSPlatform("windows")]
public class WindowsDirectoryTrustTests
{
    [WindowsOnly]
    public void AMissingDirectoryIsReportedAsMissing()
    {
        string path = Path.Combine(Path.GetTempPath(), "obs-" + Guid.NewGuid().ToString("N")[..10]);

        Assert.Equal(DirectoryVerdict.Missing, WindowsDirectoryTrust.VerdictFor(path));
    }

    [WindowsOnly]
    public void ADirectoryCreatedByAUserIsNotTrustedForTheSERVICEButIsForItsCreator()
    {
        // This is the developer's case, and it is also the case of the attacker who prepares the
        // directory before the service starts: from the outside they are identical, and it is
        // right that both are refused.
        string path = Path.Combine(Path.GetTempPath(), "obs-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);

        try
        {
            // Against SYSTEM and administrators alone it is NOT trusted: it is the
            // case of the attacker who prepares the directory before the service starts.
            Assert.False(DirectoryTrust.Evaluate(WindowsDirectoryTrust.Observe(path)).CanHoldSecret());

            // But the process that created it can trust it, and that is the case of
            // the developer who starts the service by hand.
            WindowsDirectoryTrust.Prepare(path);
            Assert.True(WindowsDirectoryTrust.VerdictFor(path).CanHoldSecret());
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [WindowsOnly]
    public void APLANTEDStoreIsRefusedAndTheDirectoryIsLeftEXACTLYAsItWas()
    {
        // THE ATTACK, exercised through Prepare itself. Measured on Windows 11 from an UNELEVATED
        // session: a standard user creates C:\ProgramData\Observer, OWNS it, and writes
        // credentials.json into it, which inherits SYSTEM: FullControl from ProgramData so
        // LocalSystem can read it. The installer never creates that directory - the service does, at
        // its first start - and removes it on uninstall, so the window reopens at every reinstall.
        // No race is needed: the name is public and can be planted months ahead. The service then
        // repaired the directory, genuinely, and read the planted file back as its own machine token
        // - valid FROM THE NETWORK and, through AccessPolicy.MayEndProcesses, able to end any
        // process on the machine.
        //
        // A directory this process created stands in for the attacker's: from the outside the two
        // are identical, and an unelevated session cannot build a genuinely foreign-owned directory
        // (SetOwner rejects every SID in its token, BUILTIN\Users included - measured). Which is
        // why the trusted set is passed in: see OnlySystemIsTrusted.
        string path = Path.Combine(Path.GetTempPath(), "obs-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);
        string store = Path.Combine(path, CredentialDirectory.FileName);
        File.WriteAllText(
            store,
            """{"current":"planted-by-a-standard-user","previous":null,"previousExpiresAt":null}""");

        try
        {
            string ownerBefore = OwnerOf(path);

            InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
                () => WindowsDirectoryTrust.Prepare(path, OnlySystemIsTrusted));

            Assert.Contains("is not empty", refusal.Message, StringComparison.Ordinal);

            // A folder the service cannot LIST counts as not empty - an empty one whose owner took SYSTEM
            // out of its permissions is refused with this very message (measured, verdict Unknown) - and
            // the message has to say so, or it tells an operator who finds nothing inside that it is
            // wrong. Every refusal carries the sentence; only that case needs it.
            Assert.Contains(
                "A folder that cannot be listed counts as not empty, even if nothing is in it.",
                refusal.Message,
                StringComparison.Ordinal);

            // The recovery commands are IN the message, character for character, because the MSI and
            // the .deb ship no README and this is the only text the operator has in front of them. A
            // typo in one of them is not cosmetic: "takeout" got in once, in a draft, and would have
            // sent an administrator to a command that does not exist at the moment they need it.
            Assert.Contains($"takeown /F \"{path}\" /A /R", refusal.Message, StringComparison.Ordinal);

            // ... and the takeown line has NO /D. That option takes the localized letter for "yes"
            // (S on Italian Windows), and the Y this message used to print is rejected with a syntax
            // error that does nothing - so the one line meant for the case where Windows refuses the
            // delete was itself refused on every non-English machine. Measured. Without /D, takeown asks
            // its yes/no question in the operator's own language, which the message says.
            Assert.DoesNotContain("/D Y", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("in the language of Windows: answer yes", refusal.Message, StringComparison.Ordinal);
            Assert.Contains($"icacls \"{path}\" /setowner \"*S-1-5-32-544\" /T", refusal.Message, StringComparison.Ordinal);
            Assert.Contains($"icacls \"{path}\" /reset /T", refusal.Message, StringComparison.Ordinal);
            Assert.Contains(
                $"icacls \"{path}\" /inheritance:r /grant:r \"*S-1-5-18:(OI)(CI)F\" \"*S-1-5-32-544:(OI)(CI)F\"",
                refusal.Message,
                StringComparison.Ordinal);

            // The key is never echoed into a message that will end up in a log.
            Assert.DoesNotContain("planted-by-a-standard-user", refusal.Message, StringComparison.Ordinal);

            // NOTHING WAS TOUCHED, and this is the assertion that matters most. Moving the refusal
            // to after Repair would still throw on this first call and look correct - but the owner
            // would already be fixed, so the next start - an operator's own, or the one at boot (reasoned,
            // not tried; Windows was seen NOT to retry a refused start by itself) - would see a spotless
            // directory and adopt the planted file. The owner staying put is what makes the refusal durable.
            Assert.Equal(ownerBefore, OwnerOf(path));
            Assert.Contains("planted-by-a-standard-user", File.ReadAllText(store), StringComparison.Ordinal);
            Assert.Equal(
                DirectoryVerdict.UntrustedOwner,
                DirectoryTrust.Evaluate(WindowsDirectoryTrust.Observe(path), OnlySystemIsTrusted));

            // And so the next start refuses in exactly the same way instead of curing itself.
            Assert.Contains(
                "is not empty",
                Assert.Throws<InvalidOperationException>(
                    () => WindowsDirectoryTrust.Prepare(path, OnlySystemIsTrusted)).Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [WindowsOnly]
    public void AnUntrustedButEMPTYDirectoryIsNotRefusedByTheGuard()
    {
        // The other half of the rule: squatting a folder NAME must not keep the monitor from ever
        // starting, so an untrusted directory with nothing in it is dealt with rather than refused.
        // It is REPLACED, not repaired - the name of this test used to say repaired, which stopped
        // being true when the container started being thrown away instead of fixed in place.
        //
        // What comes out afterwards is NOT asserted, because it is not the same everywhere and that
        // is a property of the machine, not of the rule: ConfirmSafe objects either way, since the
        // recreated directory is owned by its creator and OnlySystemIsTrusted does not name that
        // account. What is asserted is the one thing true in both: the refusal is never the GUARD'S.
        // Deleting "&& !IsEmpty(path)" makes the guard fire here, and this is the only witness that
        // clause has.
        string path = Path.Combine(Path.GetTempPath(), "obs-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);

        try
        {
            Exception? refusal = Record.Exception(
                () => WindowsDirectoryTrust.Prepare(path, OnlySystemIsTrusted));

            Assert.DoesNotContain(
                "is not empty",
                refusal?.Message ?? string.Empty,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [WindowsOnly]
    public void AnUntrustedEmptyDirectoryIsREPLACEDAndNotRepairedInPlace()
    {
        // The race this closes: repairing an untrusted directory in place took TWO calls in a fixed
        // order - owner first, DACL second, because the other order achieves nothing under an owner
        // with implicit WRITE_DAC - and until the second landed, the old owner could still create a
        // file that ConfirmSafe would not notice, since it re-reads only the owner and the DACL.
        //
        // The witness is an ALTERNATE DATA STREAM on the directory, and it is chosen because it is
        // the one probe that does not depend on the session. Measured: it enumerates as ZERO entries,
        // so IsEmpty stays true and the guard still lets this through; it SURVIVES the old
        // SetOwner + SetAccessControl repair; and it is destroyed when the directory is removed. So
        // the stream being gone means the container was replaced, and the stream still being there
        // means it was repaired in place. Asserting on the DACL instead would be a false green on an
        // elevated runner, where the directory is already owned by BUILTIN\Administrators and the old
        // repair therefore succeeded and left it protected anyway.
        string path = Path.Combine(Path.GetTempPath(), "obs-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);
        string witness = path + ":observer-witness";
        File.WriteAllText(witness, "written before Prepare ran");

        Assert.True(File.Exists(witness), "this volume does not support alternate data streams");
        Assert.Empty(Directory.EnumerateFileSystemEntries(path));

        try
        {
            // ConfirmSafe refuses afterwards in both environments, because the recreated directory
            // belongs to its creator and OnlySystemIsTrusted does not name that account. That is not
            // what is under test.
            Record.Exception(() => WindowsDirectoryTrust.Prepare(path, OnlySystemIsTrusted));

            Assert.False(File.Exists(witness), "the directory was repaired in place, not replaced");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [WindowsOnly]
    public void AREADONLYEmptyDirectoryIsStillReplacedInsteadOfStoppingTheService()
    {
        // The line that decides whether replacing is a fix or an outage. MEASURED, unelevated: an
        // EMPTY directory carrying the ReadOnly attribute makes RemoveDirectory fail with access
        // denied, and it keeps failing under a DACL that grants the caller full control, because the
        // read-only bit is a rule in the delete disposition and not an access check. A standard user
        // creates the directory, is its CREATOR OWNER and so holds WRITE_ATTRIBUTES, runs
        // "attrib +r" and walks away: no privilege, no running process, and without clearing that
        // bit the service would never start again.
        string path = Path.Combine(Path.GetTempPath(), "obs-" + Guid.NewGuid().ToString("N")[..10]);
        DirectoryInfo info = Directory.CreateDirectory(path);
        string witness = path + ":observer-witness";
        File.WriteAllText(witness, "written before Prepare ran");
        info.Attributes |= FileAttributes.ReadOnly;

        try
        {
            Exception? refusal = Record.Exception(
                () => WindowsDirectoryTrust.Prepare(path, OnlySystemIsTrusted));

            // Replaced, not refused for being read-only: the witness is gone, and whatever ConfirmSafe
            // then said, it did not say the directory could not be removed.
            Assert.False(File.Exists(witness), "the read-only bit stopped the replacement");
            Assert.DoesNotContain(
                "could not replace it",
                refusal?.Message ?? string.Empty,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                new DirectoryInfo(path).Attributes &= ~FileAttributes.ReadOnly;
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [WindowsOnly]
    public void ANonRecursiveDeleteIsWhatPROVESTheDirectoryWasEmpty()
    {
        // The platform fact the whole replacement rests on, pinned here so nobody has to re-measure
        // it. What this does NOT pin is the call site: adding "recursive: true" in Replace leaves
        // every test in this suite green, because the guard refuses a non-empty directory before
        // Replace is ever reached, so the flag only matters inside the race window and no test can
        // stand in that window. The flag is protected by the comment on Replace, not by a test.
        //
        // The HResult and NOT the message: the message is localised, and on this machine it reads
        // "La directory non e' vuota".
        string path = Path.Combine(Path.GetTempPath(), "obs-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);
        string store = Path.Combine(path, CredentialDirectory.FileName);
        File.WriteAllText(store, """{"current":"planted"}""");

        try
        {
            IOException error = Assert.Throws<IOException>(() => Directory.Delete(path));

            Assert.Equal(unchecked((int)0x80070091), error.HResult);   // ERROR_DIR_NOT_EMPTY
            Assert.True(File.Exists(store), "a failed non-recursive delete must leave everything");
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>SYSTEM alone, so that a directory these tests create is never owned by a
    /// trusted account.</summary>
    /// <remarks>
    /// NOT <see cref="DirectoryTrust.DefaultTrustedSids"/>, and the difference is the whole reason
    /// these tests are deterministic. Who owns a newly created directory depends on the session:
    /// unelevated it is the user, but on an ELEVATED one — which a CI runner is — Windows hands it
    /// to <c>BUILTIN\Administrators</c>, which the default set trusts. Measured the hard way: with
    /// the default set these two tests passed here and failed on windows-latest, where the verdict
    /// was <see cref="DirectoryVerdict.OpenDacl"/> and the refusal came from <c>ConfirmSafe</c>
    /// instead of the guard. Nobody creating a directory is ever SYSTEM, so this set is the same
    /// answer on both.
    /// </remarks>
    private static readonly IReadOnlyList<string> OnlySystemIsTrusted = [DirectoryTrust.SystemSid];

    private static string OwnerOf(string path) =>
        ((SecurityIdentifier)new DirectoryInfo(path)
            .GetAccessControl(AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier))!).Value;

    [WindowsOnly]
    public void AJUNCTIONIsDetectedBeforeAnyAclIsRead()
    {
        // A junction is created by a standard user with NO privileges: no
        // SeCreateSymbolicLinkPrivilege, no developer mode. If the service did not recognise it,
        // it would "secure" the attacker's directory and store the machine token inside it.
        // NEITHER NAME CONTAINS "junction", and that is not tidiness. Every refusal in this file
        // interpolates the path, so while the link was called "obs-junction-..." the assertion below
        // was satisfied by ANY refusal raised for it - including the wrong one - and the misordering
        // this test exists to catch went undetected.
        string target = Path.Combine(Path.GetTempPath(), "obs-tgt-" + Guid.NewGuid().ToString("N")[..8]);
        string junction = Path.Combine(Path.GetTempPath(), "obs-lnk-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(target);

        // The target is NOT left empty, and that is what makes this test able to fail. The
        // emptiness check that refuses a planted store must stay BELOW the reparse-point check,
        // because enumerating a junction follows it: with an empty target, moving it above would
        // still let the "junction" message through and this test would pass on a broken order. With
        // a file in there, the wrong order refuses with the wrong message instead.
        File.WriteAllText(Path.Combine(target, CredentialDirectory.FileName), "{}");

        using Process? mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{junction}\" \"{target}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        Assert.NotNull(mklink);
        mklink.WaitForExit(10_000);

        try
        {
            Assert.Equal(0, mklink.ExitCode);

            DirectoryFacts facts = WindowsDirectoryTrust.Observe(junction);

            Assert.True(facts.IsReparsePoint);
            Assert.Equal(DirectoryVerdict.ReparsePoint, DirectoryTrust.Evaluate(facts));

            // And the service refuses, instead of "repairing" it.
            InvalidOperationException error =
                Assert.Throws<InvalidOperationException>(() => WindowsDirectoryTrust.Prepare(junction));

            // The whole phrase, which only the reparse-point refusal contains. "junction" alone also
            // appears in any other refusal, because they all name the path.
            Assert.Contains("junction or symbolic link", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            // Directory.Delete on a junction removes the link, not the target.
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction);
            }

            Directory.Delete(target, recursive: true);
        }
    }

    [WindowsOnly]
    public void TheProposedSecurityNamesNobodyBesidesSystemAndAdministrators()
    {
        string sddl = WindowsDirectoryTrust.SecurityDescriptor()
            .GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);

        // "P" = protected, that is, it does not inherit. Without it, it would inherit from
        // ProgramData the ACE that grants read access to BUILTIN\Users.
        Assert.Contains("D:P", sddl, StringComparison.Ordinal);
        Assert.Contains(";;;SY)", sddl, StringComparison.Ordinal);
        Assert.Contains(";;;BA)", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;BU)", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;WD)", sddl, StringComparison.Ordinal);
    }
}