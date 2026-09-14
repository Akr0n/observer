using System.Diagnostics;
using System.Runtime.Versioning;
using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// L'adattatore che raccoglie da Windows i fatti su una cartella.
/// </summary>
/// <remarks>
/// Qui si prova solo cio' che una sessione NON amministrativa puo' davvero costruire: una
/// giunzione e una cartella posseduta dall'utente corrente. Il caso "sicura" richiede un
/// proprietario SYSTEM o Administrators e non e' costruibile senza elevazione — e' coperto
/// dalla tabella di <see cref="DirectoryTrustTests"/>, che lavora sui fatti.
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
        // E' il caso dello sviluppatore, ed e' anche il caso dell'attaccante che prepara la
        // cartella prima che il servizio parta: dall'esterno sono identici, ed e' giusto che
        // entrambi vengano rifiutati.
        string path = Path.Combine(Path.GetTempPath(), "obs-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);

        try
        {
            // Contro i soli SYSTEM e amministratori NON e' fidata: e' il caso
            // dell'attaccante che prepara la cartella prima che il servizio parta.
            Assert.False(DirectoryTrust.Evaluate(WindowsDirectoryTrust.Observe(path)).CanHoldSecret());

            // Ma il processo che l'ha creata puo' fidarsene, ed e' il caso dello
            // sviluppatore che lancia il servizio a mano.
            WindowsDirectoryTrust.Prepare(path);
            Assert.True(WindowsDirectoryTrust.VerdictFor(path).CanHoldSecret());
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [WindowsOnly]
    public void AJUNCTIONIsDetectedBeforeAnyAclIsRead()
    {
        // Una giunzione la crea un utente standard SENZA privilegi: niente
        // SeCreateSymbolicLinkPrivilege, niente modalita' sviluppatore. Se il servizio non la
        // riconoscesse, "metterebbe in sicurezza" la cartella dell'attaccante e ci
        // depositerebbe dentro il token di macchina.
        string target = Path.Combine(Path.GetTempPath(), "obs-bersaglio-" + Guid.NewGuid().ToString("N")[..8]);
        string junction = Path.Combine(Path.GetTempPath(), "obs-giunzione-" + Guid.NewGuid().ToString("N")[..8]);

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

            // E il servizio si rifiuta, invece di "ripararla".
            InvalidOperationException error =
                Assert.Throws<InvalidOperationException>(() => WindowsDirectoryTrust.Prepare(junction));

            Assert.Contains("junction", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // Directory.Delete su una giunzione rimuove il collegamento, non il bersaglio.
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

        // "P" = protetta, cioe' non eredita. Senza, erediterebbe da ProgramData l'ACE che
        // concede lettura a BUILTIN\Users.
        Assert.Contains("D:P", sddl, StringComparison.Ordinal);
        Assert.Contains(";;;SY)", sddl, StringComparison.Ordinal);
        Assert.Contains(";;;BA)", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;BU)", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;WD)", sddl, StringComparison.Ordinal);
    }
}