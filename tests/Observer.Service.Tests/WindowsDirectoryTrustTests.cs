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
[Collection(AmbienteDelProcesso.Nome)]
[SupportedOSPlatform("windows")]
public class WindowsDirectoryTrustTests
{
    [SoloSuWindows]
    public void UnaCartellaAssenteVieneVistaComeAssente()
    {
        string percorso = Path.Combine(Path.GetTempPath(), "obs-" + Guid.NewGuid().ToString("N")[..10]);

        Assert.Equal(DirectoryVerdict.Assente, WindowsDirectoryTrust.Verdetto(percorso));
    }

    [SoloSuWindows]
    public void UnaCartellaCreataDaUnUtenteNonEFidataPerIlSERVIZIO_maLoEPerChiLaCrea()
    {
        // E' il caso dello sviluppatore, ed e' anche il caso dell'attaccante che prepara la
        // cartella prima che il servizio parta: dall'esterno sono identici, ed e' giusto che
        // entrambi vengano rifiutati.
        string percorso = Path.Combine(Path.GetTempPath(), "obs-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(percorso);

        try
        {
            // Contro i soli SYSTEM e amministratori NON e' fidata: e' il caso
            // dell'attaccante che prepara la cartella prima che il servizio parta.
            Assert.False(DirectoryTrust.Valuta(WindowsDirectoryTrust.Osserva(percorso)).PuoOspitareUnSegreto());

            // Ma il processo che l'ha creata puo' fidarsene, ed e' il caso dello
            // sviluppatore che lancia il servizio a mano.
            WindowsDirectoryTrust.Prepara(percorso);
            Assert.True(WindowsDirectoryTrust.Verdetto(percorso).PuoOspitareUnSegreto());
        }
        finally
        {
            Directory.Delete(percorso, recursive: true);
        }
    }

    [SoloSuWindows]
    public void UnaGIUNZIONEVieneRiconosciutaPrimaDiGuardareLeAcl()
    {
        // Una giunzione la crea un utente standard SENZA privilegi: niente
        // SeCreateSymbolicLinkPrivilege, niente modalita' sviluppatore. Se il servizio non la
        // riconoscesse, "metterebbe in sicurezza" la cartella dell'attaccante e ci
        // depositerebbe dentro il token di macchina.
        string bersaglio = Path.Combine(Path.GetTempPath(), "obs-bersaglio-" + Guid.NewGuid().ToString("N")[..8]);
        string giunzione = Path.Combine(Path.GetTempPath(), "obs-giunzione-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(bersaglio);

        using Process? mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{giunzione}\" \"{bersaglio}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        Assert.NotNull(mklink);
        mklink.WaitForExit(10_000);

        try
        {
            Assert.Equal(0, mklink.ExitCode);

            DirectoryFacts fatti = WindowsDirectoryTrust.Osserva(giunzione);

            Assert.True(fatti.PuntoDiReparse);
            Assert.Equal(DirectoryVerdict.PuntoDiReparse, DirectoryTrust.Valuta(fatti));

            // E il servizio si rifiuta, invece di "ripararla".
            InvalidOperationException errore =
                Assert.Throws<InvalidOperationException>(() => WindowsDirectoryTrust.Prepara(giunzione));

            Assert.Contains("junction", errore.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // Directory.Delete su una giunzione rimuove il collegamento, non il bersaglio.
            if (Directory.Exists(giunzione))
            {
                Directory.Delete(giunzione);
            }

            Directory.Delete(bersaglio, recursive: true);
        }
    }

    [SoloSuWindows]
    public void LaSicurezzaPropostaNonNominaNessunoOltreSystemEAmministratori()
    {
        string sddl = WindowsDirectoryTrust.Sicurezza()
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