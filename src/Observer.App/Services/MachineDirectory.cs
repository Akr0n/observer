using System.Text.Json;
using System.Text.Json.Serialization;
using Observer.Core.Security;

namespace Observer.App.Services;

/// <summary>Una macchina come sta scritta nel file.</summary>
/// <param name="Name">Come chiamarla a schermo. Facoltativo.</param>
/// <param name="BaseAddress">Indirizzo del servizio, in HTTPS.</param>
/// <param name="ApiToken">Il token di QUELLA macchina.</param>
/// <param name="Fingerprint">L'impronta del certificato di QUELLA macchina.</param>
public sealed record MachineEntry(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("baseAddress")] string? BaseAddress,
    [property: JsonPropertyName("apiToken")] string? ApiToken,
    [property: JsonPropertyName("fingerprint")] string? Fingerprint);

/// <summary>Il contenuto di <c>machines.json</c>.</summary>
/// <param name="Machines">Le macchine remote. Quella locale non si elenca: c'e' sempre.</param>
public sealed record MachinesFile(
    [property: JsonPropertyName("machines")] IReadOnlyList<MachineEntry>? Machines);

/// <summary>L'elenco risolto, con i motivi di cio' che non ci e' entrato.</summary>
/// <param name="Machines">Le macchine utilizzabili. La prima e' sempre questa.</param>
/// <param name="Problems">Una frase per ogni voce scartata, gia' pronta per lo schermo.</param>
public sealed record MachineListResult(
    IReadOnlyList<ObserverEndpoint> Machines,
    IReadOnlyList<string> Problems);

/// <summary>
/// Da dove arriva l'elenco delle macchine da guardare.
/// </summary>
/// <remarks>
/// Il file e' l'unica verita' e la dashboard lo <b>legge soltanto</b>. Non ha una finestra per
/// aggiungere macchine, e non e' una mancanza: significa nessuna validazione di campi da
/// mantenere, nessuna finestra di modifica, e soprattutto nessun programma con interfaccia
/// grafica che scrive un file pieno di credenziali di altre macchine.
/// <para>
/// La macchina locale <b>non si elenca</b> e non si puo' togliere: e' sempre la prima voce, e
/// non ha ne' indirizzo ne' token ne' impronta, perche' passa dal canale locale.
/// </para>
/// </remarks>
public static class MachineDirectory
{
    /// <summary>Il nome del file, accanto al vecchio <c>client.json</c>.</summary>
    public const string NomeFile = "machines.json";

    private static readonly JsonSerializerOptions Formato = new(JsonSerializerDefaults.Web);

    /// <summary>Dove sta l'elenco: accanto alla configurazione a macchina singola.</summary>
    public static string FilePath => Path.Combine(
        Path.GetDirectoryName(ClientConfiguration.FilePath) ?? ".",
        NomeFile);

    /// <summary>Legge davvero il disco e l'ambiente.</summary>
    /// <returns>L'elenco e i problemi.</returns>
    public static MachineListResult Read() =>
        Resolve(LeggiFile(FilePath), ClientConfiguration.Read(), SecretStores.PerQuestaMacchina());

