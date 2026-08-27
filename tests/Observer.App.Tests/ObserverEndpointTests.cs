using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// Dove il client va a cercare il servizio, e con quale credenziale.
/// </summary>
/// <remarks>
/// E' il pezzo che rende installabile la dashboard: su una macchina appena installata non c'e'
/// alcuna configurazione, e senza questo comportamento la finestra si aprirebbe su
/// "Configuration missing" chiedendo un token che il servizio non pretende nemmeno.
/// </remarks>
public class ObserverEndpointTests
{
    private const string Impronta = "sha256:ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB";

    [Fact]
    public void SenzaALCUNAConfigurazioneSiVaSulCanaleLOCALE()
    {
        ClientConfigurationResult esito = ClientConfiguration.Resolve(null, null, null, null);

        Assert.Null(esito.Problem);
        Assert.NotNull(esito.Endpoint);
        Assert.Equal(EndpointKind.Locale, esito.Endpoint.Kind);
        Assert.Null(esito.Endpoint.ApiToken);
    }

    [Fact]
    public void IlValoreZeroDelGENEREEIlCanaleLocale()
    {
        // Il canale locale non porta segreti: se un campo dimenticato deve valere qualcosa, che
        // valga quello che non puo' perdere nulla.
        Assert.Equal(EndpointKind.Locale, default(EndpointKind));
    }

    [Fact]
    public void UnINDIRIZZOConfiguratoRendeIlPuntoREMOTO()
    {
        ClientConfigurationResult esito = ClientConfiguration.Resolve("un-token", "https://altra-macchina:5058/", Impronta, null);

        Assert.Null(esito.Problem);
        Assert.NotNull(esito.Endpoint);
        Assert.Equal(EndpointKind.Remoto, esito.Endpoint.Kind);
        Assert.Equal("un-token", esito.Endpoint.ApiToken);
    }

    [Fact]
    public void UnINDIRIZZORemotoSENZATokenVieneRifiutato()
    {
        // Puntare a un'altra macchina senza credenziale non e' un caso da indovinare: quel
        // servizio rifiutera' ogni richiesta, e dirlo subito e' meglio che mostrare 401 a raffica.
        ClientConfigurationResult esito = ClientConfiguration.Resolve(null, "https://altra-macchina:5058/", Impronta, null);

        Assert.Null(esito.Endpoint);
        Assert.False(string.IsNullOrWhiteSpace(esito.Problem));
    }

    [Fact]
    public void UnTOKENSenzaIndirizzoRestaSulLOCALE_maSenzaUsarlo()
    {
        // Un token esportato per errore non deve dirottare il client dalla macchina su cui sta.
        ClientConfigurationResult esito = ClientConfiguration.Resolve("un-token", null, null, null);

        Assert.NotNull(esito.Endpoint);
        Assert.Equal(EndpointKind.Locale, esito.Endpoint.Kind);
        Assert.Null(esito.Endpoint.ApiToken);
    }

    [Fact]
    public void IlPuntoLocaleNonHaUnIndirizzoDiRETE()
    {
        ObserverEndpoint locale = ObserverEndpoint.CanaleLocale();

        // L'host e' fittizio e non deve risolversi: la connessione la fa il ConnectCallback,
        // e l'host finisce solo nell'header Host.
        Assert.EndsWith(".invalid/", locale.BaseAddress.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnPuntoNonStampaMAIIlProprioToken()
    {
        // I record generano un ToString con TUTTE le proprieta' dentro: senza un override,
        // basterebbe un binding distratto o una riga di log per mostrare il segreto a schermo.
        ObserverEndpoint remoto = ObserverEndpoint.Remoto(
            new Uri("http://altra:5057/"), "SEGRETISSIMO", "dalla prova");

        Assert.DoesNotContain("SEGRETISSIMO", remoto.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void IlPuntoLocaleSiDescriveSenzaParlareDiToken()
    {
        // Finisce nell'intestazione della finestra: deve dire dove si sta guardando, non
        // menzionare una credenziale che li' non esiste.
        string descrizione = ObserverEndpoint.CanaleLocale().Descrizione;

        Assert.False(string.IsNullOrWhiteSpace(descrizione));
        Assert.DoesNotContain("token", descrizione, StringComparison.OrdinalIgnoreCase);
    }
}