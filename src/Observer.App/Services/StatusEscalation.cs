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

    /// <summary>Guasto vero: quello che merita il rosso.</summary>
    Error = 2,
}

/// <summary>
/// Cosa mostrare quando una lettura non e' andata a buon fine.
/// </summary>
/// <param name="Tone">Gravita', cioe' il colore della barra.</param>
/// <param name="Title">Titolo della barra.</param>
/// <param name="Text">Testo della barra.</param>
/// <param name="Subheading">La riga sotto il titolo della finestra.</param>
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
/// L'attesa vale solo dove aspettare puo' cambiare l'esito: un servizio che non risponde
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
    public static readonly TimeSpan Tolleranza = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Traduce un esito in cio' che va scritto a schermo.
    /// </summary>
    /// <param name="esito">Come e' andata l'ultima lettura.</param>
    /// <param name="problema">La frase gia' pronta prodotta dal client.</param>
    /// <param name="durata">Da quanto tempo le letture falliscono di fila.</param>
    /// <param name="punto">Il servizio interrogato.</param>
    /// <param name="valoriGiaMostrati">
    /// True se a schermo ci sono gia' dei valori, che restano li' ma sono fermi.
    /// </param>
    /// <returns>Titolo, testo, gravita' e riga sotto il titolo.</returns>
    public static StatusMessage Per(
        ServiceOutcome esito,
        string problema,
        TimeSpan durata,
        ObserverEndpoint punto,
        bool valoriGiaMostrati)
    {
        ArgumentNullException.ThrowIfNull(punto);

        bool ancoraInTempo = durata < Tolleranza;

        return esito switch
        {
            ServiceOutcome.NonRaggiungibile when ancoraInTempo => new StatusMessage(
                StatusTone.Informational,
                "Connecting",
                // Di una macchina REMOTA non si sa se stia partendo: sarebbe un'affermazione
                // che da qui non si puo' fare. Si dice cio' che si sta facendo, e basta.
                punto.Kind == EndpointKind.Locale
                    ? "Waiting for the Observer service on this machine to answer. It may still be starting up."
                    : $"Contacting {punto.Descrizione}…",
                Attesa(valoriGiaMostrati)),

            ServiceOutcome.NonAncoraPronto when ancoraInTempo => new StatusMessage(
                StatusTone.Informational,
                "Service is starting",
                problema,
                Attesa(valoriGiaMostrati)),

            // Il servizio risponde: non e' irraggiungibile, ma non sta nemmeno campionando.
            // Restare "Service is starting" per sempre, con un testo che promette che si
            // risolve da solo, sarebbe una bugia che nessuno smentisce mai.
            ServiceOutcome.NonAncoraPronto => new StatusMessage(
                StatusTone.Warning,
                "No readings yet",
                $"The service on {punto.Descrizione} is answering, but it still hasn't produced a " +
                "reading. Sampling is not working there: run \"observer doctor\" on that machine to " +
                "see what it reports.",
                Guasto(valoriGiaMostrati)),

            ServiceOutcome.NonRaggiungibile => Rosso("Service unreachable", problema, valoriGiaMostrati),
            ServiceOutcome.TokenRifiutato => Rosso("Token rejected", problema, valoriGiaMostrati),
            ServiceOutcome.VersioneIncompatibile => Rosso("Version mismatch", problema, valoriGiaMostrati),
            ServiceOutcome.RispostaIncomprensibile => Rosso("Unrecognized response", problema, valoriGiaMostrati),
            _ => Rosso("Reading failed", problema, valoriGiaMostrati),
        };
    }

    private static StatusMessage Rosso(string titolo, string problema, bool valoriGiaMostrati) =>
        new(StatusTone.Error, titolo, problema, Guasto(valoriGiaMostrati));

    private static string Attesa(bool valoriGiaMostrati) =>
        valoriGiaMostrati
            ? "Reconnecting: the values shown are the last successful reading."
            : "Connecting…";

    // I valori restano a schermo apposta: cancellarli farebbe credere che la macchina abbia
    // smesso di avere una CPU. Questa riga e' cio' che impedisce di leggerli come attuali.
    private static string Guasto(bool valoriGiaMostrati) =>
        valoriGiaMostrati
            ? "Not connected: the values shown are the last successful reading."
            : "Not connected.";
}