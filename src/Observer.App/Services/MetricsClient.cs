using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json;
using Observer.Core.Metrics;

namespace Observer.App.Services;

/// <summary>
/// Legge le metriche dal servizio. Interfaccia separata dall'implementazione HTTP solo
/// perche' il view model possa essere costruito anche con un finto client.
/// </summary>
public interface IMetricsClient
{
    /// <summary>Il punto interrogato, da mostrare a schermo. Non stampa mai il token.</summary>
    ObserverEndpoint Endpoint { get; }

    /// <summary>Legge l'ultimo campionamento.</summary>
    Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken);

    /// <summary>Legge il catalogo delle metriche.</summary>
    Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken);

    /// <summary>Legge lo storico di una serie.</summary>
    /// <param name="richiesta">Quale serie, da quando, con che risoluzione.</param>
    /// <param name="cancellationToken">Annullato alla chiusura.</param>
    /// <returns>I punti, oppure il motivo per cui non ci sono.</returns>
    Task<HistoryFetch> GetHistoryAsync(HistoryQuery richiesta, CancellationToken cancellationToken);
}

/// <summary>Che pezzo di storico si vuole.</summary>
/// <param name="Collector">Identificatore del collector.</param>
/// <param name="Metric">Identificatore della metrica.</param>
/// <param name="Instance">L'istanza, quando la metrica ne ha piu' d'una.</param>
/// <param name="Da">L'inizio della finestra.</param>
/// <param name="Risoluzione">"raw", "1m", "5m". <b>Mai "auto"</b>: vedi le note.</param>
/// <remarks>
/// La risoluzione va sempre dichiarata. Con "auto" il servizio sceglie in base all'ampiezza
/// della finestra, e su un'ora sceglie il grezzo: tremilaseicento punti per disegnarne
/// sessanta, cioe' mezzo megabyte sul filo a ogni ricarica per buttarne via il 98 per cento.
/// </remarks>
public sealed record HistoryQuery(
    string Collector,
    string Metric,
    string? Instance,
    DateTimeOffset Da,
    string Risoluzione);

/// <summary>Un intervallo dello storico, come arriva dal servizio.</summary>
/// <param name="Timestamp">L'inizio dell'intervallo.</param>
/// <param name="Count">Quanti campioni ci sono caduti dentro.</param>
/// <param name="Avg">La media dei campioni presenti.</param>
/// <param name="Min">Il minimo.</param>
/// <param name="Max">Il massimo.</param>
/// <param name="Last">L'ultimo campione dell'intervallo.</param>
/// <remarks>
/// <b>Gli intervalli senza campioni non arrivano affatto</b>: non esiste un punto con
/// <c>Count</c> a zero. Chi disegna deve costruire la propria griglia dei tempi e cercarci
/// dentro questi punti — vedi <see cref="HistoryStrip"/>.
/// </remarks>
public sealed record HistoryPoint(
    DateTimeOffset Timestamp,
    int Count,
    double Avg,
    double Min,
    double Max,
    double Last);

/// <summary>La risposta di /metrics/history, come arriva sul filo.</summary>
/// <param name="Resolution">La risoluzione effettivamente usata.</param>
/// <param name="BucketSeconds">Quanti secondi copre un intervallo.</param>
/// <param name="Truncated">Vero quando il servizio ha tagliato i punti piu' vecchi.</param>
/// <param name="Points">Gli intervalli che hanno almeno un campione.</param>
public sealed record HistoryResponse(
    string Resolution,
    int BucketSeconds,
    bool Truncated,
    IReadOnlyList<HistoryPoint> Points);

