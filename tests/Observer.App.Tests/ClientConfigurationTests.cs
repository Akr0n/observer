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
    [Fact]
    public void NienteConfigurazione_SiGuardaLaMacchinaSuCuiSiSTA()
    {
        // Il caso di una macchina appena installata. Prima questo era "Configuration missing",
        // e chiedeva un token che il servizio locale non pretende nemmeno.
        ClientConfigurationResult esito = ClientConfiguration.Resolve(null, null, null);

        Assert.Null(esito.Problem);
        Assert.Equal(EndpointKind.Locale, esito.Endpoint!.Kind);
    }

    [Fact]
    public void IndirizzoETokenDallAMBIENTE()
    {
        ClientConfigurationResult esito =
            ClientConfiguration.Resolve("dal-ambiente", "http://altra:5057", null);

        Assert.Null(esito.Problem);
        Assert.Equal(EndpointKind.Remoto, esito.Endpoint!.Kind);
        Assert.Equal("dal-ambiente", esito.Endpoint.ApiToken);
        Assert.Equal(new Uri("http://altra:5057/"), esito.Endpoint.BaseAddress);
    }

    [Fact]
    public void IndirizzoETokenDalFILE()
    {
        ClientConfigurationResult esito = ClientConfiguration.Resolve(
            null, null, """{ "baseAddress": "http://altra:7000/", "apiToken": "dal-file" }""");

        Assert.Null(esito.Problem);
        Assert.Equal("dal-file", esito.Endpoint!.ApiToken);
        Assert.Equal(new Uri("http://altra:7000/"), esito.Endpoint.BaseAddress);
    }

    [Fact]
    public void LAMBIENTEVinceSulFile()
    {
        // Stesso motivo per cui vince nel servizio: un valore vecchio dimenticato nel file
        // sovrascriverebbe in silenzio quello nuovo appena esportato, e il sintomo sarebbe un
        // 401 inspiegabile.
        ClientConfigurationResult esito = ClientConfiguration.Resolve(
            "vince-questo",
            "http://vince-questa:9000",
            """{ "baseAddress": "http://vecchia:7000/", "apiToken": "vecchio" }""");

        Assert.Equal("vince-questo", esito.Endpoint!.ApiToken);
        Assert.Equal(new Uri("http://vince-questa:9000/"), esito.Endpoint.BaseAddress);
    }

    [Fact]
    public void LaBarraFinaleVieneAggiuntaSeManca()
    {
        // Senza, Uri risolverebbe "metrics/latest" cancellando l'ultimo segmento di un
        // indirizzo tipo "http://host:5057/observer/", e la richiesta finirebbe altrove.
        ClientConfigurationResult esito =
            ClientConfiguration.Resolve("t", "http://altra:5057/observer", null);

        Assert.Equal(new Uri("http://altra:5057/observer/"), esito.Endpoint!.BaseAddress);
    }

    [Fact]
    public void GliSpaziVengonoTolti()
    {
        ClientConfigurationResult esito =
            ClientConfiguration.Resolve("  con-spazi  ", "  http://altra:5057  ", null);

        Assert.Equal("con-spazi", esito.Endpoint!.ApiToken);
    }

    [Fact]
    public void UnIndirizzoREMOTOSenzaTokenSpiegaCosaFare()
    {
        ClientConfigurationResult esito = ClientConfiguration.Resolve(null, "http://altra:5057", null);

        Assert.Null(esito.Endpoint);
        Assert.Contains("observer share", esito.Problem!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("non-un-indirizzo")]
    [InlineData("ftp://altra:5057")]
    [InlineData("://rotto")]
    public void UnIndirizzoINUTILIZZABILEVieneSpiegato(string indirizzo)
    {
        ClientConfigurationResult esito = ClientConfiguration.Resolve("t", indirizzo, null);

        Assert.Null(esito.Endpoint);
        Assert.False(string.IsNullOrWhiteSpace(esito.Problem));
    }

    [Fact]
    public void UnFileDiCONFIGURAZIONERottoVieneSpiegato()
    {
        ClientConfigurationResult esito = ClientConfiguration.Resolve(null, null, "{ non e' json");

        Assert.Null(esito.Endpoint);
        Assert.Contains("isn't valid JSON", esito.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void UnFileVUOTOEquivaleAllAssenzaDiConfigurazione()
    {
        // Cioe' si guarda la macchina su cui si sta: e' il comportamento utile, e non un errore.
        ClientConfigurationResult esito = ClientConfiguration.Resolve(null, null, "   ");

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