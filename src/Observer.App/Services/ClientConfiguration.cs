using System.Text.Json;
using Observer.Core.Security;
using System.Text.Json.Serialization;

namespace Observer.App.Services;

/// <summary>
/// Esito della lettura della configurazione: o il punto da interrogare, o la frase da
/// mostrare a schermo. Mai entrambi nulli.
/// </summary>
/// <param name="Endpoint">Il servizio da interrogare, oppure null.</param>
/// <param name="Problem">La spiegazione quando <paramref name="Endpoint"/> e' null.</param>
public sealed record ClientConfigurationResult(ObserverEndpoint? Endpoint, string? Problem);

/// <summary>
/// Contenuto del file di configurazione locale del client.
/// </summary>
/// <param name="BaseAddress">Indirizzo del servizio. Facoltativo.</param>
/// <param name="ApiToken">Token di accesso. Facoltativo se presente nell'ambiente.</param>
/// <param name="Fingerprint">
/// L'impronta del certificato di quella macchina. Obbligatoria per un punto remoto: senza,
/// il collegamento sarebbe cifrato ma non saprebbe con CHI.
/// </param>
public sealed record ObserverClientFile(
    [property: JsonPropertyName("baseAddress")] string? BaseAddress,
    [property: JsonPropertyName("apiToken")] string? ApiToken,
    [property: JsonPropertyName("fingerprint")] string? Fingerprint);

/// <summary>
/// Decide da dove il client prende indirizzo e token.
/// </summary>
/// <remarks>
/// La parte che decide (<see cref="Resolve"/>) e' una funzione pura sui suoi ingressi:
/// non legge ne' ambiente ne' disco, quindi e' verificabile con un test invece che
/// avviando l'applicazione e guardandola.
/// </remarks>
public static class ClientConfiguration
{
    /// <summary>Variabile d'ambiente con il token. Stesso nome usato dal servizio.</summary>
    public const string TokenVariable = "Observer__ApiToken";

    /// <summary>Variabile d'ambiente con l'indirizzo del servizio.</summary>
    public const string BaseAddressVariable = "Observer__BaseAddress";

    /// <summary>Variabile d'ambiente con l'impronta del certificato di quella macchina.</summary>
    /// <remarks>
    /// Esiste per simmetria con le altre due, e non e' una comodita': senza, rendere obbligatoria
    /// l'impronta avrebbe reso inutilizzabile l'intera via delle variabili d'ambiente, che e'
    /// l'unica praticabile dove il file di configurazione non si puo' scrivere.
    /// </remarks>
    public const string FingerprintVariable = "Observer__Fingerprint";

    /// <summary>Un indirizzo di esempio, per i messaggi. NON e' piu' un valore predefinito.</summary>
    /// <remarks>
    /// Senza indirizzo configurato il client va sul canale LOCALE, che non ha ne' porta ne'
    /// token. Un indirizzo si mette solo per guardare un ALTRO computer.
    /// </remarks>
    public const string EsempioIndirizzo = "https://another-machine:5058/";

