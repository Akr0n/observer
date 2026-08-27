using Observer.Core.Metrics;

namespace Observer.App.Services;

/// <summary>
/// Come e' andata una chiamata al servizio. Ogni ramo corrisponde a una frase DIVERSA da
/// mostrare a schermo: chi guarda la finestra non legge i log, quindi "non riesco a
/// collegarmi" e "il token e' sbagliato" devono restare distinguibili fin qui.
/// </summary>
public enum ServiceOutcome
{
    /// <summary>Nessun esito. Non deve mai spacciarsi per successo.</summary>
    Unknown = 0,

    /// <summary>Risposta valida.</summary>
    Ok = 1,

    /// <summary>Il servizio non risponde: spento, porta sbagliata, rete assente.</summary>
    NonRaggiungibile = 2,

    /// <summary>Il servizio risponde ma rifiuta il token (401 o 403).</summary>
    TokenRifiutato = 3,

    /// <summary>Il servizio e' partito ma non ha ancora prodotto il primo campionamento (503).</summary>
    NonAncoraPronto = 4,

    /// <summary>La risposta e' arrivata ma non e' un campionamento leggibile.</summary>
    RispostaIncomprensibile = 5,

    /// <summary>Il servizio parla una versione del formato che questo client non conosce.</summary>
    VersioneIncompatibile = 6,

    /// <summary>Codice HTTP non previsto.</summary>
    RispostaInattesa = 7,

    /// <summary>Il certificato presentato non e' quello atteso, oppure ne manca l'impronta.</summary>
    /// <remarks>
    /// Tenuto separato da <see cref="NonRaggiungibile"/> di proposito. Sul filo si vede lo
    /// stesso guasto - il collegamento non si stabilisce - ma le due cause chiedono gesti
    /// opposti: la prima si aspetta, questa NO. Un'impronta che cambia e' una reinstallazione
    /// del servizio oppure qualcuno in mezzo, e in nessuno dei due casi conviene riprovare.
    /// </remarks>
    ImprontaNonCorrisponde = 8,
}

/// <summary>
/// Esito della lettura di <c>/metrics/latest</c>.
/// </summary>
/// <param name="Outcome">Come e' andata.</param>
/// <param name="Problem">Frase gia' pronta per lo schermo, vuota quando l'esito e' Ok.</param>
/// <param name="Snapshot">Il campionamento, valorizzato solo quando l'esito e' Ok.</param>
public sealed record SnapshotFetch(ServiceOutcome Outcome, string Problem, MachineSnapshot? Snapshot)
{
    /// <summary>True quando c'e' davvero un campionamento da mostrare.</summary>
    public bool IsOk => Outcome == ServiceOutcome.Ok && Snapshot is not null;
}

/// <summary>
/// Esito della lettura di <c>/metrics/catalog</c>.
/// </summary>
/// <param name="Outcome">Come e' andata.</param>
/// <param name="Problem">Frase gia' pronta per lo schermo, vuota quando l'esito e' Ok.</param>
/// <param name="Catalog">Il catalogo, valorizzato solo quando l'esito e' Ok.</param>
public sealed record CatalogFetch(ServiceOutcome Outcome, string Problem, MetricCatalog? Catalog)
{
    /// <summary>True quando c'e' davvero un catalogo utilizzabile.</summary>
    public bool IsOk => Outcome == ServiceOutcome.Ok && Catalog is not null;
}
