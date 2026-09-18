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
    private const string Fingerprint = "sha256:ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB";

    [Fact]
    public void NoConfiguration_WatchesTheMachineYouAreON()
    {
        // Il caso di una macchina appena installata. Prima questo era "Configuration missing",
        // e chiedeva un token che il servizio locale non pretende nemmeno.
        ClientConfigurationResult result = ClientConfiguration.Resolve(null, null, null, null);

        Assert.Null(result.Problem);
        Assert.Equal(EndpointKind.Local, result.Endpoint!.Kind);
    }

    [Fact]
    public void AddressAndTokenFromTheENVIRONMENT()
    {
        ClientConfigurationResult result =
            ClientConfiguration.Resolve("dal-ambiente", "https://altra:5058", Fingerprint, null);

        Assert.Null(result.Problem);
        Assert.Equal(EndpointKind.Remote, result.Endpoint!.Kind);
        Assert.Equal("dal-ambiente", result.Endpoint.ApiToken);
        Assert.Equal(new Uri("https://altra:5058/"), result.Endpoint.BaseAddress);
    }

    [Fact]
    public void AddressAndTokenFromTheFILE()
    {
        ClientConfigurationResult result = ClientConfiguration.Resolve(null, null, null, """{ "baseAddress": "https://altra:7000/", "apiToken": "dal-file", "fingerprint": "sha256:ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB" }""");

        Assert.Null(result.Problem);
        Assert.Equal("dal-file", result.Endpoint!.ApiToken);
        Assert.Equal(new Uri("https://altra:7000/"), result.Endpoint.BaseAddress);
    }

    [Fact]
    public void TheENVIRONMENTWinsOverTheFile()
    {
        // Stesso motivo per cui vince nel servizio: un valore vecchio dimenticato nel file
        // sovrascriverebbe in silenzio quello nuovo appena esportato, e il sintomo sarebbe un
        // 401 inspiegabile.
        ClientConfigurationResult result = ClientConfiguration.Resolve("vince-questo",
            "https://vince-questa:9000",
            Fingerprint,
            """{ "baseAddress": "https://vecchia:7000/", "apiToken": "vecchio", "fingerprint": "sha256:ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB" }""");

        Assert.Equal("vince-questo", result.Endpoint!.ApiToken);
        Assert.Equal(new Uri("https://vince-questa:9000/"), result.Endpoint.BaseAddress);
    }

    [Fact]
    public void TheTrailingSlashIsAddedWhenMissing()
    {
        // Senza, Uri risolverebbe "metrics/latest" cancellando l'ultimo segmento di un
        // indirizzo tipo "http://host:5057/observer/", e la richiesta finirebbe altrove.
        ClientConfigurationResult result =
            ClientConfiguration.Resolve("t", "https://altra:5058/observer", Fingerprint, null);

        Assert.Equal(new Uri("https://altra:5058/observer/"), result.Endpoint!.BaseAddress);
    }

    [Fact]
    public void SurroundingSpacesAreTrimmed()
    {
        ClientConfigurationResult result =
            ClientConfiguration.Resolve("  con-spazi  ", "  https://altra:5058  ", Fingerprint, null);

        Assert.Equal("con-spazi", result.Endpoint!.ApiToken);
    }

    [Fact]
    public void AREMOTEAddressWithoutATokenSaysWhatToDo()
    {
        ClientConfigurationResult result = ClientConfiguration.Resolve(null, "https://altra:5058", Fingerprint, null);

        Assert.Null(result.Endpoint);
        Assert.Contains("observer share", result.Problem!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("non-un-indirizzo")]
    [InlineData("ftp://altra:5057")]
    [InlineData("://rotto")]
    public void AnUNUSABLEAddressIsExplained(string address)
    {
        ClientConfigurationResult result = ClientConfiguration.Resolve("t", address, Fingerprint, null);

        Assert.Null(result.Endpoint);
        Assert.False(string.IsNullOrWhiteSpace(result.Problem));
    }

    [Fact]
    public void ABrokenCONFIGURATIONFileIsExplained()
    {
        ClientConfigurationResult result = ClientConfiguration.Resolve(null, null, null, "{ non e' json");

        Assert.Null(result.Endpoint);
        Assert.Contains("isn't valid JSON", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEMPTYFileMeansNoConfigurationAtAll()
    {
        // Cioe' si guarda la macchina su cui si sta: e' il comportamento utile, e non un errore.
        ClientConfigurationResult result = ClientConfiguration.Resolve(null, null, null, "   ");

        Assert.Null(result.Problem);
        Assert.Equal(EndpointKind.Local, result.Endpoint!.Kind);
    }

    [Fact]
    public void TheFilePathLIVESOutsideTheRepository()
    {
        // Cosi' un token non puo' finire in un commit. E in LocalApplicationData e non in
        // Roaming: su una macchina di dominio Roaming si sincronizza con un file server, e un
        // segreto legato a UNA macchina non deve seguire l'utente da un computer all'altro.
        Assert.Contains("Observer", ClientConfiguration.FilePath, StringComparison.Ordinal);
        Assert.EndsWith("client.json", ClientConfiguration.FilePath, StringComparison.Ordinal);
    }
}