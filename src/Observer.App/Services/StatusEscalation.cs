namespace Observer.App.Services;

/// <summary>
/// Quanto e' grave cio' che la barra di stato sta dicendo. Governa solo il colore.
/// </summary>
/// <remarks>
/// Enum proprio invece di quello di FluentAvalonia, per la stessa ragione di
/// <see cref="MetricSeverity"/>: la decisione e' logica di presentazione pura e va provata
/// senza tirarsi dietro una libreria di controlli. La traduzione in colore la fa il view model.
/// </remarks>
public enum StatusTone
{
    /// <summary>Sta succedendo qualcosa di normale. Neutro.</summary>
    Informational = 0,

    /// <summary>Qualcosa non torna, ma il servizio risponde ancora.</summary>
    Warning = 1,

    /// <summary>DisconnectedSubheading vero: quello che merita il rosso.</summary>
    Error = 2,
}

/// <summary>
/// Cosa mostrare quando una lettura non e' andata a buon fine.
/// </summary>
/// <param name="Tone">Severity', cioe' il colore della barra.</param>
/// <param name="Title">Titolo della barra.</param>
/// <param name="Text">Testo della barra.</param>
/// <param name="Subheading">La riga sotto il title della finestra.</param>
public sealed record StatusMessage(StatusTone Tone, string Title, string Text, string Subheading);

/// <summary>
/// Decide se un guasto e' ancora normale oppure e' diventato un errore.
/// </summary>
/// <remarks>
/// La regola: <b>la gravita' dipende da quanto DURA il guasto, non dal singolo tentativo
/// andato male.</b> Senza, la finestra si apriva rossa su ogni macchina appena installata,
/// perche' il primo tentativo cadeva mentre il servizio stava ancora partendo — e l'errore
/// spariva da solo un attimo dopo. Un allarme che si spegne da solo insegna a ignorare anche
/// quelli veri.
/// <para>
/// L'attesa vale solo dove aspettare puo' cambiare l'outcome: un servizio che non risponde
/// ancora, un servizio che non ha ancora campionato. Un token sbagliato o una versione
/// incompatibile saranno identici fra un minuto, quindi si dicono subito.
/// </para>
/// </remarks>
public static class StatusEscalation
{
    /// <summary>
    /// Quanto si aspetta prima di chiamare guasto un servizio che non risponde.
    /// </summary>
    /// <remarks>
    /// Misurato su questa macchina, servizio avviato a mano e gia' scaldato: dall'avvio del
    /// processo alla prima risposta 200 su <c>/metrics/latest</c> passano 0,9-1,4 secondi su
    /// tre giri. Su una macchina appena installata il costo e' piu' alto — cache dei file
    /// fredda, antivirus che scandisce i binari appena scritti, avvio mediato dal gestore dei
    /// servizi — e dieci secondi lasciano un margine largo senza far sembrare la finestra
    /// bloccata a chi apre la dashboard su una macchina dove il servizio non c'e'.
    /// </remarks>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Traduce un outcome in cio' che va scritto a schermo.
    /// </summary>
    /// <param name="outcome">Come e' andata l'ultima lettura.</param>
    /// <param name="problem">La frase gia' pronta prodotta dal client.</param>
    /// <param name="failingFor">Da quanto tempo le letture falliscono di fila.</param>
    /// <param name="endpoint">Il servizio interrogato.</param>
    /// <param name="hasValuesOnScreen">
    /// True se a schermo ci sono gia' dei valori, che restano li' ma sono fermi.
    /// </param>
    /// <returns>Titolo, testo, gravita' e riga sotto il title.</returns>
    public static StatusMessage MessageFor(
        ServiceOutcome outcome,
        string problem,
        TimeSpan failingFor,
        ObserverEndpoint endpoint,
        bool hasValuesOnScreen)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        bool withinGrace = failingFor < GracePeriod;