    /// <summary>Compone l'elenco senza toccare il disco.</summary>
    /// <param name="contenuto">Il contenuto grezzo di <c>machines.json</c>, se esiste.</param>
    /// <param name="ripiego">
    /// Cio' che dice la configurazione a macchina singola, usata quando l'elenco non c'e'.
    /// </param>
    /// <returns>L'elenco e i problemi.</returns>
    /// <remarks>
    /// Se <c>machines.json</c> non esiste vale ancora il vecchio <c>client.json</c> con la sua
    /// variabile d'ambiente. Chi aveva gia' configurato una macchina non deve rifare niente solo
    /// perche' adesso se ne possono elencare tante.
    /// </remarks>
    public static MachineListResult Resolve(
        string? contenuto, ClientConfigurationResult ripiego, ISecretStore deposito)
    {
        ArgumentNullException.ThrowIfNull(ripiego);
        ArgumentNullException.ThrowIfNull(deposito);

        // La macchina su cui si sta seduti c'e' SEMPRE, e sta per prima. Non ha bisogno di
        // niente per funzionare, quindi non c'e' modo di sbagliarne la configurazione.
        List<ObserverEndpoint> macchine = [ObserverEndpoint.CanaleLocale()];
        List<string> problemi = [];

        if (string.IsNullOrWhiteSpace(contenuto))
        {
            AggiungiRipiego(ripiego, macchine, problemi);

            return new MachineListResult(macchine, problemi);
        }

        MachinesFile? file;

        try
        {
            file = JsonSerializer.Deserialize<MachinesFile>(contenuto, Formato);
        }
        catch (JsonException errore)
        {
            problemi.Add(
                $"{FilePath} isn't valid JSON ({errore.Message}). Until it is fixed, only this " +
                "machine is listed. " + Esempio());

            return new MachineListResult(macchine, problemi);
        }

        if (file?.Machines is null)
        {
            // JSON valido ma senza l'elenco: non e' un file "vuoto e va bene", e' un file che
            // qualcuno credeva di aver scritto. Azzerare l'elenco in silenzio farebbe sparire
            // anche la vecchia configurazione a macchina singola, e chi guarda vedrebbe solo
            // sparire una macchina senza sapere perche'.
            problemi.Add(
                $"{FilePath} has no \"machines\" list, so nothing in it can be used. " + Esempio());

            AggiungiRipiego(ripiego, macchine, problemi);

            return new MachineListResult(macchine, problemi);
        }

        foreach (MachineEntry voce in file.Machines)
        {
            try
            {
                if (Converti(voce, deposito) is { } punto)
                {
                    macchine.Add(punto);
                }
                else
                {
                    problemi.Add(Problema(voce, deposito));
                }
            }
            catch (SecretStoreException errore)
            {
                // Un deposito di cui non ci si puo' fidare fa saltare QUELLA voce e lo dice,
                // invece di far cadere l'intero elenco: le altre macchine non c'entrano.
                problemi.Add(errore.Message);
            }
        }

        return new MachineListResult(macchine, problemi);
    }

    /// <summary>
    /// Aggiunge la vecchia configurazione a macchina singola, se e' utilizzabile.
    /// </summary>
    /// <remarks>
    /// Passa dagli STESSI requisiti delle voci elencate, e non e' ridondanza: senza questo
    /// controllo il vecchio <c>client.json</c> sarebbe una porta di servizio che riammette
    /// <c>http://</c> e i punti senza impronta, cioe' esattamente cio' che l'elenco rifiuta.
    /// </remarks>
    private static void AggiungiRipiego(
        ClientConfigurationResult ripiego,
        List<ObserverEndpoint> macchine,
        List<string> problemi)
    {
        if (ripiego.Endpoint is not { Kind: EndpointKind.Remoto } singola)
        {
            if (ripiego.Problem is { Length: > 0 } problema)
            {
                problemi.Add(problema);
            }

            return;
        }

        if (singola.BaseAddress.Scheme != Uri.UriSchemeHttps || !singola.ImprontaFissata)
        {
            problemi.Add(
                $"{singola.Descrizione} comes from the older single-machine configuration and " +
                "can't be used as it stands: a remote machine needs an https address and a " +
                "certificate fingerprint. " + Esempio());

            return;
        }

        macchine.Add(singola);
    }

    /// <summary>Un esempio di file corretto, per i messaggi.</summary>
    /// <returns>Il testo dell'esempio.</returns>
    public static string Esempio() =>
        "A machine looks like this: { \"name\": \"laptop\", \"baseAddress\": " +
        "\"https://laptop:5058/\", \"fingerprint\": \"sha256:...\" }. Run \"observer share\" " +
        "on that machine to get the address and the fingerprint, and \"observer token set " +
        "laptop\" here to keep its token out of this file.";