    private static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Percorso del file di configurazione del client: su Windows
    /// <c>%LOCALAPPDATA%\Observer\client.json</c>, su Linux
    /// <c>~/.local/share/Observer/client.json</c>.
    /// Sta FUORI dal repository apposta, cosi' il token non puo' finire in un commit.
    /// </summary>
    /// <remarks>
    /// LocalApplicationData e non ApplicationData, cioe' Local e non Roaming: su una macchina
    /// aggiunta a un dominio la cartella Roaming viene sincronizzata con un file server, quindi
    /// il token attraverserebbe la rete e resterebbe depositato altrove. Un segreto legato a
    /// UNA macchina non deve seguire l'utente da un computer all'altro.
    /// </remarks>
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Observer",
        "client.json");

    /// <summary>
    /// Legge davvero ambiente e disco e produce la configurazione.
    /// </summary>
    public static ClientConfigurationResult Read() =>
        Resolve(
            Environment.GetEnvironmentVariable(TokenVariable),
            Environment.GetEnvironmentVariable(BaseAddressVariable),
            Environment.GetEnvironmentVariable(FingerprintVariable),
            LeggiFile(FilePath));

    /// <summary>
    /// Combina ambiente e file secondo la precedenza stabilita, senza toccare il disco.
    /// </summary>
    /// <param name="tokenFromEnvironment">Valore di <see cref="TokenVariable"/>, se presente.</param>
    /// <param name="baseAddressFromEnvironment">Valore di <see cref="BaseAddressVariable"/>, se presente.</param>
    /// <param name="fingerprintFromEnvironment">Valore di <see cref="FingerprintVariable"/>, se presente.</param>
    /// <param name="fileContent">Contenuto grezzo del file di configurazione, se esiste.</param>
    /// <remarks>
    /// L'ambiente VINCE sul file, per lo stesso motivo per cui vince nel servizio: un token
    /// vecchio dimenticato nel file sovrascriverebbe in silenzio quello nuovo appena
    /// esportato, e il sintomo sarebbe un 401 inspiegabile.
    /// </remarks>
    public static ClientConfigurationResult Resolve(
        string? tokenFromEnvironment,
        string? baseAddressFromEnvironment,
        string? fingerprintFromEnvironment,
        string? fileContent)
    {
        ObserverClientFile? file;

        try
        {
            file = string.IsNullOrWhiteSpace(fileContent)
                ? null
                : JsonSerializer.Deserialize<ObserverClientFile>(fileContent, FileOptions);
        }
        catch (JsonException ex)
        {
            return new ClientConfigurationResult(
                null,
                $"The configuration file {FilePath} isn't valid JSON ({ex.Message}). " +
                $"It must contain exactly: {{ \"baseAddress\": \"{EsempioIndirizzo}\", \"apiToken\": \"the other machine's token\" }}");
        }

        string? indirizzo = Primo(baseAddressFromEnvironment, file?.BaseAddress);

        if (indirizzo is null)
        {
            // NESSUN indirizzo configurato significa "guarda la macchina su cui stai", e su
            // quella il servizio non chiede alcun token. E' cio' che rende installabile la
            // dashboard: dopo l'installazione non c'e' niente da configurare.
            // Un token esportato per errore NON dirotta il client: qui viene ignorato.
            return new ClientConfigurationResult(ObserverEndpoint.CanaleLocale(), null);
        }

        if (!Uri.TryCreate(ConBarraFinale(indirizzo), UriKind.Absolute, out Uri? baseAddress)
            || baseAddress.Scheme != Uri.UriSchemeHttps)
        {
            // http:// NON e' piu' accettato, e non e' un irrigidimento gratuito: il servizio
            // non risponde piu' in chiaro sulla rete. Accettarlo qui vorrebbe dire spedire il
            // token una volta al secondo verso una porta che non c'e', oppure - peggio - verso
            // qualcosa che risponde al posto suo.
            return new ClientConfigurationResult(
                null,
                $"The service address \"{indirizzo}\" can't be used. " +
                $"It must be a full https address, for example {EsempioIndirizzo}. " +
                "Observer no longer answers in the clear over the network: the token used to " +
                "cross it once a second. " +
                $"Set it in the {BaseAddressVariable} environment variable, or in the " +
                $"\"baseAddress\" field of {FilePath}. " +
                "Remove it entirely to watch the machine you are sitting at.");
        }

        string? token = Primo(tokenFromEnvironment, file?.ApiToken);

        if (token is null)
        {
            // Un indirizzo remoto senza credenziale non e' un caso da indovinare: quel
            // servizio rifiutera' ogni richiesta, e dirlo subito e' meglio che mostrare 401
            // a raffica una volta al secondo.
            return new ClientConfigurationResult(null, TestoTokenMancante(indirizzo));
        }

        string? impronta = Primo(fingerprintFromEnvironment, file?.Fingerprint);

        if (CertificateFingerprint.Normalizza(impronta) is null)
        {
            // Cifrato non basta. Senza impronta il collegamento e' protetto da chi ascolta ma
            // non da chi si mette in mezzo, e quello e' il caso peggiore perche' sembra sicuro.
            return new ClientConfigurationResult(null, TestoImprontaMancante(indirizzo));
        }

        string origine = string.IsNullOrWhiteSpace(tokenFromEnvironment)
            ? $"from the file {FilePath}"
            : $"from the {TokenVariable} environment variable";

        return new ClientConfigurationResult(
            ObserverEndpoint.Remoto(baseAddress, token, origine, impronta),
            null);
    }

    /// <summary>Il testo mostrato quando manca l'impronta per un servizio REMOTO.</summary>
    /// <param name="indirizzo">L'indirizzo configurato.</param>
    /// <returns>La frase da mostrare.</returns>
    public static string TestoImprontaMancante(string indirizzo) =>
        $"No certificate fingerprint is configured for {indirizzo}. Observer's certificate is " +
        "self-signed, so without a fingerprint there is nothing to tell that machine apart from " +
        "anyone able to stand in the middle of the connection: the traffic would be encrypted, " +
        "but not to a known machine. Run \"observer share\" on THAT machine and put the value it " +
        $"prints in the \"fingerprint\" field of {FilePath}, next to the token.";

    /// <summary>Il testo mostrato quando manca il token per un servizio REMOTO.</summary>
    /// <param name="indirizzo">L'indirizzo configurato.</param>
    /// <returns>La frase da mostrare.</returns>
    public static string TestoTokenMancante(string indirizzo) =>
        $"No token is configured for {indirizzo}, so there is no point in trying to connect: " +
        "another machine's Observer rejects every request that isn't authenticated. Get its " +
        "token by running \"observer share\" on THAT machine, from an elevated terminal, then " +
        $"put it in the {TokenVariable} environment variable, or in the \"apiToken\" field of " +
        $"{FilePath}. " +
        "To watch the machine you are sitting at instead, remove the address entirely: no token " +
        "is needed for that.";

    private static string? LeggiFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            // Un file illeggibile equivale a un file assente: la diagnosi utile e' quella
            // sul token mancante, non lo stack trace di un accesso al disco.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Primo(string? preferito, string? alternativa) =>
        string.IsNullOrWhiteSpace(preferito)
            ? (string.IsNullOrWhiteSpace(alternativa) ? null : alternativa.Trim())
            : preferito.Trim();

    private static string ConBarraFinale(string indirizzo) =>
        indirizzo.EndsWith('/') ? indirizzo : indirizzo + "/";
}