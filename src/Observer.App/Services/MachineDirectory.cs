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

/// <summary>Il content di <c>machines.json</c>.</summary>
/// <param name="Machines">Le machines remote. Quella locale non si elenca: c'e' sempre.</param>
public sealed record MachinesFile(
    [property: JsonPropertyName("machines")] IReadOnlyList<MachineEntry>? Machines);

/// <summary>L'elenco risolto, con i motivi di cio' che non ci e' entrato.</summary>
/// <param name="Machines">Le machines utilizzabili. La prima e' sempre questa.</param>
/// <param name="Problems">Una frase per ogni entry scartata, gia' pronta per lo schermo.</param>
public sealed record MachineListResult(
    IReadOnlyList<ObserverEndpoint> Machines,
    IReadOnlyList<string> Problems);

/// <summary>
/// Da dove arriva l'elenco delle machines da guardare.
/// </summary>
/// <remarks>
/// Il file e' l'unica verita' e la dashboard lo <b>legge soltanto</b>. Non ha una finestra per
/// aggiungere machines, e non e' una mancanza: significa nessuna validazione di campi da
/// mantenere, nessuna finestra di modifica, e soprattutto nessun programma con interfaccia
/// grafica che scrive un file pieno di credenziali di altre machines.
/// <para>
/// La macchina locale <b>non si elenca</b> e non si puo' togliere: e' sempre la prima entry, e
/// non ha ne' address ne' token ne' impronta, perche' passa dal canale locale.
/// </para>
/// </remarks>
public static class MachineDirectory
{
    /// <summary>Il nome del file, accanto al vecchio <c>client.json</c>.</summary>
    public const string FileName = "machines.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Dove sta l'elenco: accanto alla configurazione a macchina singleMachine.</summary>
    public static string FilePath => Path.Combine(
        Path.GetDirectoryName(ClientConfiguration.FilePath) ?? ".",
        FileName);

    /// <summary>Legge davvero il disco e l'ambiente.</summary>
    /// <returns>L'elenco e i problems.</returns>
    public static MachineListResult Read() =>
        Resolve(ReadFile(FilePath), ClientConfiguration.Read(), SecretStores.ForThisMachine());

    /// <summary>Compone l'elenco senza toccare il disco.</summary>
    /// <param name="content">Il content grezzo di <c>machines.json</c>, se esiste.</param>
    /// <param name="fallback">
    /// Cio' che dice la configurazione a macchina singleMachine, usata quando l'elenco non c'e'.
    /// </param>
    /// <returns>L'elenco e i problems.</returns>
    /// <remarks>
    /// Se <c>machines.json</c> non esiste vale ancora il vecchio <c>client.json</c> con la sua
    /// variabile d'ambiente. Chi aveva gia' configurato una macchina non deve rifare niente solo
    /// perche' adesso se ne possono elencare tante.
    /// </remarks>
    public static MachineListResult Resolve(
        string? content, ClientConfigurationResult fallback, ISecretStore store)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(store);

        // La macchina su cui si sta seduti c'e' SEMPRE, e sta per prima. Non ha bisogno di
        // niente per funzionare, quindi non c'e' modo di sbagliarne la configurazione.
        List<ObserverEndpoint> machines = [ObserverEndpoint.LocalChannel()];
        List<string> problems = [];

        if (string.IsNullOrWhiteSpace(content))
        {
            AddFallback(fallback, machines, problems);

            return new MachineListResult(machines, problems);
        }

        MachinesFile? file;

        try
        {
            file = JsonSerializer.Deserialize<MachinesFile>(content, JsonOptions);
        }
        catch (JsonException error)
        {
            problems.Add(
                $"{FilePath} isn't valid JSON ({error.Message}). Until it is fixed, only this " +
                "machine is listed. " + DescribeExample());

            return new MachineListResult(machines, problems);
        }

        if (file?.Machines is null)
        {
            // JSON valido ma senza l'elenco: non e' un file "vuoto e va bene", e' un file che
            // qualcuno credeva di aver scritto. Azzerare l'elenco in silenzio farebbe sparire
            // anche la vecchia configurazione a macchina singleMachine, e label guarda vedrebbe solo
            // sparire una macchina senza sapere perche'.
            problems.Add(
                $"{FilePath} has no \"machines\" list, so nothing in it can be used. " + DescribeExample());

            AddFallback(fallback, machines, problems);

            return new MachineListResult(machines, problems);
        }

        foreach (MachineEntry entry in file.Machines)
        {
            try
            {
                if (ToEndpoint(entry, store) is { } endpoint)
                {
                    machines.Add(endpoint);
                }
                else
                {
                    problems.Add(ExplainRejection(entry, store));
                }
            }
            catch (SecretStoreException error)
            {
                // Un store di cui non ci si puo' fidare fa saltare QUELLA entry e lo dice,
                // invece di far cadere l'intero elenco: le altre machines non c'entrano.
                problems.Add(error.Message);
            }
        }

