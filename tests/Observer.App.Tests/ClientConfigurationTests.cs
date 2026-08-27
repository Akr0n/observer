using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// Da dove il client prende indirizzo e token.
/// </summary>
/// <remarks>
/// La parte che decide e' una funzione PURA sui suoi ingressi: non legge ne' ambiente ne'
/// disco, quindi si verifica con un test invece che avviando l'applicazione e guardandola.
/// </remarks>
public class ClientConfigurationTests
{
    private const string Impronta = "sha256:ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB";

    [Fact]
    public void NienteConfigurazione_SiGuardaLaMacchinaSuCuiSiSTA()
    {
        // Il caso di una macchina appena installata. Prima questo era "Configuration missing",
        // e chiedeva un token che il servizio locale non pretende nemmeno.
        ClientConfigurationResult esito = ClientConfiguration.Resolve(null, null, null, null);

        Assert.Null(esito.Problem);
        Assert.Equal(EndpointKind.Locale, esito.Endpoint!.Kind);
    }

    [Fact]
    public void IndirizzoETokenDallAMBIENTE()
    {
        ClientConfigurationResult esito =
            ClientConfiguration.Resolve("dal-ambiente", "https://altra:5058", Impronta, null);

        Assert.Null(esito.Problem);
        Assert.Equal(EndpointKind.Remoto, esito.Endpoint!.Kind);
        Assert.Equal("dal-ambiente", esito.Endpoint.ApiToken);
        Assert.Equal(new Uri("https://altra:5058/"), esito.Endpoint.BaseAddress);
    }

    [Fact]
    public void IndirizzoETokenDalFILE()
    {
        ClientConfigurationResult esito = ClientConfiguration.Resolve(null, null, null, """{ "baseAddress": "https://altra:7000/", "apiToken": "dal-file", "fingerprint": "sha256:ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB" }""");

        Assert.Null(esito.Problem);
        Assert.Equal("dal-file", esito.Endpoint!.ApiToken);
        Assert.Equal(new Uri("https://altra:7000/"), esito.Endpoint.BaseAddress);
    }

    [Fact]
    public void LAMBIENTEVinceSulFile()
    {
        // Stesso motivo per cui vince nel servizio: un valore vecchio dimenticato nel file
        // sovrascriverebbe in silenzio quello nuovo appena esportato, e il sintomo sarebbe un
        // 401 inspiegabile.
        ClientConfigurationResult esito = ClientConfiguration.Resolve("vince-questo",
            "https://vince-questa:9000",
            Impronta,
            """{ "baseAddress": "https://vecchia:7000/", "apiToken": "vecchio", "fingerprint": "sha256:ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB" }""");

        Assert.Equal("vince-questo", esito.Endpoint!.ApiToken);
        Assert.Equal(new Uri("https://vince-questa:9000/"), esito.Endpoint.BaseAddress);
    }

    [Fact]
    public void LaBarraFinaleVieneAggiuntaSeManca()
    {
        // Senza, Uri risolverebbe "metrics/latest" cancellando l'ultimo segmento di un
        // indirizzo tipo "http://host:5057/observer/", e la richiesta finirebbe altrove.
        ClientConfigurationResult esito =
            ClientConfiguration.Resolve("t", "https://altra:5058/observer", Impronta, null);

        Assert.Equal(new Uri("https://altra:5058/observer/"), esito.Endpoint!.BaseAddress);
    }

    [Fact]
    public void GliSpaziVengonoTolti()
    {
        ClientConfigurationResult esito =
            ClientConfiguration.Resolve("  con-spazi  ", "  https://altra:5058  ", Impronta, null);

        Assert.Equal("con-spazi", esito.Endpoint!.ApiToken);
    }

    [Fact]
    public void UnIndirizzoREMOTOSenzaTokenSpiegaCosaFare()
    {
        ClientConfigurationResult esito = ClientConfiguration.Resolve(null, "https://altra:5058", Impronta, null);

        Assert.Null(esito.Endpoint);
        Assert.Contains("observer share", esito.Problem!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("non-un-indirizzo")]
    [InlineData("ftp://altra:5057")]
    [InlineData("://rotto")]
    public void UnIndirizzoINUTILIZZABILEVieneSpiegato(string indirizzo)
    {
        ClientConfigurationResult esito = ClientConfiguration.Resolve("t", indirizzo, Impronta, null);

        Assert.Null(esito.Endpoint);
        Assert.False(string.IsNullOrWhiteSpace(esito.Problem));
    }

    [Fact]
    public void UnFileDiCONFIGURAZIONERottoVieneSpiegato()
    {
        ClientConfigurationResult esito = ClientConfiguration.Resolve(null, null, null, "{ non e' json");

        Assert.Null(esito.Endpoint);
        Assert.Contains("isn't valid JSON", esito.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void UnFileVUOTOEquivaleAllAssenzaDiConfigurazione()
    {
        // Cioe' si guarda la macchina su cui si sta: e' il comportamento utile, e non un errore.
        ClientConfigurationResult esito = ClientConfiguration.Resolve(null, null, null, "   ");

        Assert.Null(esito.Problem);
        Assert.Equal(EndpointKind.Locale, esito.Endpoint!.Kind);
    }

    [Fact]
    public void IlPercorsoDelFileSTAFuoriDalRepository()
    {
        // Cosi' un token non puo' finire in un commit. E in LocalApplicationData e non in
        // Roaming: su una macchina di dominio Roaming si sincronizza con un file server, e un
        // segreto legato a UNA macchina non deve seguire l'utente da un computer all'altro.
        Assert.Contains("Observer", ClientConfiguration.FilePath, StringComparison.Ordinal);
        Assert.EndsWith("client.json", ClientConfiguration.FilePath, StringComparison.Ordinal);
    }
}