    private static ObserverEndpoint? Converti(MachineEntry voce, ISecretStore deposito)
    {
        if (string.IsNullOrWhiteSpace(voce.BaseAddress)
            || !Uri.TryCreate(ConBarraFinale(voce.BaseAddress.Trim()), UriKind.Absolute, out Uri? indirizzo)
            || indirizzo.Scheme != Uri.UriSchemeHttps
            || CertificateFingerprint.Normalizza(voce.Fingerprint) is null
            || string.IsNullOrWhiteSpace(voce.Name))
        {
            return null;
        }

        // Un token scritto nel file NON viene usato, nemmeno se e' quello giusto. Accettarlo
        // "solo per compatibilita'" vorrebbe dire che il segreto puo' restare li' per sempre,
        // e che questa modifica non ha tolto niente a nessuno.
        if (!string.IsNullOrWhiteSpace(voce.ApiToken))
        {
            return null;
        }

        return deposito.TryRead(voce.Name.Trim(), out string token)
            ? ObserverEndpoint.Remoto(
                indirizzo,
                token,
                "from the secret store",
                voce.Fingerprint,
                voce.Name)
            : null;
    }

    /// <summary>Perche' una voce e' stata scartata, detto in modo che si possa correggere.</summary>
    /// <remarks>
    /// Il caso di gran lunga piu' probabile e' <c>http://</c> al posto di <c>https://</c>, e
    /// merita una frase sua: da quando il servizio ha un certificato di macchina non risponde
    /// piu' in chiaro sulla rete, quindi un indirizzo vecchio non e' un errore di battitura ma
    /// una configurazione che era giusta ieri.
    /// </remarks>
    private static string Problema(MachineEntry voce, ISecretStore deposito)
    {
        string chi = string.IsNullOrWhiteSpace(voce.Name)
            ? (string.IsNullOrWhiteSpace(voce.BaseAddress) ? "an entry with no address" : voce.BaseAddress.Trim())
            : voce.Name.Trim();

        if (!string.IsNullOrWhiteSpace(voce.BaseAddress)
            && voce.BaseAddress.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return
                chi + " is listed with an http:// address. Observer no longer answers in the " +
                "clear over the network: the token used to cross it once a second, and a single " +
                "packet capture handed over a permanent credential. Change it to https:// and " +
                "add that machine's fingerprint.";
        }

        if (CertificateFingerprint.Normalizza(voce.Fingerprint) is null)
        {
            return string.IsNullOrWhiteSpace(voce.Fingerprint)
                ? chi + " has no fingerprint, so there is no way to tell that machine apart from " +
                  "anyone able to stand in the middle of the connection. " + Esempio()
                : chi + " has a fingerprint that isn't a SHA-256 value of " +
                  CertificateFingerprint.QuanteCifre() + " hex digits. " + Esempio();
        }

        if (!string.IsNullOrWhiteSpace(voce.ApiToken))
        {
            return
                chi + " still carries its token inside " + NomeFile + ", and Observer will not " +
                "use it from there. That token now also authorises ending processes on that " +
                "machine, so a file meant to be read, copied and shared is the wrong place for " +
                "it. Run \"observer token set " + chi + "\" to hand it over, then delete the " +
                "\"apiToken\" line from " + FilePath + ".";
        }

        if (string.IsNullOrWhiteSpace(voce.Name))
        {
            return
                "An entry with address " + (voce.BaseAddress ?? "(none)").Trim() + " has no " +
                "\"name\", and the name is how its token is looked up in " +
                deposito.Descrizione + ". " + Esempio();
        }

        // L'indirizzo si controlla PRIMA del token mancante: con un indirizzo malformato quella
        // macchina non e' raggiungibile comunque, e mandare a depositare un token sarebbe
        // mandare a fare la cosa giusta nell'ordine sbagliato.
        if (string.IsNullOrWhiteSpace(voce.BaseAddress)
            || !Uri.TryCreate(ConBarraFinale(voce.BaseAddress.Trim()), UriKind.Absolute, out Uri? indirizzo)
            || indirizzo.Scheme != Uri.UriSchemeHttps)
        {
            return chi + " can't be used: the address must be a full https:// address. " + Esempio();
        }

        return
            chi + " has no token in " + deposito.Descrizione + ", and another machine's Observer " +
            "rejects every request that isn't authenticated. Run \"observer token set " + chi +
            "\" to store it.";
    }

    private static string ConBarraFinale(string indirizzo) =>
        indirizzo.EndsWith('/') ? indirizzo : indirizzo + "/";

    private static string? LeggiFile(string percorso)
    {
        try
        {
            return File.Exists(percorso) ? File.ReadAllText(percorso) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}