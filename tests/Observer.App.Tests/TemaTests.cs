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
public class TemaTests
{
    [Theory]
    [InlineData("system", "System")]
    [InlineData("light", "Light")]
    [InlineData("dark", "Dark")]
    public void LaVoceSiLeggeComeSiVede(string chiave, string testo) =>
        Assert.Equal(testo, new OpzioneTema(chiave).ToString());

    [Fact]
    public void LaChiaveTrovaLaSuaVariante()
    {
        Assert.Equal(ThemeVariant.Light, OpzioneTema.Variante("light"));
        Assert.Equal(ThemeVariant.Dark, OpzioneTema.Variante("dark"));
        Assert.Equal(ThemeVariant.Default, OpzioneTema.Variante("system"));

        // Una chiave che non esiste segue il sistema, mai una variante a caso.
        Assert.Equal(ThemeVariant.Default, OpzioneTema.Variante("nero"));
    }

    [Fact]
    public void LeVociDelSelettoreSonoTreNellOrdineGiusto()
    {
        Assert.Equal(["System", "Light", "Dark"], MainViewModel.OpzioniTema.Select(voce => voce.ToString()));
        Assert.Equal(new OpzioneTema("dark"), new OpzioneTema("dark"));
    }

    [Fact]
    public void CambiareTemaOScalaNotificaAncheLaVoceDelSelettore()
    {
        // La tendina e' legata alla VOCE (TemaScelto, ScalaScelta), ma la finestra scrive la
        // chiave (Tema, ScalaTesto): senza la notifica della voce, all'avvio con "dark" nel
        // file la finestra sarebbe scura con il selettore fermo su System.
        MainViewModel viewModel = new(client: null, problemaDiConfigurazione: null);
        List<string> notificate = [];
        viewModel.PropertyChanged += (_, e) => notificate.Add(e.PropertyName ?? string.Empty);

        viewModel.Tema = "dark";
        viewModel.ScalaTesto = 1.3d;

        Assert.Contains(nameof(MainViewModel.Tema), notificate);
        Assert.Contains(nameof(MainViewModel.TemaScelto), notificate);
        Assert.Contains(nameof(MainViewModel.ScalaTesto), notificate);
        Assert.Contains(nameof(MainViewModel.ScalaScelta), notificate);
    }

    [Fact]
    public void IlViewModelNonAccettaUnTemaInventato()
    {
        MainViewModel viewModel = new(client: null, problemaDiConfigurazione: null);

        Assert.Equal("system", viewModel.Tema);

        viewModel.Tema = "dark";
        Assert.Equal(new OpzioneTema("dark"), viewModel.TemaScelto);

        viewModel.Tema = "nero";
        Assert.Equal("system", viewModel.Tema);

        // Il selettore puo' assegnare null mentre cambia elenco: il tema resta com'e'.
        viewModel.Tema = "light";
        viewModel.TemaScelto = null!;
        Assert.Equal("light", viewModel.Tema);
    }

    [Fact]
    public void IlViewModelParteDallaNormaleEOffreTutteLeScale()
    {
        // La tendina mostra OpzioniScala, non ScaleAmmesse: un gradino perso fra le due liste
        // non si vede in nessun altro test. E la scala di partenza e' quella normale, non il
        // primo elemento della lista, che da 0.14.0 e' 0,75.
        MainViewModel viewModel = new(client: null, problemaDiConfigurazione: null);

        Assert.Equal(Preferenze.ScalaNormale, viewModel.ScalaTesto);
        Assert.Equal(Preferenze.ScaleAmmesse, MainViewModel.OpzioniScala.Select(voce => voce.Fattore));

        // Come per il tema: una scala inventata non entra, torna alla normale.
        viewModel.ScalaTesto = 0.5d;
        Assert.Equal(Preferenze.ScalaNormale, viewModel.ScalaTesto);
    }
}