/// <summary>L'esito di una lettura dello storico.</summary>
/// <param name="Outcome">Com'e' andata.</param>
/// <param name="Problem">Che cosa dire a chi guarda, quando e' andata male.</param>
/// <param name="Points">I punti, quando e' andata bene.</param>
public sealed record HistoryFetch(
    ServiceOutcome Outcome,
    string Problem,
    IReadOnlyList<HistoryPoint>? Points);

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
    // Sei secondi, e il numero e' misurato, non scelto. I 100 predefiniti di HttpClient
    // lascerebbero la finestra ferma senza spiegazione per un minuto e mezzo; ma il limite
    // dal basso non e' il periodo di campionamento, e' QUANTO COSTA UN RIFIUTO.
    //
    // Su Windows, .NET 10, sei giri per indirizzo: una connessione rifiutata impiega
    // 2018-2104 ms su 127.0.0.1, su [::1] e sull'indirizzo di rete di questa macchina — non
    // e' una stranezza del loopback, e' il costo di un rifiuto. Un NOME a doppia pila li
    // paga due volte, perche' .NET prova un indirizzo dopo l'altro: con i 3 secondi di prima,
    // "localhost" su porta chiusa dava 3007-3034 ms e il rifiuto non arrivava mai — la
    // finestra diceva "nessuna risposta, controlla il firewall" di un servizio spento, che e'
    // esattamente il consiglio sbagliato. Sei secondi coprono due famiglie di indirizzi con
    // un paio di secondi di margine, e la barra rossa arriva comunque alla tolleranza dei 10.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient http;
    private readonly AuthenticationHeaderValue? authorization;

    /// <summary>Il confronto sull'impronta, oppure null se questo punto non ne ha una.</summary>
    private readonly CertificatePinning? fissaggio;

    /// <summary>Costruisce il client sul punto letto dalla configurazione.</summary>
    /// <param name="endpoint">Il servizio da interrogare.</param>
    public MetricsClient(ObserverEndpoint endpoint)
        : this(endpoint, FissaggioPer(endpoint))
    {
    }

    private MetricsClient(ObserverEndpoint endpoint, CertificatePinning? fissaggio)
        : this(endpoint, fissaggio?.Handler() ?? HandlerPer(endpoint), disposeHandler: true)
    {
        this.fissaggio = fissaggio;
    }

    private static CertificatePinning? FissaggioPer(ObserverEndpoint endpoint) =>
        endpoint.Fingerprint is { Length: > 0 } impronta ? new CertificatePinning(impronta) : null;

    /// <summary>Costruisce il client su un handler fornito da fuori. Serve ai test.</summary>
    /// <param name="endpoint">Il servizio da interrogare.</param>
    /// <param name="handler">L'handler da usare.</param>
    public MetricsClient(ObserverEndpoint endpoint, HttpMessageHandler handler)
        : this(endpoint, handler, disposeHandler: false)
    {
    }

    private MetricsClient(ObserverEndpoint endpoint, HttpMessageHandler handler, bool disposeHandler)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(handler);

        Endpoint = endpoint;

        // Nessun header Authorization sul canale locale, e non e' una svista: mandare il token
        // dove non serve significa continuare a esporlo senza guadagnarci niente.
        authorization = endpoint.ApiToken is { Length: > 0 } token
            ? new AuthenticationHeaderValue("Bearer", token)
            : null;

        http = new HttpClient(handler, disposeHandler) { Timeout = RequestTimeout };
    }

    /// <inheritdoc />
    public ObserverEndpoint Endpoint { get; }

    private Uri BaseAddress => Endpoint.BaseAddress;

    private static SocketsHttpHandler HandlerPer(ObserverEndpoint endpoint) =>
        endpoint.Kind == EndpointKind.Locale
            ? LocalChannelHandler.Crea()
            : new SocketsHttpHandler();

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
                $"The service on {Endpoint.Descrizione} uses data format version " +
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
    public async Task<HistoryFetch> GetHistoryAsync(
        HistoryQuery richiesta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(richiesta);

        // Passa dallo stesso LeggiAsync di latest e catalog, e non e' pigrizia: token,
        // fissaggio dell'impronta, scadenze e traduzione degli errori restano un pezzo di
        // codice solo. Una seconda strada verso il servizio sarebbe una seconda strada da
        // sbagliare, e sbagliarla qui vorrebbe dire spedire il token senza controllare a chi.
        // Tutte le parti sono gia' stringhe, e l'istante e' formattato con "O" e la cultura
        // invariante: qui non passa nessun numero che una cultura possa scrivere diverso.
        string percorso = "metrics/history?collector=" + Uri.EscapeDataString(richiesta.Collector)
            + "&metric=" + Uri.EscapeDataString(richiesta.Metric)
            + "&from=" + Uri.EscapeDataString(richiesta.Da.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
            + "&resolution=" + Uri.EscapeDataString(richiesta.Risoluzione);

        if (!string.IsNullOrEmpty(richiesta.Instance))
        {
            percorso += "&instance=" + Uri.EscapeDataString(richiesta.Instance);
        }

        (ServiceOutcome outcome, string problem, HistoryResponse? risposta) =
            await LeggiAsync<HistoryResponse>(percorso, cancellationToken).ConfigureAwait(false);

        return outcome == ServiceOutcome.Ok && risposta is not null
            ? new HistoryFetch(ServiceOutcome.Ok, string.Empty, risposta.Points)
            : new HistoryFetch(outcome, problem, null);
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
            if (authorization is not null)
            {
                richiesta.Headers.Authorization = authorization;
            }

            using HttpResponseMessage risposta =
                await http.SendAsync(richiesta, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

            string codice = ((int)risposta.StatusCode).ToString(CultureInfo.InvariantCulture);

            if (risposta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (
                    ServiceOutcome.TokenRifiutato,
                    Endpoint.Kind == EndpointKind.Locale
                        ? TestoRifiutoLocale(codice)
                        : TestoTokenRifiutato(codice),
                    null);
            }

            if (risposta.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return (
                    ServiceOutcome.NonAncoraPronto,
                    $"The service on {Endpoint.Descrizione} is listening but hasn't produced its first " +
                    "reading yet. This usually clears on its own after a second or two.",
                    null);
            }

            if (!risposta.IsSuccessStatusCode)
            {
                return (
                    ServiceOutcome.RispostaInattesa,
                    $"The service on {Endpoint.Descrizione} replied {codice} ({risposta.ReasonPhrase}), " +
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
            // Un fallimento di TLS su un punto con impronta fissata NON e' "non raggiungibile",
            // e confonderli sarebbe il peggiore dei due errori: il primo si aspetta, questo no.
            // La macchina risponde eccome - e' l'identita' a non tornare.
            // HaRifiutato e non solo "c'e' un fissaggio": una AuthenticationException puo'
            // arrivare da molti guasti TLS che col certificato non c'entrano, e raccontarli
            // all'utente come "qualcuno si sta mettendo in mezzo" sarebbe un'accusa pesante
            // fatta senza prove.
            if (fissaggio is { HaRifiutato: true } && ex.InnerException is AuthenticationException)
            {
                return (
                    ServiceOutcome.ImprontaNonCorrisponde,
                    fissaggio.Spiegazione(Endpoint.Descrizione),
                    null);
            }

            ServiceOutcome esito = TransportFailure.Classifica(ex);

            return (esito, TestoDiTrasporto(esito, ex.Message), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Scaduto il timeout della richiesta, non una chiusura dell'applicazione: qui
            // distinguere i due casi e' cio' che evita di mostrare un errore mentre si esce.
            return (ServiceOutcome.TempoScaduto, TestoTempoScaduto(), null);
        }
    }

    /// <summary>Sceglie la frase in base a COME il collegamento e' fallito.</summary>
    private string TestoDiTrasporto(ServiceOutcome esito, string dettaglio) => esito switch
    {
        ServiceOutcome.ConnessioneRifiutata => TestoRifiutato(dettaglio),
        ServiceOutcome.TempoScaduto => TestoTempoScaduto(),
        _ => TestoNonRaggiungibile(dettaglio),
    };

    // Un rifiuto e' la risposta piu' informativa che un guasto possa dare: il pacchetto e'
    // arrivato, la macchina ha risposto, e cio' che manca e' soltanto qualcuno in ascolto su
    // quella porta. Dirlo evita di mandare a cercare il firewall, che e' dove porterebbe la
    // frase generica.
    private string TestoRifiutato(string dettaglio) =>
        Endpoint.Kind == EndpointKind.Locale
            ? "The Observer service isn't running on this machine: the local channel refused the " +
              "connection. Start the service, or run \"observer doctor\". Technical detail: " + dettaglio
            : $"{Endpoint.Descrizione} answered, but nothing is listening on port " +
              Endpoint.BaseAddress.Port.ToString(CultureInfo.InvariantCulture) +
              ". The machine is reachable, so Observer is stopped there or it is on another port. " +
              $"Technical detail: {dettaglio}";

    // Il gemello opposto, ed e' il caso che e' costato un pomeriggio. Un servizio spento
    // RIFIUTA, quindi il silenzio parla d'altro: una macchina spenta, oppure qualcosa che
    // scarta i pacchetti. Su Windows la regola del firewall vale su un profilo per volta, e
    // una macchina in dominio su una rete di casa la classifica come pubblica.
    private string TestoTempoScaduto() =>
        Endpoint.Kind == EndpointKind.Locale
            ? "The Observer service on this machine didn't answer within " +
              RequestTimeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) +
              " seconds. It is listening but not replying: run \"observer doctor\"."
            : $"{Endpoint.Descrizione} didn't answer within " +
              RequestTimeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) +
              " seconds, and nothing refused the connection either. Either that machine is off, " +
              "or something is dropping the packets: check that inbound TCP " +
              Endpoint.BaseAddress.Port.ToString(CultureInfo.InvariantCulture) +
              " is allowed there, on the profile that network is classified as.";

    private string TestoNonRaggiungibile(string dettaglio) =>
        Endpoint.Kind == EndpointKind.Locale
            ? "The Observer service isn't answering on this machine. Check that it is running, " +
              "or start it by hand. Technical detail: " + dettaglio
            : $"Can't reach the service on {Endpoint.Descrizione}. Check that the machine is on, " +
              "that Observer is running there, and that the address is correct. " +
              $"Technical detail: {dettaglio}";

    private string TestoRispostaIlleggibile(string dettaglio) =>
        $"{Endpoint.Descrizione} responded, but not with a sample this application can read. " +
        $"It probably isn't Observer. Technical detail: {dettaglio}";

    /// <summary>Il 401 sul canale locale: non c'e' alcun token da correggere.</summary>
    /// <remarks>
    /// Il testo del percorso remoto manderebbe l'utente a cercare un token che sulla propria
    /// macchina non esiste. Questo caso non dovrebbe accadere: quando accade, il posto giusto
    /// dove guardare e' la diagnosi del servizio, non un file di configurazione.
    /// </remarks>
    private static string TestoRifiutoLocale(string codice) =>
        $"The Observer service on this machine refused the request ({codice}), even though it " +
        "came in on the local channel. It should not: the service serves local, identified " +
        "callers without any credential. Run \"observer doctor\" to see what it reports.";

    private string TestoTokenRifiutato(string codice) =>
        $"The service on {Endpoint.Descrizione} rejected the token ({codice}). The token in use " +
        $"comes {Endpoint.Origine}, and it has to be the one that machine reports when you run " +
        "\"observer share\" on it.";
}