        return outcome switch
        {
            // I tre modi di non ottenere risposta meritano la stessa attesa: appena avviata,
            // una macchina RIFIUTA la connessione perche' la porta non e' ancora aperta, e la
            // accetta poco dopo. Distinguerli serve quando il guasto dura, non durante l'avvio.
            ServiceOutcome.Unreachable or ServiceOutcome.ConnectionRefused
                or ServiceOutcome.TimedOut when withinGrace => new StatusMessage(
                StatusTone.Informational,
                "Connecting",
                // Di una macchina REMOTA non si sa se stia partendo: sarebbe un'affermazione
                // che da qui non si puo' fare. Si dice cio' che si sta facendo, e basta.
                endpoint.Kind == EndpointKind.Local
                    ? "Waiting for the Observer service on this machine to answer. It may still be starting up."
                    : $"Contacting {endpoint.Description}…",
                WaitingSubheading(hasValuesOnScreen)),

            ServiceOutcome.NotReadyYet when withinGrace => new StatusMessage(
                StatusTone.Informational,
                "Service is starting",
                problem,
                WaitingSubheading(hasValuesOnScreen)),

            // Il servizio risponde: non e' irraggiungibile, ma non sta nemmeno campionando.
            // Restare "Service is starting" per sempre, con un testo che promette che si
            // risolve da solo, sarebbe una bugia che nessuno smentisce mai.
            ServiceOutcome.NotReadyYet => new StatusMessage(
                StatusTone.Warning,
                "No readings yet",
                $"The service on {endpoint.Description} is answering, but it still hasn't produced a " +
                "reading. Sampling is not working there: run \"observer doctor\" on that machine to " +
                "see what it reports.",
                DisconnectedSubheading(hasValuesOnScreen)),

            // Rifiuto e silenzio non sono sinonimi di "irraggiungibile", ed e' tutto il endpoint:
            // al primo si risponde avviando un servizio, al secondo aprendo una porta. Un solo
            // title per entrambi obbligava chi guarda a indovinare quale dei due fosse.
            ServiceOutcome.ConnectionRefused => ErrorMessage("Service not running", problem, hasValuesOnScreen),
            ServiceOutcome.TimedOut => ErrorMessage("No answer", problem, hasValuesOnScreen),

            ServiceOutcome.Unreachable => ErrorMessage("Service unreachable", problem, hasValuesOnScreen),
            ServiceOutcome.TokenRejected => ErrorMessage("Token rejected", problem, hasValuesOnScreen),
            ServiceOutcome.IncompatibleVersion => ErrorMessage("Version mismatch", problem, hasValuesOnScreen),
            ServiceOutcome.UnreadableResponse => ErrorMessage("Unrecognized response", problem, hasValuesOnScreen),

            // Questi due finivano sotto il title generico, e non per una decisione: erano
            // semplicemente scivolati nell'arm di scarto. Un certificato cambiato in
            // particolare merita di dirsi, perche' e' il solo guasto qui dentro a cui NON
            // conviene rispondere riprovando.
            ServiceOutcome.UnexpectedResponse => ErrorMessage("Unexpected reply", problem, hasValuesOnScreen),
            ServiceOutcome.FingerprintMismatch => ErrorMessage("Certificate changed", problem, hasValuesOnScreen),

            _ => ErrorMessage("Reading failed", problem, hasValuesOnScreen),
        };
    }

    private static StatusMessage ErrorMessage(string title, string problem, bool hasValuesOnScreen) =>
        new(StatusTone.Error, title, problem, DisconnectedSubheading(hasValuesOnScreen));

    private static string WaitingSubheading(bool hasValuesOnScreen) =>
        hasValuesOnScreen
            ? "Reconnecting: the values shown are the last successful reading."
            : "Connecting…";

    // I valori restano a schermo apposta: cancellarli farebbe credere che la macchina abbia
    // smesso di avere una CPU. Questa riga e' cio' che impedisce di leggerli come attuali.
    private static string DisconnectedSubheading(bool hasValuesOnScreen) =>
        hasValuesOnScreen
            ? "Not connected: the values shown are the last successful reading."
            : "Not connected.";
}