using System.Text.Json;
using System.Text.Json.Serialization;

namespace Observer.App.Services;

/// <summary>
/// Indirizzo del servizio e token con cui autenticarsi, gia' validati.
/// </summary>
/// <param name="BaseAddress">
/// Radice del servizio, sempre con la barra finale: senza, <see cref="Uri"/> risolverebbe
/// "metrics/latest" cancellando l'ultimo segmento di un indirizzo tipo
/// "http://host:5057/observer/", e la richiesta finirebbe sull'URL sbagliato.
/// </param>
/// <param name="ApiToken">Il token da mettere nell'header Authorization.</param>
/// <param name="TokenOrigin">
/// Da dove arriva il token, in italiano e senza il token dentro. Serve per dire a chi
/// legge lo schermo QUALE token il servizio ha rifiutato, senza stamparlo.
/// </param>
public sealed record ObserverClientOptions(Uri BaseAddress, string ApiToken, string TokenOrigin)
{
    /// <summary>
    /// Nasconde il token. I record generano un ToString() con TUTTE le proprieta' dentro:
    /// senza questo override basterebbe un binding distratto o una riga di log per
    /// stampare il segreto sullo schermo di chi passa.
    /// </summary>
    public override string ToString() =>
        FormattableString.Invariant($"ObserverClientOptions {{ BaseAddress = {BaseAddress}, TokenOrigin = {TokenOrigin} }}");
}

/// <summary>
/// Esito della lettura della configurazione: o le opzioni, o la frase da mostrare a
/// schermo. Mai entrambe nulle.
/// </summary>
/// <param name="Options">Le opzioni valide, oppure null.</param>
/// <param name="Problem">La spiegazione in italiano quando <paramref name="Options"/> e' null.</param>
public sealed record ClientConfigurationResult(ObserverClientOptions? Options, string? Problem);

/// <summary>
/// Contenuto del file di configurazione locale del client.
/// </summary>
/// <param name="BaseAddress">Indirizzo del servizio. Facoltativo.</param>
/// <param name="ApiToken">Token di accesso. Facoltativo se presente nell'ambiente.</param>
public sealed record ObserverClientFile(
    [property: JsonPropertyName("baseAddress")] string? BaseAddress,
    [property: JsonPropertyName("apiToken")] string? ApiToken);

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

    /// <summary>Indirizzo usato quando non ne viene indicato nessuno.</summary>
    public const string DefaultBaseAddress = "http://localhost:5057/";

    private static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Percorso del file di configurazione del client: su Windows
    /// <c>%APPDATA%\Observer\client.json</c>, su Linux <c>~/.config/Observer/client.json</c>.
    /// Sta FUORI dal repository apposta, cosi' il token non puo' finire in un commit.
    /// </summary>
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Observer",
        "client.json");

    /// <summary>
    /// Legge davvero ambiente e disco e produce la configurazione.
    /// </summary>
    public static ClientConfigurationResult Read() =>
        Resolve(
            Environment.GetEnvironmentVariable(TokenVariable),
            Environment.GetEnvironmentVariable(BaseAddressVariable),
            LeggiFile(FilePath));

    /// <summary>
    /// Combina ambiente e file secondo la precedenza stabilita, senza toccare il disco.
    /// </summary>
    /// <param name="tokenFromEnvironment">Valore di <see cref="TokenVariable"/>, se presente.</param>
    /// <param name="baseAddressFromEnvironment">Valore di <see cref="BaseAddressVariable"/>, se presente.</param>
    /// <param name="fileContent">Contenuto grezzo del file di configurazione, se esiste.</param>
    /// <remarks>
    /// L'ambiente VINCE sul file, per lo stesso motivo per cui vince nel servizio: un token
    /// vecchio dimenticato nel file sovrascriverebbe in silenzio quello nuovo appena
    /// esportato, e il sintomo sarebbe un 401 inspiegabile.
    /// </remarks>
    public static ClientConfigurationResult Resolve(
        string? tokenFromEnvironment,
        string? baseAddressFromEnvironment,
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
                $"Il file di configurazione {FilePath} non e' un JSON valido ({ex.Message}). " +
                $"Deve contenere esattamente: {{ \"baseAddress\": \"{DefaultBaseAddress}\", \"apiToken\": \"il-tuo-token\" }}");
        }

        string? token = Primo(tokenFromEnvironment, file?.ApiToken);
        string origine = string.IsNullOrWhiteSpace(tokenFromEnvironment)
            ? $"dal file {FilePath}"
            : $"dalla variabile d'ambiente {TokenVariable}";

        if (string.IsNullOrWhiteSpace(token))
        {
            return new ClientConfigurationResult(null, TestoTokenMancante());
        }

        string indirizzo = Primo(baseAddressFromEnvironment, file?.BaseAddress) ?? DefaultBaseAddress;

        if (!Uri.TryCreate(ConBarraFinale(indirizzo), UriKind.Absolute, out Uri? baseAddress)
            || (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps))
        {
            return new ClientConfigurationResult(
                null,
                $"L'indirizzo del servizio \"{indirizzo}\" non e' utilizzabile. " +
                $"Deve essere un indirizzo http o https completo, per esempio {DefaultBaseAddress}. " +
                $"Si imposta nella variabile d'ambiente {BaseAddressVariable} oppure nel campo " +
                $"\"baseAddress\" del file {FilePath}.");
        }

        return new ClientConfigurationResult(
            new ObserverClientOptions(baseAddress, token, origine),
            null);
    }

    /// <summary>Il testo mostrato quando manca il token. Estratto perche' e' anche cio' che il test verifica.</summary>
    public static string TestoTokenMancante() =>
        "Nessun token configurato, quindi non c'e' nemmeno da provare a collegarsi: il servizio " +
        "rifiuta ogni richiesta senza autenticazione. Va indicato lo STESSO token con cui e' " +
        "partito il servizio, in uno dei due modi: " +
        $"1) nella variabile d'ambiente {TokenVariable}; " +
        $"2) nel file {FilePath}, con dentro " +
        $"{{ \"baseAddress\": \"{DefaultBaseAddress}\", \"apiToken\": \"il-tuo-token\" }}. " +
        "Se ci sono entrambi vince la variabile d'ambiente.";

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
