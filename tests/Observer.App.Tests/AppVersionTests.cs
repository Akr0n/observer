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
    public void TheShortVersionKeepsWhatComesBeforeThePlus(string? informationalVersion, string expected) =>
        Assert.Equal(expected, AppVersion.Shorten(informationalVersion));

    [Fact]
    public void ThisProgramHasAVersionThatLooksLikeAVersion()
    {
        // I metadati arrivano da Directory.Build.props attraverso l'SDK: se questo test
        // fallisce, il titolo della finestra dira' "Observer" e basta, e nessuno se ne accorge.
        string version = AppVersion.OfThisProgram();

        Assert.Matches(@"^\d+\.\d+\.\d+", version);
        Assert.DoesNotContain("+", version, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWindowTitleCarriesNameAndVersion()
    {
        MainViewModel viewModel = new(client: null, configurationProblem: null);

        Assert.StartsWith("Observer ", viewModel.WindowTitle, StringComparison.Ordinal);
        Assert.EndsWith(AppVersion.OfThisProgram(), viewModel.WindowTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoVersionTheTitleIsJustTheName() =>
        Assert.Equal("Observer", MainViewModel.Title(string.Empty));
}