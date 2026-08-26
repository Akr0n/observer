using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// Da dove arrivano indirizzo e token. E' la parte che, sbagliata, produce il sintomo piu'
/// difficile da diagnosticare per chi non legge il codice: un 401 che nessuno sa spiegare.
/// </summary>
public class ClientConfigurationTests
{
    [Fact]
    public void Resolve_SenzaTokenNeInAmbienteNeNelFile_NonProduceOpzioniESpiegaCosaFare()
    {
        // Un client che parte comunque e martella il servizio di richieste destinate al 401
        // e' peggio di uno che dice subito cosa manca.
        ClientConfigurationResult risultato = ClientConfiguration.Resolve(null, null, null);

        Assert.Null(risultato.Options);
        Assert.NotNull(risultato.Problem);
        Assert.Contains(ClientConfiguration.TokenVariable, risultato.Problem, StringComparison.Ordinal);
        Assert.Contains(ClientConfiguration.FilePath, risultato.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ConIlSoloTokenInAmbiente_UsaIndirizzoPredefinito()
    {
        ClientConfigurationResult risultato = ClientConfiguration.Resolve("segreto", null, null);

        Assert.Null(risultato.Problem);
        Assert.NotNull(risultato.Options);
        Assert.Equal(new Uri(ClientConfiguration.DefaultBaseAddress), risultato.Options.BaseAddress);
        Assert.Equal("segreto", risultato.Options.ApiToken);
    }

    [Fact]
    public void Resolve_ConTokenSoloNelFile_LoUsa()
    {
        ClientConfigurationResult risultato = ClientConfiguration.Resolve(
            null,
            null,
            """{ "apiToken": "dal-file" }""");

        Assert.NotNull(risultato.Options);
        Assert.Equal("dal-file", risultato.Options.ApiToken);
        Assert.Contains("file", risultato.Options.TokenOrigin, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ConTokenSiaInAmbienteSiaNelFile_VinceLAmbiente()
    {
        // Stessa precedenza del servizio, e per lo stesso motivo: un token vecchio dimenticato
        // nel file sovrascriverebbe in silenzio quello nuovo appena esportato, e il sintomo
        // sarebbe un 401 inspiegabile.
        ClientConfigurationResult risultato = ClientConfiguration.Resolve(
            "dall-ambiente",
            null,
            """{ "apiToken": "dal-file" }""");

        Assert.NotNull(risultato.Options);
        Assert.Equal("dall-ambiente", risultato.Options.ApiToken);
        Assert.Contains(ClientConfiguration.TokenVariable, risultato.Options.TokenOrigin, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ConIndirizzoNelFile_LoUsaEGliAggiungeLaBarraFinale()
    {
        // Senza barra finale, Uri risolverebbe "metrics/latest" cancellando l'ultimo segmento
        // e la richiesta finirebbe su un percorso diverso da quello configurato.
        ClientConfigurationResult risultato = ClientConfiguration.Resolve(
            "segreto",
            null,
            """{ "baseAddress": "http://198.51.100.7:5057" }""");

        Assert.NotNull(risultato.Options);
        Assert.Equal("http://198.51.100.7:5057/", risultato.Options.BaseAddress.AbsoluteUri);
    }

    [Fact]
    public void Resolve_ConIndirizzoInAmbiente_VinceSuQuelloDelFile()
    {
        ClientConfigurationResult risultato = ClientConfiguration.Resolve(
            "segreto",
            "http://198.51.100.9:6000",
            """{ "baseAddress": "http://198.51.100.7:5057" }""");

        Assert.NotNull(risultato.Options);
        Assert.Equal("http://198.51.100.9:6000/", risultato.Options.BaseAddress.AbsoluteUri);
    }

    [Theory]
    [InlineData("non-un-indirizzo")]
    [InlineData("ftp://198.51.100.7")]
    [InlineData("localhost:5057")]
    public void Resolve_ConIndirizzoInutilizzabile_SpiegaInvecediLanciare(string indirizzo)
    {
        ClientConfigurationResult risultato = ClientConfiguration.Resolve("segreto", indirizzo, null);

        Assert.Null(risultato.Options);
        Assert.NotNull(risultato.Problem);
        Assert.Contains(ClientConfiguration.BaseAddressVariable, risultato.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ConFileNonValido_SpiegaCheCosaDeveContenere()
    {
        ClientConfigurationResult risultato = ClientConfiguration.Resolve(null, null, "questo non e' json");

        Assert.Null(risultato.Options);
        Assert.NotNull(risultato.Problem);
        Assert.Contains("apiToken", risultato.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ConTokenFattoDiSoliSpazi_LoTrattaComeAssente()
    {
        ClientConfigurationResult risultato = ClientConfiguration.Resolve("   ", null, null);

        Assert.Null(risultato.Options);
    }

    [Fact]
    public void ObserverClientOptions_NonStampaIlTokenNelToString()
    {
        // I record generano un ToString() con tutte le proprieta' dentro: basterebbe un
        // binding distratto per mostrare il segreto sullo schermo di chi passa.
        ObserverClientOptions opzioni = new(
            new Uri("http://localhost:5057/"),
            "questo-non-deve-comparire",
            "dai test");

        Assert.DoesNotContain("questo-non-deve-comparire", opzioni.ToString(), StringComparison.Ordinal);
    }
}
