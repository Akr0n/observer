namespace Observer.App.Services;

/// <summary>Da dove il client raggiunge un servizio Observer.</summary>
/// <remarks>
/// Il valore ZERO e' <see cref="Local"/>: il canale locale non porta segreti, quindi se un
/// campo dimenticato deve valere qualcosa, che valga quello che non puo' perdere nulla.
/// </remarks>
public enum EndpointKind
{
    /// <summary>La macchina su cui gira questa dashboard, dal canale locale. Nessun token.</summary>
    Local = 0,

    /// <summary>Un altro computer, via rete. Serve il token di quella macchina.</summary>
    Remote,
}

/// <summary>Un servizio Observer da interrogare.</summary>
/// <param name="Kind">Se e' la macchina locale o un altro computer.</param>
/// <param name="BaseAddress">
/// Radice del servizio, sempre con la barra finale: senza, <see cref="Uri"/> risolverebbe
/// "metrics/latest" cancellando l'ultimo segmento di un baseAddress tipo
/// "http://host:5057/observer/". Per il canale locale e' un host FITTIZIO: la connessione la
/// apre il ConnectCallback, e questo valore finisce solo nell'header Host.
/// </param>
/// <param name="ApiToken">Il token, solo per i punti remoti. Null sul canale locale.</param>
/// <param name="Origin">Da dove arriva la configurazione, senza il token dentro.</param>
/// <param name="Fingerprint">
/// L'fingerprint del certificato che quella macchina DEVE presentare. Null sul canale locale, che
/// non attraversa la rete e non ha niente da cifrare.
/// </param>
/// <param name="Name">
/// Come chiamarla nell'elenco, se chi ha scritto la configurazione le ha dato un name. Null
/// significa "usa l'baseAddress".
/// </param>
public sealed record ObserverEndpoint(
    EndpointKind Kind,
    Uri BaseAddress,
    string? ApiToken,
    string Origin,
    string? Fingerprint = null,
    string? Name = null)
{
    /// <summary>Il name del canale locale, uguale al valore predefinito del servizio.</summary>
    public const string LocalChannelName = "Observer";

    /// <summary>Il percorso del socket unix, uguale al valore predefinito del servizio.</summary>
    public const string LocalSocketPath = "/run/observer/observer.sock";

    /// <summary>La macchina su cui gira questa dashboard.</summary>
    /// <returns>Il punto locale.</returns>
    public static ObserverEndpoint LocalChannel() =>
        new(
            EndpointKind.Local,
            // Host fittizio sotto .invalid, che per definizione non risolve mai: rende
            // esplicito che nessuno deve provare a risolverlo.
            new Uri("http://observer-local.invalid/"),
            null,
            "the local channel on this machine");

    /// <summary>Un altro computer.</summary>
    /// <param name="baseAddress">La radice del servizio remoto.</param>
    /// <param name="token">Il token di quella macchina.</param>
    /// <param name="origin">Da dove arriva la configurazione.</param>
    /// <returns>Il punto remoto.</returns>
    public static ObserverEndpoint Remote(
        Uri baseAddress,
        string token,
        string origin,
        string? fingerprint = null,
        string? name = null) =>
        new(EndpointKind.Remote, baseAddress, token, origin, fingerprint, name);

    /// <summary>Come si chiama questo punto a schermo.</summary>
    /// <remarks>
    /// Sul canale locale non nomina alcun token, perche' li' non ne esiste uno: dirlo
    /// manderebbe l'utente a cercare una credenziale che non serve.
    /// </remarks>
    public string Description =>
        Kind == EndpointKind.Local
            ? "this machine"
            : BaseAddress.ToString();

    /// <summary>Come si chiama questo punto NELL'ELENCO delle macchine.</summary>
    /// <remarks>
    /// Il name scelto a mano vince sull'baseAddress, perche' in una barra laterale
    /// "https://192.168.1.24:5058/" non dice a nessuno di quale macchina si tratti.
    /// </remarks>
    /// <remarks>
    /// Non coincide con <see cref="Description"/>, e la differenza non e' un capriccio: quella
    /// vive DENTRO una frase ("Connected to this machine"), questo e' una voce di elenco a se'
    /// stante e vuole l'iniziale maiuscola.
    /// </remarks>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name)
            ? (Kind == EndpointKind.Local ? "This machine" : Description)
            : Name.Trim();

    /// <summary>Vero quando questo punto viaggia cifrato e con l'fingerprint fissata.</summary>
    public bool IsFingerprintPinned => !string.IsNullOrWhiteSpace(Fingerprint);

    /// <summary>
    /// Nasconde il token. I record generano un ToString() con TUTTE le proprieta' dentro:
    /// senza questo override basterebbe un binding distratto o una riga di log per stampare
    /// il segreto sullo schermo di chi passa.
    /// </summary>
    /// <returns>Una descrizione senza segreti dentro.</returns>
    public override string ToString() =>
        FormattableString.Invariant($"ObserverEndpoint {{ {Kind}, {Description}, {Origin} }}");
}