using System.Diagnostics;
using System.Runtime.Versioning;
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
    public void ADirectoryAUserOWNSWithAFileInItIsNothingTheSERVICEMayAdoptFrom()
    {
        // The whole attack, pinned. Measured on Windows 11 from an UNELEVATED session: a standard
        // user creates C:\ProgramData\Observer, OWNS it, and writes credentials.json into it, which
        // inherits SYSTEM: FullControl from ProgramData so LocalSystem can read it. The installer
        // never creates that directory - the service does, at its first start - and removes it on
        // uninstall, so the window reopens at every reinstall. No race: the name is public.
        //
        // The service then repaired the directory, genuinely, and read the planted file back as its
        // own machine token - which is valid FROM THE NETWORK and, through
        // AccessPolicy.MayEndProcesses, ends any process on the machine.
        //
        // Evaluated against SYSTEM and the administrators ALONE, which is the service's own trusted
        // set: a directory this process created stands in for the attacker's, because from the
        // outside the two are identical. That substitution is the same one the test above makes.
        string path = Path.Combine(Path.GetTempPath(), "obs-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);
        File.WriteAllText(
            Path.Combine(path, CredentialDirectory.FileName),
            """{"current":"planted-by-a-standard-user","previous":null,"previousExpiresAt":null}""");

        try
        {
            DirectoryVerdict asTheServiceSeesIt =
                DirectoryTrust.Evaluate(WindowsDirectoryTrust.Observe(path));

            // The OWNER decides it, and it is checked before the DACL: the planted directory needs
            // no suspicious ACE to be dangerous.
            Assert.Equal(DirectoryVerdict.UntrustedOwner, asTheServiceSeesIt);

            // Repairing it is still allowed and still right - an empty squatted directory must not
            // keep the monitor from ever starting - but nothing in it may be adopted.
            Assert.False(asTheServiceSeesIt.ContentsHaveTrustedAuthor());
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [WindowsOnly]
    public void AJUNCTIONIsDetectedBeforeAnyAclIsRead()
    {
        // A junction is created by a standard user with NO privileges: no
        // SeCreateSymbolicLinkPrivilege, no developer mode. If the service did not recognise it,
        // it would "secure" the attacker's directory and store the machine token inside it.
        string target = Path.Combine(Path.GetTempPath(), "obs-target-" + Guid.NewGuid().ToString("N")[..8]);
        string junction = Path.Combine(Path.GetTempPath(), "obs-junction-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(target);

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