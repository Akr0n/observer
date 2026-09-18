using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// Where the client goes looking for the service, and with which credential.
/// </summary>
/// <remarks>
/// It is the piece that makes the dashboard installable: on a freshly installed machine there is
/// no configuration at all, and without this behaviour the window would open on
/// "Configuration missing", asking for a token the service does not even require.
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
        // The local channel carries no secrets: if a forgotten field has to mean something, let
        // it mean the one that cannot leak anything.
        Assert.Equal(EndpointKind.Local, default(EndpointKind));
    }

    [Fact]
    public void AConfiguredADDRESSMakesTheEndpointREMOTE()
    {
        ClientConfigurationResult result = ClientConfiguration.Resolve("a-token", "https://other-machine:5058/", Fingerprint, null);

        Assert.Null(result.Problem);
        Assert.NotNull(result.Endpoint);
        Assert.Equal(EndpointKind.Remote, result.Endpoint.Kind);
        Assert.Equal("a-token", result.Endpoint.ApiToken);
    }

    [Fact]
    public void ARemoteAddressWITHOUTATokenIsRefused()
    {
        // Pointing at another machine with no credential is not a case to be guessed at: that
        // service will refuse every request, and saying so at once beats a burst of 401s.
        ClientConfigurationResult result = ClientConfiguration.Resolve(null, "https://other-machine:5058/", Fingerprint, null);

        Assert.Null(result.Endpoint);
        Assert.False(string.IsNullOrWhiteSpace(result.Problem));
    }

    [Fact]
    public void ATOKENWithoutAnAddressStaysLOCAL_butIsNotUsed()
    {
        // A token exported by mistake must not divert the client away from the machine it is on.
        ClientConfigurationResult result = ClientConfiguration.Resolve("a-token", null, null, null);

        Assert.NotNull(result.Endpoint);
        Assert.Equal(EndpointKind.Local, result.Endpoint.Kind);
        Assert.Null(result.Endpoint.ApiToken);
    }

    [Fact]
    public void TheLocalEndpointHasNoNETWORKAddress()
    {
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();

        // The host is a fake one and must not resolve: the connection is made by the
        // ConnectCallback, and the host only ends up in the Host header.
        Assert.EndsWith(".invalid/", local.BaseAddress.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEndpointNEVERPrintsItsOwnToken()
    {
        // Records generate a ToString with ALL the properties in it: without an override, one
        // careless binding or one log line would be enough to put the secret on screen.
        ObserverEndpoint remoteEndpoint = ObserverEndpoint.Remote(
            new Uri("http://other:5057/"), "TOPSECRET", "from the test");

        Assert.DoesNotContain("TOPSECRET", remoteEndpoint.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLocalEndpointDescribesItselfWithoutMentioningAToken()
    {
        // It ends up in the window's heading: it has to say where you are looking, not mention
        // a credential that does not exist there.
        string description = ObserverEndpoint.LocalChannel().Description;

        Assert.False(string.IsNullOrWhiteSpace(description));
        Assert.DoesNotContain("token", description, StringComparison.OrdinalIgnoreCase);
    }
}