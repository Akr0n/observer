using Avalonia.Styling;
using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>
/// The Theme and Zoom selectors: the entries they offer, and what they ask of the window.
/// </summary>
/// <remarks>
/// Neither is applied by the view model - the theme by the application, the zoom by the window's
/// layout transform - and neither can be tested without a window. Here everything that comes
/// before that is tested: the allowed keys and the allowed zoom levels, the label a screen reader
/// announces, and the key -> variant translation, which is the one place where a typo would
/// leave a light window to whoever asked for the dark one with no test saying so.
/// </remarks>
public class ThemeAndZoomSelectorTests
{
    [Theory]
    [InlineData("system", "System")]
    [InlineData("light", "Light")]
    [InlineData("dark", "Dark")]
    public void TheThemeOptionShowsItsLabelNotItsKey(string key, string text) =>
        Assert.Equal(text, new ThemeOption(key).ToString());

    [Fact]
    public void TheKeyMapsToItsVariantAndAnUnknownOneToTheSystemDefault()
    {
        Assert.Equal(ThemeVariant.Light, ThemeOption.VariantFor("light"));
        Assert.Equal(ThemeVariant.Dark, ThemeOption.VariantFor("dark"));
        Assert.Equal(ThemeVariant.Default, ThemeOption.VariantFor("system"));

        // A key that does not exist follows the system, never a variant at random.
        Assert.Equal(ThemeVariant.Default, ThemeOption.VariantFor("black"));
    }

    [Fact]
    public void TheThemeSelectorOffersThreeOptionsInTheRightOrder()
    {
        Assert.Equal(["System", "Light", "Dark"], MainViewModel.ThemeOptions.Select(option => option.ToString()));
        Assert.Equal(new ThemeOption("dark"), new ThemeOption("dark"));
    }

    [Fact]
    public void ChangingThemeOrZoomAlsoNotifiesTheSelectedOption()
    {
        // The drop-down is bound to the ENTRY (SelectedTheme, SelectedScale), but the window
        // writes the key (Theme, Zoom): without the entry's notification, starting with "dark"
        // in the file the window would be dark with the selector stuck on System.
        MainViewModel viewModel = new(client: null, configurationProblem: null);
        List<string> notified = [];
        viewModel.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? string.Empty);

        viewModel.Theme = "dark";
        viewModel.Zoom = 1.3d;

        Assert.Contains(nameof(MainViewModel.Theme), notified);
        Assert.Contains(nameof(MainViewModel.SelectedTheme), notified);
        Assert.Contains(nameof(MainViewModel.Zoom), notified);
        Assert.Contains(nameof(MainViewModel.SelectedScale), notified);
    }

    [Fact]
    public void TheViewModelRejectsAnUnknownThemeAndIgnoresANullSelection()
    {
        MainViewModel viewModel = new(client: null, configurationProblem: null);

        Assert.Equal("system", viewModel.Theme);

        viewModel.Theme = "dark";
        Assert.Equal(new ThemeOption("dark"), viewModel.SelectedTheme);

        viewModel.Theme = "black";
        Assert.Equal("system", viewModel.Theme);

        // The selector can assign null while the list changes: the theme stays as it is.
        viewModel.Theme = "light";
        viewModel.SelectedTheme = null!;
        Assert.Equal("light", viewModel.Theme);
    }

    [Fact]
    public void TheViewModelStartsAtNormalZoomAndOffersEveryLevel()
    {
        // The drop-down shows ScaleOptions, not AllowedZoomLevels: a step lost between the two
        // lists shows up in no other test. And the starting zoom is the normal one, not the
        // first element of the list, which since 0.14.0 is 0.75.
        MainViewModel viewModel = new(client: null, configurationProblem: null);

        Assert.Equal(Preferences.NormalZoom, viewModel.Zoom);
        Assert.Equal(Preferences.AllowedZoomLevels, MainViewModel.ScaleOptions.Select(option => option.Factor));

        // As with the theme: a made-up zoom level is refused, it falls back to normal.
        viewModel.Zoom = 0.5d;
        Assert.Equal(Preferences.NormalZoom, viewModel.Zoom);
    }
}