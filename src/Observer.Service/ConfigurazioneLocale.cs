namespace Observer.Service;

/// <summary>Il file di configurazione locale, che puo' esserci vuoto.</summary>
/// <remarks>
/// <c>AddJsonFile(optional: true)</c> tollera un file ASSENTE, non un file VUOTO: zero byte
/// fanno fallire l'avvio con <c>The input does not contain any JSON tokens</c> e uno stack
/// trace. E svuotare il file e' esattamente cio' che si fa quando si vuole togliere il token
/// che contiene, adesso che il servizio se lo genera da solo.
/// </remarks>
public static class ConfigurazioneLocale
{
    /// <summary>Il nome del file, uguale su ogni sistema.</summary>
    public const string NomeFile = "appsettings.Local.json";

    /// <summary>Se il file ha qualcosa da leggere.</summary>
    /// <param name="percorso">Il percorso completo del file.</param>
    /// <returns>Falso se e' assente o non contiene altro che spazi.</returns>
    /// <remarks>
    /// Un file con dentro un JSON SBAGLIATO va caricato lo stesso, e deve fallire: quello e' un
    /// errore vero, e nasconderlo lascerebbe l'utente a chiedersi perche' il suo token non viene
    /// letto. La tolleranza vale solo per "non c'e' niente da leggere", che e' indistinguibile
    /// dall'assenza del file.
    /// </remarks>
    public static bool VaCaricato(string percorso)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(percorso);

        try
        {
            return !string.IsNullOrWhiteSpace(File.ReadAllText(percorso));
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            // Illeggibile per un altro motivo: lo si lascia caricare, cosi' il guasto vero
            // emerge dal caricatore di configurazione invece di essere ingoiato qui.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}