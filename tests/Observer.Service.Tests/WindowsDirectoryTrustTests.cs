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

            // The key is never echoed into a message that will end up in a log.
            Assert.DoesNotContain("planted-by-a-standard-user", refusal.Message, StringComparison.Ordinal);

            // NOTHING WAS TOUCHED, and this is the assertion that matters most. Moving the refusal
            // to after Repair would still throw on this first call and look correct - but the owner
            // would already be fixed, so the 5-second restart the package configures would see a
            // spotless directory and adopt the planted file. The owner staying put is what makes the
            // refusal durable.
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
    public void AnUntrustedButEMPTYDirectoryIsStillRepairedAndNotRefused()
    {
        // The other half of the rule: squatting a folder NAME must not keep the monitor from ever
        // starting, so an untrusted directory with nothing in it is repaired and not refused.
        //
        // What comes out of the repair afterwards is NOT asserted, because it is not the same
        // everywhere and that is a property of the machine, not of the rule: unelevated the repair
        // cannot finish (SetOwner to Administrators needs privileges only LocalSystem has), while
        // elevated it finishes and then ConfirmSafe objects, since the descriptor it writes grants
        // the current account, which OnlySystemIsTrusted does not name. What is asserted is the one
        // thing true in both: the refusal is never the GUARD'S. Deleting "&& !IsEmpty(path)" makes
        // the guard fire here, and this is the only witness that clause has.
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
        string target = Path.Combine(Path.GetTempPath(), "obs-target-" + Guid.NewGuid().ToString("N")[..8]);
        string junction = Path.Combine(Path.GetTempPath(), "obs-junction-" + Guid.NewGuid().ToString("N")[..8]);

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

            Assert.Contains("junction", error.Message, StringComparison.OrdinalIgnoreCase);
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