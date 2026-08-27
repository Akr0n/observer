namespace Observer.App.Services;

/// <summary>Da dove il client raggiunge un servizio Observer.</summary>
/// <remarks>
/// Il valore ZERO e' <see cref="Locale"/>: il canale locale non porta segreti, quindi se un
/// campo dimenticato deve valere qualcosa, che valga quello che non puo' perdere nulla.
/// </remarks>
public enum EndpointKind
{
    /// <summary>La macchina su cui gira questa dashboard, dal canale locale. Nessun token.</summary>
    Locale = 0,

    /// <summary>Un altro computer, via rete. Serve il token di quella macchina.</summary>
    Remoto,
}

/// <summary>Un servizio Observer da interrogare.</summary>
/// <param name="Kind">Se e' la macchina locale o un altro computer.</param>
/// <param name="BaseAddress">
/// Radice del servizio, sempre con la barra finale: senza, <see cref="Uri"/> risolverebbe
/// "metrics/latest" cancellando l'ultimo segmento di un indirizzo tipo
/// "http://host:5057/observer/". Per il canale locale e' un host FITTIZIO: la connessione la
/// apre il ConnectCallback, e questo valore finisce solo nell'header Host.
/// </param>
/// <param name="ApiToken">Il token, solo per i punti remoti. Null sul canale locale.</param>
/// <param name="Origine">Da dove arriva la configurazione, senza il token dentro.</param>
public sealed record ObserverEndpoint(
    EndpointKind Kind,
    Uri BaseAddress,
    string? ApiToken,
    string Origine)
{
    /// <summary>Il nome del canale locale, uguale al valore predefinito del servizio.</summary>
    public const string NomeCanaleLocale = "Observer";

    /// <summary>Il percorso del socket unix, uguale al valore predefinito del servizio.</summary>
    public const string PercorsoSocketLocale = "/run/observer/observer.sock";

    /// <summary>La macchina su cui gira questa dashboard.</summary>
    /// <returns>Il punto locale.</returns>
    public static ObserverEndpoint CanaleLocale() =>
        new(
            EndpointKind.Locale,
            // Host fittizio sotto .invalid, che per definizione non risolve mai: rende
            // esplicito che nessuno deve provare a risolverlo.
            new Uri("http://observer-local.invalid/"),
            null,
            "the local channel on this machine");

    /// <summary>Un altro computer.</summary>
    /// <param name="indirizzo">La radice del servizio remoto.</param>
    /// <param name="token">Il token di quella macchina.</param>
    /// <param name="origine">Da dove arriva la configurazione.</param>
    /// <returns>Il punto remoto.</returns>
    public static ObserverEndpoint Remoto(Uri indirizzo, string token, string origine) =>
        new(EndpointKind.Remoto, indirizzo, token, origine);

    /// <summary>Come si chiama questo punto a schermo.</summary>
    /// <remarks>
    /// Sul canale locale non nomina alcun token, perche' li' non ne esiste uno: dirlo
    /// manderebbe l'utente a cercare una credenziale che non serve.
    /// </remarks>
    public string Descrizione =>
        Kind == EndpointKind.Locale
            ? "this machine"
            : BaseAddress.ToString();

    /// <summary>
    /// Nasconde il token. I record generano un ToString() con TUTTE le proprieta' dentro:
    /// senza questo override basterebbe un binding distratto o una riga di log per stampare
    /// il segreto sullo schermo di chi passa.
    /// </summary>
    /// <returns>Una descrizione senza segreti dentro.</returns>
    public override string ToString() =>
        FormattableString.Invariant($"ObserverEndpoint {{ {Kind}, {Descrizione}, {Origine} }}");
}