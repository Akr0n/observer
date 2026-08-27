using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Observer.Service.Credentials;

/// <summary>Crea il file del deposito su Windows, gia' con la DACL giusta.</summary>
/// <remarks>
/// Classe a parte e annotata per CA1416, che con TreatWarningsAsErrors fa fallire la build su
/// entrambi i runner.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsCredentialFile
{
    /// <summary>Crea un file nuovo con una DACL protetta.</summary>
    /// <param name="percorso">Il percorso del file da creare.</param>
    /// <returns>Il flusso su cui scrivere.</returns>
    public static Stream CreaProtetto(string percorso) =>
        new FileInfo(percorso).Create(
            // CreateNew e non Create: su un file gia' esistente, Create IGNORA il descrittore
            // passato e lascia in piedi quello che c'era. La chiamata riesce senza errore, e il
            // segreto finisce dentro una DACL scelta da qualcun altro.
            FileMode.CreateNew,
            FileSystemRights.WriteData | FileSystemRights.Synchronize,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.None,
            Sicurezza());

    /// <summary>La DACL del deposito.</summary>
    /// <returns>Il descrittore.</returns>
    /// <remarks>
    /// SYSTEM e amministratori, piu' l'account che ESEGUE questo processo. In produzione il
    /// servizio gira come LocalSystem e quella terza regola coincide con la prima, quindi non
    /// concede nulla di nuovo; lanciato a mano durante lo sviluppo e' cio' che permette al
    /// servizio di rileggere il proprio deposito invece di trovarselo chiuso in faccia.
    /// </remarks>
    public static FileSecurity Sicurezza()
    {
        FileSecurity sicurezza = new();

        // Taglia l'ereditarieta': la cartella di sistema che ospita il deposito concede a
        // BUILTIN\Users la lettura ereditabile, e ereditare basta a perdere il segreto.
        sicurezza.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        sicurezza.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        sicurezza.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        using WindowsIdentity corrente = WindowsIdentity.GetCurrent();

        if (corrente.User is { } account)
        {
            sicurezza.AddAccessRule(new FileSystemAccessRule(
                account,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        return sicurezza;
    }
}