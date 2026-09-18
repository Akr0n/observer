using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// Where the client takes its address and token from.
/// </summary>
/// <remarks>
/// The part that decides is a PURE function of its inputs: it reads neither the environment nor
/// the disk, so it is checked with a test instead of by starting the application and watching it.
/// </remarks>
public class ClientConfigurationTests
{
    private const string Fingerprint = "sha256:ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB";

    [Fact]
    public void NoConfiguration_WatchesTheMachineYouAreON()
    {
        // The case of a freshly installed machine. This used to be "Configuration missing", and
        // it asked for a token the local service does not even require.
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
        // Same reason it wins in the service: an old value forgotten in the file would silently
        // overwrite the new one just exported, and the symptom would be an inexplicable 401.
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
        // Without it, Uri would resolve "metrics/latest" by dropping the last segment of an
        // address such as "http://host:5057/observer/", and the request would end up elsewhere.
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
        // Meaning it watches the machine you are on: that is the useful behaviour, not an error.
        ClientConfigurationResult result = ClientConfiguration.Resolve(null, null, null, "   ");

        Assert.Null(result.Problem);
        Assert.Equal(EndpointKind.Local, result.Endpoint!.Kind);
    }

    [Fact]
    public void TheFilePathLIVESOutsideTheRepository()
    {
        // That way a token cannot end up in a commit. And in LocalApplicationData, not in
        // Roaming: on a domain machine Roaming syncs with a file server, and a secret tied to
        // ONE machine must not follow the user from one computer to another.
        Assert.Contains("Observer", ClientConfiguration.FilePath, StringComparison.Ordinal);
        Assert.EndsWith("client.json", ClientConfiguration.FilePath, StringComparison.Ordinal);
    }
}