using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Observer.Core.Metrics;

namespace Observer.App.Services;

/// <summary>
/// Legge le metriche dal servizio. Interfaccia separata dall'implementazione HTTP solo
/// perche' il view model possa essere costruito anche con un finto client.
/// </summary>
public interface IMetricsClient
{
    /// <summary>Indirizzo del servizio interrogato, da mostrare a schermo.</summary>
    Uri BaseAddress { get; }

    /// <summary>Da dove arriva il token, senza il token dentro.</summary>
    string TokenOrigin { get; }

    /// <summary>Legge l'ultimo campionamento.</summary>
    Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken);

    /// <summary>Legge il catalogo delle metriche.</summary>
    Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Client HTTP verso Observer.Service.
/// </summary>
/// <remarks>
/// Sta in Observer.App e non in Observer.Core di proposito: un secondo consumatore non
/// esiste ancora, e spostarlo il giorno in cui esistera' costa poco.
/// <para>
/// Non lancia MAI per un guasto del servizio. Ogni modo di fallire diventa un
/// <see cref="ServiceOutcome"/> con la sua frase in italiano, perche' chi guarda la
/// finestra deve leggere cosa non va, non trovarla vuota.
/// </para>
/// </remarks>
public sealed class MetricsClient : IMetricsClient, IDisposable
{
    // Sotto il periodo di campionamento del servizio: una richiesta che impiega piu' di un
    // secondo e' gia' in ritardo sul campione successivo, e i 100 secondi predefiniti di
    // HttpClient lascerebbero la finestra ferma senza spiegazione per un minuto e mezzo.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient http;
    private readonly AuthenticationHeaderValue authorization;

    /// <summary>Costruisce il client sulle opzioni lette dalla configurazione.</summary>
    public MetricsClient(ObserverClientOptions options)
        : this(options, new SocketsHttpHandler(), disposeHandler: true)
    {
    }

    /// <summary>Costruisce il client su un handler fornito da fuori. Serve ai test.</summary>
    public MetricsClient(ObserverClientOptions options, HttpMessageHandler handler)
        : this(options, handler, disposeHandler: false)
    {
    }

    private MetricsClient(ObserverClientOptions options, HttpMessageHandler handler, bool disposeHandler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);

        BaseAddress = options.BaseAddress;
        TokenOrigin = options.TokenOrigin;
        authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken);
        http = new HttpClient(handler, disposeHandler) { Timeout = RequestTimeout };
    }

    /// <inheritdoc />
    public Uri BaseAddress { get; }

    /// <inheritdoc />
    public string TokenOrigin { get; }

    /// <inheritdoc />
    public async Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken)
    {
        (ServiceOutcome outcome, string problem, MachineSnapshot? snapshot) =
            await LeggiAsync<MachineSnapshot>("metrics/latest", cancellationToken).ConfigureAwait(false);

        if (outcome != ServiceOutcome.Ok || snapshot is null)
        {
            return new SnapshotFetch(outcome, problem, null);
        }

        if (snapshot.SchemaVersion != MachineSnapshot.CurrentSchemaVersion)
        {
            // Senza questo controllo un servizio piu' recente riempirebbe la finestra di
            // campi a zero marcati "Ok", che e' peggio di un messaggio d'errore.
            return new SnapshotFetch(
                ServiceOutcome.VersioneIncompatibile,
                $"The service at {BaseAddress} uses data format version " +
                snapshot.SchemaVersion.ToString(CultureInfo.InvariantCulture) +
                ", but this application only understands version " +
                MachineSnapshot.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture) +
                ". Service and client have to be updated together.",
                null);
        }

        return new SnapshotFetch(ServiceOutcome.Ok, string.Empty, snapshot);
    }

    /// <inheritdoc />
    public async Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken)
    {
        (ServiceOutcome outcome, string problem, List<CollectorCatalogEntry>? entries) =
            await LeggiAsync<List<CollectorCatalogEntry>>("metrics/catalog", cancellationToken).ConfigureAwait(false);

        return outcome == ServiceOutcome.Ok && entries is not null
            ? new CatalogFetch(ServiceOutcome.Ok, string.Empty, new MetricCatalog(entries))
            : new CatalogFetch(outcome, problem, null);
    }

    /// <inheritdoc />
    public void Dispose() => http.Dispose();

    private async Task<(ServiceOutcome Outcome, string Problem, T? Value)> LeggiAsync<T>(
        string percorsoRelativo,
        CancellationToken cancellationToken)
        where T : class
    {
        Uri indirizzo = new(BaseAddress, percorsoRelativo);

        try
        {
            using HttpRequestMessage richiesta = new(HttpMethod.Get, indirizzo);
            richiesta.Headers.Authorization = authorization;

            using HttpResponseMessage risposta =
                await http.SendAsync(richiesta, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

            string codice = ((int)risposta.StatusCode).ToString(CultureInfo.InvariantCulture);

            if (risposta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (
                    ServiceOutcome.TokenRifiutato,
                    $"The service at {BaseAddress} rejected the token ({codice}). " +
                    $"The token in use comes {TokenOrigin} and has to be IDENTICAL to the one the " +
                    "service was started with (Observer:ApiToken).",
                    null);
            }

            if (risposta.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return (
                    ServiceOutcome.NonAncoraPronto,
                    $"The service at {BaseAddress} is listening but hasn't produced its first " +
                    "reading yet. This usually clears on its own after a second or two.",
                    null);
            }

            if (!risposta.IsSuccessStatusCode)
            {
                return (
                    ServiceOutcome.RispostaInattesa,
                    $"The service at {BaseAddress} replied {codice} ({risposta.ReasonPhrase}), " +
                    "which this application doesn't know how to interpret.",
                    null);
            }

            T? valore = await risposta.Content
                .ReadFromJsonAsync<T>(WireOptions, cancellationToken)
                .ConfigureAwait(false);

            return valore is null
                ? (ServiceOutcome.RispostaIncomprensibile, TestoRispostaIlleggibile("the response was empty"), null)
                : (ServiceOutcome.Ok, string.Empty, valore);
        }
        catch (JsonException ex)
        {
            return (ServiceOutcome.RispostaIncomprensibile, TestoRispostaIlleggibile(ex.Message), null);
        }
        catch (NotSupportedException ex)
        {
            // Content-Type diverso da JSON: capita puntando per sbaglio a un altro servizio.
            return (ServiceOutcome.RispostaIncomprensibile, TestoRispostaIlleggibile(ex.Message), null);
        }
        catch (HttpRequestException ex)
        {
            return (ServiceOutcome.NonRaggiungibile, TestoNonRaggiungibile(ex.Message), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Scaduto il timeout della richiesta, non una chiusura dell'applicazione: qui
            // distinguere i due casi e' cio' che evita di mostrare un errore mentre si esce.
            return (
                ServiceOutcome.NonRaggiungibile,
                $"The service at {BaseAddress} didn't respond within " +
                RequestTimeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + " seconds.",
                null);
        }
    }

    private string TestoNonRaggiungibile(string dettaglio) =>
        $"Can't reach the service at {BaseAddress}. " +
        "Check that it is running (dotnet run --project src/Observer.Service) and that the " +
        $"address is correct. Technical detail: {dettaglio}";

    private string TestoRispostaIlleggibile(string dettaglio) =>
        $"{BaseAddress} responded, but not with a sample this application can read. " +
        $"It probably isn't Observer.Service. Technical detail: {dettaglio}";
}
