using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>La versione nella barra del titolo.</summary>
/// <remarks>
/// La parte che puo' mentire e' il taglio: l'hash del commit va via, un suffisso di pre-release
/// no, e una versione assente non deve produrre un titolo con uno spazio penzolante.
/// </remarks>
public class AppVersionTests
{
    [Theory]
    [InlineData("0.8.0+7c65549abcdef", "0.8.0")]
    [InlineData("0.8.0", "0.8.0")]
    [InlineData("1.2.3-beta.1+abc", "1.2.3-beta.1")]
    [InlineData(" 0.8.0 ", "0.8.0")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void LaVersioneCortaTieneCioCheStaPrimaDelPiu(string? informativa, string attesa) =>
        Assert.Equal(attesa, AppVersion.Corta(informativa));

    [Fact]
    public void QuestoProgrammaHaUnaVersioneCheSembraUnaVersione()
    {
        // I metadati arrivano da Directory.Build.props attraverso l'SDK: se questo test
        // fallisce, il titolo della finestra dira' "Observer" e basta, e nessuno se ne accorge.
        string versione = AppVersion.DiQuestoProgramma();

        Assert.Matches(@"^\d+\.\d+\.\d+", versione);
        Assert.DoesNotContain("+", versione, StringComparison.Ordinal);
    }

    [Fact]
    public void IlTitoloDellaFinestraPortaNomeEVersione()
    {
        MainViewModel viewModel = new(client: null, problemaDiConfigurazione: null);

        Assert.StartsWith("Observer ", viewModel.TitoloFinestra, StringComparison.Ordinal);
        Assert.EndsWith(AppVersion.DiQuestoProgramma(), viewModel.TitoloFinestra, StringComparison.Ordinal);
    }

    [Fact]
    public void SenzaVersioneIlTitoloEIlSoloNome() =>
        Assert.Equal("Observer", MainViewModel.Titolo(string.Empty));
}