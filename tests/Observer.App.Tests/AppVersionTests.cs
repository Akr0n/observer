using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>The version in the title bar.</summary>
/// <remarks>
/// The part that can lie is the trimming: the commit hash goes, a pre-release suffix does not,
/// and a missing version must not produce a title with a dangling space.
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
        // The metadata comes from Directory.Build.props through the SDK: if this test fails, the
        // window title will say just "Observer", and nobody notices.
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