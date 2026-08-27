using System.Text.Json;

namespace Observer.Service.Credentials;

/// <summary>
/// Legge e scrive il deposito del token di macchina.
/// </summary>
/// <remarks>
/// La ricetta di scrittura non e' quella ovvia, e ognuno dei suoi passi corregge un guasto
/// misurato:
/// <list type="bullet">
/// <item>il temporaneo sta nella STESSA cartella, altrimenti la sostituzione non e' atomica;</item>
/// <item>viene creato GIA' protetto, perche' su Windows la sostituzione fa vincere la DACL del
/// TEMPORANEO: un temporaneo con permessi ereditati declassa il deposito a leggibile da
/// chiunque, in silenzio;</item>
/// <item>viene creato con CreateNew e mai con Create, perche' Create su un file esistente
/// IGNORA il descrittore passato e lascia in piedi quello che c'era;</item>
/// <item>viene cancellato in un finally, perche' una sostituzione fallita lo lascerebbe sul
/// disco col segreto in chiaro.</item>
/// </list>
/// </remarks>
public static class CredentialStore
{
    private static readonly JsonSerializerOptions Formato = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>Legge il deposito.</summary>
    /// <param name="percorso">Il percorso del file.</param>
    /// <returns>Le credenziali, oppure null se il deposito non esiste ancora.</returns>
    /// <exception cref="InvalidOperationException">Se esiste ma non e' utilizzabile.</exception>
    /// <remarks>
    /// Non usa File.Exists: su un file protetto davvero, File.Exists restituisce false anche
    /// quando il file c'e'. Ramificare su quello farebbe rigenerare la chiave a ogni avvio,
    /// tagliando fuori tutti i client remoti senza che nessuno capisca perche'.
    /// </remarks>
    public static MachineCredentials? Leggi(string percorso)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(percorso);

        string contenuto;

        try
        {
            contenuto = File.ReadAllText(percorso);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException errore)
        {
            // "Non riesco a leggerlo" NON e' "non c'e'": confonderli rigenererebbe la chiave.
            throw new InvalidOperationException(
                $"The credential store '{percorso}' exists but can't be read by this process. " +
                "The machine token will not be regenerated, because that would lock out every " +
                "remote client. Run the service as LocalSystem, or repair the file permissions.",
                errore);
        }

        try
        {
            return JsonSerializer.Deserialize<MachineCredentials>(contenuto, Formato)
                ?? throw new InvalidOperationException(
                    $"The credential store '{percorso}' is empty.");
        }
        catch (JsonException errore)
        {
            throw new InvalidOperationException(
                $"The credential store '{percorso}' isn't valid JSON ({errore.Message}). " +
                "Observer will not overwrite it: if the file was hand-edited, fix it; if it is " +
                "damaged, delete it and the service will create a new machine token.",
                errore);
        }
    }

    /// <summary>Scrive il deposito, in modo atomico e senza perdere i permessi.</summary>
    /// <param name="percorso">Il percorso del file.</param>
    /// <param name="credenziali">Le credenziali da depositare.</param>
    public static void Scrivi(string percorso, MachineCredentials credenziali)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(percorso);
        ArgumentNullException.ThrowIfNull(credenziali);

        // Nella stessa cartella del deposito: una Move fra volumi diversi non e' atomica.
        string temporaneo = percorso + ".nuovo";

        try
        {
            if (File.Exists(temporaneo))
            {
                File.Delete(temporaneo);
            }

            using (Stream flusso = CredentialFile.CreaProtetto(temporaneo))
            {
                JsonSerializer.Serialize(flusso, credenziali, Formato);
            }

            File.Move(temporaneo, percorso, overwrite: true);
        }
        finally
        {
            // Una sostituzione fallita lascerebbe qui il segreto in chiaro, e con i permessi
            // ereditati della cartella invece di quelli del deposito.
            if (File.Exists(temporaneo))
            {
                File.Delete(temporaneo);
            }
        }
    }
}