        return new MachineListResult(machines, problems);
    }

    /// <summary>
    /// Aggiunge la vecchia configurazione a macchina singleMachine, se e' utilizzabile.
    /// </summary>
    /// <remarks>
    /// Passa dagli STESSI requisiti delle voci elencate, e non e' ridondanza: senza questo
    /// controllo il vecchio <c>client.json</c> sarebbe una porta di servizio che riammette
    /// <c>http://</c> e i punti senza impronta, cioe' esattamente cio' che l'elenco rifiuta.
    /// </remarks>
    private static void AddFallback(
        ClientConfigurationResult fallback,
        List<ObserverEndpoint> machines,
        List<string> problems)
    {
        if (fallback.Endpoint is not { Kind: EndpointKind.Remote } singleMachine)
        {
            if (fallback.Problem is { Length: > 0 } problem)
            {
                problems.Add(problem);
            }

            return;
        }

        if (singleMachine.BaseAddress.Scheme != Uri.UriSchemeHttps || !singleMachine.IsFingerprintPinned)
        {
            problems.Add(
                $"{singleMachine.Description} comes from the older single-machine configuration and " +
                "can't be used as it stands: a remote machine needs an https address and a " +
                "certificate fingerprint. " + DescribeExample());

            return;
        }

        machines.Add(singleMachine);
    }

    /// <summary>Un esempio di file corretto, per i messaggi.</summary>
    /// <returns>Il testo dell'esempio.</returns>
    public static string DescribeExample() =>
        "A machine looks like this: { \"name\": \"laptop\", \"baseAddress\": " +
        "\"https://laptop:5058/\", \"fingerprint\": \"sha256:...\" }. Run \"observer share\" " +
        "on that machine to get the address and the fingerprint, and \"observer token set " +
        "laptop\" here to keep its token out of this file.";

    private static ObserverEndpoint? ToEndpoint(MachineEntry entry, ISecretStore store)
    {
        if (string.IsNullOrWhiteSpace(entry.BaseAddress)
            || !Uri.TryCreate(WithTrailingSlash(entry.BaseAddress.Trim()), UriKind.Absolute, out Uri? address)
            || address.Scheme != Uri.UriSchemeHttps
            || CertificateFingerprint.Normalize(entry.Fingerprint) is null
            || string.IsNullOrWhiteSpace(entry.Name))
        {
            return null;
        }

        // Un token scritto nel file NON viene usato, nemmeno se e' quello giusto. Accettarlo
        // "solo per compatibilita'" vorrebbe dire che il segreto puo' restare li' per sempre,
        // e che questa modifica non ha tolto niente a nessuno.
        if (!string.IsNullOrWhiteSpace(entry.ApiToken))
        {
            return null;
        }

        return store.TryRead(entry.Name.Trim(), out string token)
            ? ObserverEndpoint.Remote(
                address,
                token,
                "from the secret store",
                entry.Fingerprint,
                entry.Name)
            : null;
    }

    /// <summary>Perche' una entry e' stata scartata, detto in modo che si possa correggere.</summary>
    /// <remarks>
    /// Il caso di gran lunga piu' probabile e' <c>http://</c> al posto di <c>https://</c>, e
    /// merita una frase sua: da quando il servizio ha un certificato di macchina non risponde
    /// piu' in chiaro sulla rete, quindi un address vecchio non e' un error di battitura ma
    /// una configurazione che era giusta ieri.
    /// </remarks>
    private static string ExplainRejection(MachineEntry entry, ISecretStore store)
    {
        string label = string.IsNullOrWhiteSpace(entry.Name)
            ? (string.IsNullOrWhiteSpace(entry.BaseAddress) ? "an entry with no address" : entry.BaseAddress.Trim())
            : entry.Name.Trim();

        if (!string.IsNullOrWhiteSpace(entry.BaseAddress)
            && entry.BaseAddress.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return
                label + " is listed with an http:// address. Observer no longer answers in the " +
                "clear over the network: the token used to cross it once a second, and a single " +
                "packet capture handed over a permanent credential. Change it to https:// and " +
                "add that machine's fingerprint.";
        }

        if (CertificateFingerprint.Normalize(entry.Fingerprint) is null)
        {
            return string.IsNullOrWhiteSpace(entry.Fingerprint)
                ? label + " has no fingerprint, so there is no way to tell that machine apart from " +
                  "anyone able to stand in the middle of the connection. " + DescribeExample()
                : label + " has a fingerprint that isn't a SHA-256 value of " +
                  CertificateFingerprint.DigitCount() + " hex digits. " + DescribeExample();
        }

        if (!string.IsNullOrWhiteSpace(entry.ApiToken))
        {
            return
                label + " still carries its token inside " + FileName + ", and Observer will not " +
                "use it from there. That token now also authorises ending processes on that " +
                "machine, so a file meant to be read, copied and shared is the wrong place for " +
                "it. Run \"observer token set " + label + "\" to hand it over, then delete the " +
                "\"apiToken\" line from " + FilePath + ".";
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            return
                "An entry with address " + (entry.BaseAddress ?? "(none)").Trim() + " has no " +
                "\"name\", and the name is how its token is looked up in " +
                store.Description + ". " + DescribeExample();
        }

        // L'address si controlla PRIMA del token mancante: con un address malformato quella
        // macchina non e' raggiungibile comunque, e mandare a depositare un token sarebbe
        // mandare a fare la cosa giusta nell'ordine sbagliato.
        if (string.IsNullOrWhiteSpace(entry.BaseAddress)
            || !Uri.TryCreate(WithTrailingSlash(entry.BaseAddress.Trim()), UriKind.Absolute, out Uri? address)
            || address.Scheme != Uri.UriSchemeHttps)
        {
            return label + " can't be used: the address must be a full https:// address. " + DescribeExample();
        }

        return
            label + " has no token in " + store.Description + ", and another machine's Observer " +
            "rejects every request that isn't authenticated. Run \"observer token set " + label +
            "\" to store it.";
    }

    private static string WithTrailingSlash(string address) =>
        address.EndsWith('/') ? address : address + "/";

    private static string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
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