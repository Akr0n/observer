using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Observer.Core.Security;
using Observer.Service.Credentials;

namespace Observer.Cli;

/// <summary>Le risposte che il verbo <c>doctor</c> mette in fila.</summary>
public static class Diagnosi
{
    /// <summary>Quanto e' protetto il deposito, in una frase.</summary>
    /// <param name="percorsoDelFile">Il percorso del deposito.</param>
    /// <returns>Il verdetto in inglese, con il motivo dentro.</returns>
    public static string Protezione(string percorsoDelFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(percorsoDelFile);

        if (Path.GetDirectoryName(percorsoDelFile) is not { Length: > 0 } cartella)
        {
            return "UNKNOWN - the store path has no directory.";
        }

        if (!OperatingSystem.IsWindows())
        {
            return Directory.Exists(cartella)
                ? "the directory exists; on Linux the mode is enforced at 0700 by the service."
                : "ABSENT - the service has not created it yet.";
        }

        DirectoryVerdict verdetto = WindowsDirectoryTrust.Verdetto(cartella);

        if (verdetto == DirectoryVerdict.Sconosciuto && Directory.Exists(cartella))
        {
            // E' cio' che un deposito PROTETTO BENE mostra a un account qualsiasi, ed e' il
            // caso piu' comune di tutti: leggere i permessi di una cartella richiede un
            // permesso su quella cartella, e un deposito fatto come si deve non ne concede.
            return
                "UNREADABLE FROM HERE - the directory exists but this account can't read its " +
                "permissions. That is what a correctly protected store looks like from an " +
                "ordinary account, and it rules out the common failure: a directory that merely " +
                "inherits its parent's permissions is readable by every user on the machine. " +
                "It does NOT prove the owner is right. Run this from an elevated terminal for a " +
                "definitive verdict.";
        }

        return Frase(verdetto);
    }

    /// <summary>L'impronta del certificato di macchina, in una frase.</summary>
    /// <param name="percorsoDelFile">Il percorso del deposito del token.</param>
    /// <returns>L'impronta leggibile, oppure il motivo per cui non si vede.</returns>
    /// <remarks>
    /// Distingue i tre casi come fa <see cref="Protezione"/>, e per lo stesso motivo:
    /// "non riesco a leggerlo da qui" e "non esiste" portano a due gesti diversi, e
    /// confonderli manda chi legge a cercare un guasto che non c'e'.
    /// </remarks>
    public static string Certificato(string percorsoDelFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(percorsoDelFile);

        string percorso = MachineCertificate.PercorsoAccantoA(percorsoDelFile);

        try
        {
            using X509Certificate2 certificato =
                MachineCertificate.Carica(File.ReadAllBytes(percorso));

            return CertificateFingerprint.PerLUomo(MachineCertificate.Impronta(certificato));
        }
        catch (Exception errore) when (errore is FileNotFoundException or DirectoryNotFoundException)
        {
            // "Non l'ho trovato" non e' "non c'e'". Su una cartella che questo account non
            // riesce nemmeno a elencare - cioe' su un deposito protetto come si deve - le due
            // cose sono indistinguibili dall'esterno, e spacciare la prima per la seconda
            // manderebbe chi legge a cercare un guasto che non esiste.
            return Elencabile(percorso)
                ? "not created yet - the service writes it the first time it starts."
                : "NOT VISIBLE FROM HERE - this account can't list the directory, so this is " +
                  "not proof that the certificate is missing. Run this from an elevated terminal.";
        }
        catch (UnauthorizedAccessException)
        {
            return "UNREADABLE FROM HERE - run this from an elevated terminal.";
        }
        catch (CryptographicException errore)
        {
            return "DAMAGED - " + errore.Message;
        }
    }

    /// <summary>Se questo account riesce anche solo a elencare la cartella del deposito.</summary>
    private static bool Elencabile(string percorsoDelCertificato)
    {
        try
        {
            string cartella = Path.GetDirectoryName(percorsoDelCertificato) ?? ".";

            _ = Directory.EnumerateFileSystemEntries(cartella).GetEnumerator().MoveNext();

            return true;
        }
        catch (Exception errore) when (errore is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Chi sta eseguendo questo comando.</summary>
    /// <returns>Il nome dell'account.</returns>
    public static string ChiSono() =>
        OperatingSystem.IsWindows() ? NomeWindows() : Environment.UserName;

    /// <summary>Se il comando gira con privilegi amministrativi.</summary>
    /// <returns>Vero, falso, oppure sconosciuto fuori da Windows.</returns>
    public static string Elevato() =>
        OperatingSystem.IsWindows()
            ? ElevatoSuWindows().ToString()
            : (Environment.UserName == "root").ToString();

    /// <summary>La frase che spiega un verdetto a chi legge lo schermo.</summary>
    /// <param name="verdetto">L'esito della valutazione della cartella.</param>
    /// <returns>La frase, in inglese.</returns>
    public static string Frase(DirectoryVerdict verdetto) => verdetto switch
    {
        DirectoryVerdict.Sicura =>
            "PROTECTED - owned by SYSTEM or Administrators, and nobody else is granted access.",
        DirectoryVerdict.Assente =>
            "ABSENT - the service has not created it yet. Start it once.",
        DirectoryVerdict.DaclAperta =>
            "NOT PROTECTED - other accounts on this machine can read it. Anyone who reads it " +
            "gets permanent access to this machine FROM THE NETWORK.",
        DirectoryVerdict.ProprietarioNonFidato =>
            "FAKE PROTECTED - the permissions name only SYSTEM and Administrators, but the " +
            "OWNER is an ordinary account, and an owner can grant itself access again whenever " +
            "it likes. This looks safe and is not.",
        DirectoryVerdict.PuntoDiReparse =>
            "HIJACKED - the path is a junction or symbolic link, so the token would be written " +
            "wherever it points. A standard user can create one without any privilege. Remove it.",
        _ => "UNKNOWN - the directory can't be examined from here. Try an elevated terminal.",
    };

    [SupportedOSPlatform("windows")]
    private static string NomeWindows()
    {
        using WindowsIdentity identita = WindowsIdentity.GetCurrent();

        return identita.Name;
    }

    [SupportedOSPlatform("windows")]
    private static bool ElevatoSuWindows()
    {
        using WindowsIdentity identita = WindowsIdentity.GetCurrent();

        return new WindowsPrincipal(identita).IsInRole(WindowsBuiltInRole.Administrator);
    }
}