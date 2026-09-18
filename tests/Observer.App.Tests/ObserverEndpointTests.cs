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
    private const string Fingerprint = "sha256:ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB";

    [Fact]
    public void WithNOConfigurationAtAllItGoesToTheLOCALChannel()
    {
        ClientConfigurationResult result = ClientConfiguration.Resolve(null, null, null, null);

        Assert.Null(result.Problem);
        Assert.NotNull(result.Endpoint);
        Assert.Equal(EndpointKind.Local, result.Endpoint.Kind);
        Assert.Null(result.Endpoint.ApiToken);
    }

    [Fact]
    public void TheZeroValueOfTheKINDIsTheLocalChannel()
    {
        // Il canale locale non porta segreti: se un campo dimenticato deve valere qualcosa, che
        // valga quello che non puo' perdere nulla.
        Assert.Equal(EndpointKind.Local, default(EndpointKind));
    }

    [Fact]
    public void AConfiguredADDRESSMakesTheEndpointREMOTE()
    {
        ClientConfigurationResult result = ClientConfiguration.Resolve("un-token", "https://altra-macchina:5058/", Fingerprint, null);

        Assert.Null(result.Problem);
        Assert.NotNull(result.Endpoint);
        Assert.Equal(EndpointKind.Remote, result.Endpoint.Kind);
        Assert.Equal("un-token", result.Endpoint.ApiToken);
    }

    [Fact]
    public void ARemoteAddressWITHOUTATokenIsRefused()
    {
        // Puntare a un'altra macchina senza credenziale non e' un caso da indovinare: quel
        // servizio rifiutera' ogni richiesta, e dirlo subito e' meglio che mostrare 401 a raffica.
        ClientConfigurationResult result = ClientConfiguration.Resolve(null, "https://altra-macchina:5058/", Fingerprint, null);

        Assert.Null(result.Endpoint);
        Assert.False(string.IsNullOrWhiteSpace(result.Problem));
    }

    [Fact]
    public void ATOKENWithoutAnAddressStaysLOCAL_butIsNotUsed()
    {
        // Un token esportato per errore non deve dirottare il client dalla macchina su cui sta.
        ClientConfigurationResult result = ClientConfiguration.Resolve("un-token", null, null, null);

        Assert.NotNull(result.Endpoint);
        Assert.Equal(EndpointKind.Local, result.Endpoint.Kind);
        Assert.Null(result.Endpoint.ApiToken);
    }

    [Fact]
    public void TheLocalEndpointHasNoNETWORKAddress()
    {
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();

        // L'host e' fittizio e non deve risolversi: la connessione la fa il ConnectCallback,
        // e l'host finisce solo nell'header Host.
        Assert.EndsWith(".invalid/", local.BaseAddress.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEndpointNEVERPrintsItsOwnToken()
    {
        // I record generano un ToString con TUTTE le proprieta' dentro: senza un override,
        // basterebbe un binding distratto o una riga di log per mostrare il segreto a schermo.
        ObserverEndpoint remoteEndpoint = ObserverEndpoint.Remote(
            new Uri("http://altra:5057/"), "SEGRETISSIMO", "dalla prova");

        Assert.DoesNotContain("SEGRETISSIMO", remoteEndpoint.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLocalEndpointDescribesItselfWithoutMentioningAToken()
    {
        // Finisce nell'intestazione della finestra: deve dire dove si sta guardando, non
        // menzionare una credenziale che li' non esiste.
        string description = ObserverEndpoint.LocalChannel().Description;

        Assert.False(string.IsNullOrWhiteSpace(description));
        Assert.DoesNotContain("token", description, StringComparison.OrdinalIgnoreCase);
    }
}