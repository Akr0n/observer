using Avalonia.Styling;
using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>
/// Il selettore del tema: le voci, e cosa chiedono all'applicazione.
/// </summary>
/// <remarks>
/// Il tema lo applica l'applicazione, e quello non si prova senza una finestra. Qui si prova
/// tutto cio' che sta prima: le chiavi ammesse, l'etichetta che un lettore di schermo annuncia,
/// e la traduzione chiave -> variante, che e' l'unico punto in cui un refuso lascerebbe una
/// finestra chiara a chi ha chiesto quella scura senza che nessun test lo dica.
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

        // Una chiave che non esiste segue il sistema, mai una variante a caso.
        Assert.Equal(ThemeVariant.Default, ThemeOption.VariantFor("nero"));
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
        // La tendina e' legata alla VOCE (SelectedTheme, SelectedScale), ma la finestra scrive la
        // chiave (Theme, Zoom): senza la notifica della voce, all'avvio con "dark" nel
        // file la finestra sarebbe scura con il selettore fermo su System.
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

        viewModel.Theme = "nero";
        Assert.Equal("system", viewModel.Theme);

        // Il selettore puo' assegnare null mentre cambia elenco: il tema resta com'e'.
        viewModel.Theme = "light";
        viewModel.SelectedTheme = null!;
        Assert.Equal("light", viewModel.Theme);
    }

    [Fact]
    public void TheViewModelStartsAtNormalZoomAndOffersEveryLevel()
    {
        // La tendina mostra ScaleOptions, non AllowedZoomLevels: un gradino perso fra le due liste
        // non si vede in nessun altro test. E la scala di partenza e' quella normale, non il
        // primo elemento della lista, che da 0.14.0 e' 0,75.
        MainViewModel viewModel = new(client: null, configurationProblem: null);

        Assert.Equal(Preferences.NormalZoom, viewModel.Zoom);
        Assert.Equal(Preferences.AllowedZoomLevels, MainViewModel.ScaleOptions.Select(option => option.Factor));

        // Come per il tema: una scala inventata non entra, torna alla normale.
        viewModel.Zoom = 0.5d;
        Assert.Equal(Preferences.NormalZoom, viewModel.Zoom);
    }
}