using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>
/// Cio' che la finestra ricorda di se', e quando deve dimenticarlo.
/// </summary>
/// <remarks>
/// La regola che conta e' quella dello schermo scollegato: una posizione salvata su un monitor
/// che non c'e' piu' riaprirebbe la finestra dove nessuno puo' vederla ne' afferrarla.
/// </remarks>
public class PreferencesTests
{
    private static readonly WindowPlacement.WorkArea PrimaryScreen = new(0, 0, 1920, 1040);

    private static readonly WindowPlacement.WorkArea RightScreen = new(1920, 0, 2560, 1400);

    [Fact]
    public void WithNoFileTheDefaultsApply()
    {
        Assert.Equal(Preferences.Defaults, Preferences.From(null));
        Assert.Equal(Preferences.Defaults, Preferences.From(string.Empty));
        Assert.Null(Preferences.Defaults.Placement);
        Assert.Equal(1.0d, Preferences.Defaults.Zoom);
    }

    [Fact]
    public void ABrokenFileDoesNotStopTheWindow() =>
        Assert.Equal(Preferences.Defaults, Preferences.From("{ questo non e' json"));

    [Fact]
    public void ADisallowedZoomFallsBackToNormal()
    {
        // Un file scritto a mano con 2.7 darebbe una finestra tre volte piu' grande dello
        // schermo, e uno con 0.5 pulsanti da 16 px. Anche un valore FRA due gradini non entra:
        // un intervallo al posto della lista lascerebbe passare 0.9, e la tendina non avrebbe
        // una voce da selezionare. Le scale sono quelle della lista, provata per intero sotto.
        Assert.Equal(1.0d, Preferences.From("""{"textScale": 2.7}""").Zoom);
        Assert.Equal(1.0d, Preferences.From("""{"textScale": 0.5}""").Zoom);
        Assert.Equal(1.0d, Preferences.From("""{"textScale": 0.9}""").Zoom);
        Assert.Equal(1.15d, Preferences.From("""{"textScale": 1.15}""").Zoom);
        Assert.Equal(0.75d, Preferences.From("""{"textScale": 0.75}""").Zoom);
        Assert.Equal(1.0d, Preferences.From("""{}""").Zoom);
    }

    [Fact]
    public void RoundTripThroughJson()
    {
        Preferences original = new(
            new WindowPlacement(192, 100, 900, 700, Maximized: false), 1.3d, "dark", "laptop", "24h");

        Assert.Equal(original, Preferences.From(original.ToJson()));
        Assert.Contains("\"textScale\":1.3", original.ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"theme\":\"dark\"", original.ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"machine\":\"laptop\"", original.ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"historyWindow\":\"24h\"", original.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithoutThePeriodShowsTheLastHourAsBefore()
    {
        // Ogni file scritto prima di questa versione: l'assenza vuol dire l'ora, che e' cio'
        // che quella versione mostrava. Nessuna migrazione.
        Assert.Equal("1h", Preferences.From("""{"theme": "dark"}""").HistoryPeriod);
        Assert.Equal("1h", Preferences.Defaults.HistoryPeriod);
    }

    [Fact]
    public void AnUnknownPeriodFallsBackToTheHourAndCaseIsIgnored()
    {
        Assert.Equal("1h", Preferences.From("""{"historyWindow": "1y"}""").HistoryPeriod);
        Assert.Equal("24h", Preferences.From("""{"historyWindow": "24H"}""").HistoryPeriod);
    }

    [Theory]
    [InlineData("1h", 60, 1)]
    [InlineData("24h", 96, 15)]
    [InlineData("7d", 84, 120)]
    public void EveryPeriodFitsTheStripAndItsBarIsAMultipleOfTheSourceStep(
        string key, int bars, int minutesPerBar)
    {
        // Il vincolo che tiene in piedi la tabella: circa novanta barre su ottocento pixel
        // danno barrette da nove, che e' il minimo per vederle separate. Duemila barre - che
        // e' cio' che darebbero sette giorni al passo della sorgente - sarebbero sotto il
        // pixel, cioe' una striscia che non si puo' leggere.
        HistoryPeriodOption period = new(key);

        Assert.Equal(bars, period.BarCount);
        Assert.Equal(TimeSpan.FromMinutes(minutesPerBar), period.Step);
        Assert.InRange(period.BarCount, 50, 120);

        // E il passo della barra dev'essere un multiplo di quello della sorgente, altrimenti
        // un intervallo conterrebbe un numero di punti diverso da quello vicino.
        Assert.Equal(TimeSpan.Zero, period.Step - (period.SourceStep * (int)(period.Step / period.SourceStep)));
    }

    [Fact]
    public void ThePeriodOptionShowsItsLabelAndItsStripTitle()
    {
        Assert.Equal("1 hour", new HistoryPeriodOption("1h").ToString());
        Assert.Equal("24 hours", new HistoryPeriodOption("24h").ToString());
        Assert.Equal("7 days", new HistoryPeriodOption("7d").ToString());

        Assert.Equal("Last hour", new HistoryPeriodOption("1h").Title);
        Assert.Equal("Last 7 days", new HistoryPeriodOption("7d").Title);
    }

    [Fact]
    public void AFileWithoutTheMachineFieldOpensOnThisComputer()
    {
        // Cioe' ogni file scritto prima di questa versione: l'assenza del campo e' esattamente
        // cio' che si vuole dire con "questo computer", quindi non serve nessuna migrazione.
        Assert.Null(Preferences.From("""{"textScale": 1.15, "theme": "dark"}""").MachineName);
    }

    [Fact]
    public void TheRememberedMachineIsFoundByNameIgnoringSurroundingSpaces()
    {
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint remote = RemoteEndpoint("laptop");

        Assert.Equal(remote, Preferences.RememberedMachine([local, remote], "laptop"));

        // Gli spazi attorno non contano: dentro machines.json il nome arriva grezzo.
        Assert.Equal(remote, Preferences.RememberedMachine([local, remote], "  laptop  "));

        // E non contano NEMMENO dal lato della voce, che e' il caso vero: MachineDirectory
        // passa il nome cosi' com'e' scritto nel file, e una voce " laptop " e' la stessa
        // macchina di "laptop". Senza il Trim da questa parte la si perderebbe.
        ObserverEndpoint withSpaces = ObserverEndpoint.Remote(
            new Uri("https://laptop:5058/"), "token", "machines.json", new string('a', 64), " laptop ");

        Assert.Equal(withSpaces, Preferences.RememberedMachine([local, withSpaces], "laptop"));
    }

    [Fact]
    public void AMissingOrBlankNameFallsBackToThisComputer()
    {
        // La voce puo' essere stata tolta o rinominata: si riparte da questo computer, che e'
        // il posto da cui si era partiti la prima volta. Aprire il vuoto, o lamentarsi di una
        // preferenza, sarebbe peggio del dimenticarla.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint remote = RemoteEndpoint("laptop");

        Assert.Equal(local, Preferences.RememberedMachine([local, remote], "sparita"));
        Assert.Equal(local, Preferences.RememberedMachine([local, remote], null));
        Assert.Equal(local, Preferences.RememberedMachine([local, remote], "   "));
    }

    [Fact]
    public void NameComparisonIsCaseSensitive()
    {
        // Su Linux la credenziale di "Laptop" e quella di "laptop" sono due file diversi,
        // quindi sono due MACCHINE diverse: trattarle come lo stesso nome riaprirebbe l'altra.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint remote = RemoteEndpoint("laptop");

        Assert.Equal(local, Preferences.RememberedMachine([local, remote], "Laptop"));
    }

    [Fact]
    public void WithNoMachinesNothingIsRemembered() =>
        Assert.Null(Preferences.RememberedMachine([], "laptop"));

    // Il NOME e' il quinto parametro: il terzo e' l'origine, cioe' da dove viene la
    // configurazione. Passare il nome li' lascia Nome a null, ed e' proprio il caso della
    // vecchia configurazione a macchina singola: una voce senza nome non si ricorda.
    private static ObserverEndpoint RemoteEndpoint(string name) =>
        ObserverEndpoint.Remote(
            new Uri($"https://{name}:5058/"),
            "token",
            "machines.json",
            new string('a', 64),
            name);

    [Theory]
    [InlineData("""{}""", "system")]
    [InlineData("""{"theme": null}""", "system")]
    [InlineData("""{"theme": "nero"}""", "system")]
    [InlineData("""{"theme": "dark"}""", "dark")]
    [InlineData("""{"theme": "Dark"}""", "dark")]
    [InlineData("""{"theme": "light"}""", "light")]
    public void AnUnknownThemeFallsBackToSystemAndCaseIsIgnored(string json, string expected)
    {
        // Un file vecchio non ha il campo, uno scritto a mano puo' avere di tutto: niente di
        // questo deve fermare la finestra, e le maiuscole si perdonano.
        Assert.Equal(expected, Preferences.From(json).Theme);
        Assert.Equal("system", Preferences.Defaults.Theme);
    }

    [Fact]
    public void APlacementInsideTheScreenIsKept()
    {
        WindowPlacement placement = new(192, 100, 900, 700, Maximized: false);

        Assert.Equal(placement, placement.WithinAnyOf([PrimaryScreen]));
    }

    [Fact]
    public void APlacementOnTheSecondScreenIsKeptWhileThatScreenExists()
    {
        // Il caso vero: portatile con monitor esterno, finestra lasciata sul monitor, e il
        // giorno dopo il monitor non c'e'. Con entrambi gli schermi la posizione vale; con
        // il solo schermo del portatile no.
        WindowPlacement onSecondScreen = new(2400, 200, 900, 700, Maximized: false);

        Assert.Equal(onSecondScreen, onSecondScreen.WithinAnyOf([PrimaryScreen, RightScreen]));
        Assert.Null(onSecondScreen.WithinAnyOf([PrimaryScreen]));
    }

    [Theory]
    [InlineData(1850, 100)]
    [InlineData(100, 980)]
    [InlineData(-500, 100)]
    [InlineData(100, -500)]
    public void APlacementThatLeavesTheGrabbableCornerOffScreenIsForgotten(int x, int y)
    {
        // Non basta che un pixel sia dentro: deve starci l'angolo con la barra del titolo,
        // altrimenti la finestra si vede ma non si puo' spostare.
        WindowPlacement placement = new(x, y, 900, 700, Maximized: false);

        Assert.Null(placement.WithinAnyOf([PrimaryScreen]));
    }

    [Fact]
    public void AWindowTooSmallToBeRealIsForgotten() =>
        Assert.Null(new WindowPlacement(10, 10, 40, 40, Maximized: false).WithinAnyOf([PrimaryScreen]));

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1800, 920, true)]
    [InlineData(1801, 920, false)]
    [InlineData(1800, 921, false)]
    [InlineData(-8, 0, true)]
    [InlineData(-16, 0, true)]
    [InlineData(-17, 0, false)]
    [InlineData(0, -1, false)]
    public void TheGrabbableCornerCheckIsExactAndToleratesTheInvisibleLeftBorder(int x, int y, bool isKept)
    {
        // I bordi esatti, altrimenti un <= che diventa < passerebbe inosservato. A sinistra
        // c'e' una tolleranza: una finestra agganciata al bordo sta a X = -8 per via del
        // bordo invisibile di Windows, e va ricordata. In alto no: (-8, -8) e' una finestra
        // massimizzata, e quella non e' una geometria normale.
        WindowPlacement placement = new(x, y, 900, 700, Maximized: false);

        Assert.Equal(isKept ? placement : null, placement.WithinAnyOf([PrimaryScreen]));
    }

    [Fact]
    public void AWindowExactlyTheMinimumGrabbableSizeIsKept()
    {
        Assert.NotNull(new WindowPlacement(10, 10, 120, 120, Maximized: false).WithinAnyOf([PrimaryScreen]));
        Assert.Null(new WindowPlacement(10, 10, 119, 120, Maximized: false).WithinAnyOf([PrimaryScreen]));
        Assert.Null(new WindowPlacement(10, 10, 120, 119, Maximized: false).WithinAnyOf([PrimaryScreen]));
    }

    [Theory]
    [InlineData(int.MaxValue, 100)]
    [InlineData(int.MaxValue - 100, 100)]
    [InlineData(100, int.MaxValue)]
    [InlineData(100, int.MaxValue - 100)]
    [InlineData(int.MinValue, 100)]
    public void AnAbsurdCoordinateDoesNotOverflow(int x, int y)
    {
        // Un file scritto a mano con x = 2147483647: la vecchia somma X + 120 traboccava in
        // negativo, il confronto passava e la finestra si apriva invisibile - per sempre,
        // perche' alla chiusura si risalvava identica. Anche a cento dal massimo: li' la
        // prima clausola non trabocca ancora, e resta solo la seconda a difendere.
        Assert.Null(new WindowPlacement(x, y, 900, 700, Maximized: false).WithinAnyOf([PrimaryScreen, RightScreen]));
    }

    [Fact]
    public void ClosedWhileNormalItRemembersWhereItWas()
    {
        WindowPlacement now = new(300, 200, 900, 700, Maximized: false);

        Assert.Equal(now, WindowPlacement.AtClose(
            minimized: false, maximized: false, lastNormal: null, saved: null, current: now));
    }

    [Fact]
    public void ClosedWhileMaximizedItRemembersThisSessionsNormalGeometry()
    {
        // Il difetto vero: la finestra di ieri era sul monitor A, oggi la si sposta su B e la
        // si massimizza. Chiudendo si deve ricordare B - l'ultima geometria normale di oggi -
        // e non A, che e' cio' che il file diceva stamattina.
        WindowPlacement yesterday = new(100, 100, 900, 700, Maximized: false);
        WindowPlacement today = new(2400, 200, 900, 700, Maximized: false);
        WindowPlacement fullScreen = new(-8, -8, 1936, 1056, Maximized: false);

        Assert.Equal(today with { Maximized = true }, WindowPlacement.AtClose(
            minimized: false, maximized: true, lastNormal: today, saved: yesterday, current: fullScreen));

        // Senza una geometria di oggi, vale quella di ieri.
        Assert.Equal(yesterday with { Maximized = true }, WindowPlacement.AtClose(
            minimized: false, maximized: true, lastNormal: null, saved: yesterday, current: fullScreen));
    }

    [Fact]
    public void MaximizedWithNoKnownGeometryOnlyTheStateIsRemembered()
    {
        // Primo avvio, subito massimizzata, chiusa: le misure dello schermo intero NON vanno
        // salvate come se fossero una finestra normale. Resta lo stato, con una posizione che
        // nessuno schermo accetta: la finestra riapre dove decide il sistema, ma piena. La
        // geometria e' quella di X11, (0, 0): su Windows sta a (-8, -8) e verrebbe scartata
        // comunque, e un test con quella non distinguerebbe la regola dalla fortuna.
        WindowPlacement fullScreen = new(0, 0, 1920, 1040, Maximized: false);

        WindowPlacement? remembered = WindowPlacement.AtClose(
            minimized: false, maximized: true, lastNormal: null, saved: null, current: fullScreen);

        Assert.NotNull(remembered);
        Assert.True(remembered.Maximized);
        Assert.Null(remembered.WithinAnyOf([PrimaryScreen, RightScreen]));
    }

    [Fact]
    public void MinimizedItRemembersHowItWasBefore()
    {
        WindowPlacement normal = new(300, 200, 900, 700, Maximized: false);
        WindowPlacement offScreen = new(-32000, -32000, 900, 700, Maximized: false);

        // Prima era normale: si ricorda normale, anche se il file diceva a tutto schermo.
        Assert.Equal(normal, WindowPlacement.AtClose(
            minimized: true, maximized: false, lastNormal: normal,
            saved: normal with { Maximized = true }, current: offScreen));

        // Prima era a tutto schermo: si ricorda cosi'.
        Assert.Equal(normal with { Maximized = true }, WindowPlacement.AtClose(
            minimized: true, maximized: true, lastNormal: normal, saved: null, current: offScreen));

        // Non si sa niente: niente da dire, e soprattutto NON la posizione fuori da tutto.
        Assert.Null(WindowPlacement.AtClose(
            minimized: true, maximized: false, lastNormal: null, saved: null, current: offScreen));
    }

    [Fact]
    public void TheZoomOptionReadsAsAPercentage()
    {
        // E' cio' che un lettore di schermo annuncia: "115 %", non "1,15".
        string text = new ZoomOption(1.15d).ToString();

        Assert.Contains("115", text, StringComparison.Ordinal);
        Assert.Contains("%", text, StringComparison.Ordinal);

        // Il double nudo comincia con uno 0 in ogni cultura; il P0 di 0,75 non ne contiene in
        // nessuna delle 889 culture di .NET. Non StartsWith("75"): in turco e' "%75".
        Assert.DoesNotContain("0", new ZoomOption(0.75d).ToString(), StringComparison.Ordinal);
        Assert.Equal(new ZoomOption(1.15d), new ZoomOption(1.15d));
    }

    [Fact]
    public void TheAllowedZoomLevelsAreExactlyTheseSix()
    {
        Assert.Equal(1.0d, Preferences.NormalZoom);

        // La lista esatta e non una regola (ordine crescente, pavimento): con la sola regola
        // togliere 0,85 o 1,5 non faceva fallire niente, provato con i mutanti. Il pavimento
        // e' 0,75 e non scende: e' la scala a cui un controllo Fluent da 32 px e' ancora
        // 24 px, e l'anello di stato tiene il buco (misurato su catture reali).
        Assert.Equal([0.75d, 0.85d, 1.0d, 1.15d, 1.3d, 1.5d], Preferences.AllowedZoomLevels);
    }

    [Fact]
    public void TheAllowedPeriodsAreExactlyTheseThree()
    {
        // La lista esatta, per la stessa ragione delle scale: il vincolo vero - la striscia
        // sta fra 60 e 96 barre - lo prova un altro test, ma con quello SOLO si potrebbe
        // togliere "24h" senza che niente diventi rosso. E il primo e' il predefinito, quindi
        // l'ordine conta: un file senza il campo apre sull'ora, non su una settimana.
        Assert.Equal(["1h", "24h", "7d"], Preferences.AllowedPeriods);

        // 90 giorni NON c'e' pur essendo conservati dal servizio: sarebbero venticinquemila
        // punti, oltre il tetto di una risposta, e alla larghezza necessaria una barra starebbe
        // per un giorno e mezzo. Un grafico che mente e' peggio di un grafico che manca.
        Assert.DoesNotContain("90d", Preferences.AllowedPeriods);
    }

    [Fact]
    public void TheSavedPeriodPreferenceReachesTheSelector()
    {
        // Il ponte fra il file e la tendina: la finestra assegna HistoryPeriod, e da li' devono
        // uscire la voce selezionata, il titolo e il passo giusti. Erano tre proprieta' senza
        // un solo test, e una mutazione in mezzo (SelectedHistoryPeriod che ricade sempre sulla prima
        // voce) lasciava la suite verde con la finestra ferma su un'ora.
        MainViewModel viewModel = new(client: null, configurationProblem: null)
        {
            HistoryPeriod = "7d",
        };

        Assert.Equal("7d", viewModel.SelectedHistoryPeriod.Key);
        Assert.Contains(viewModel.SelectedHistoryPeriod, MainViewModel.HistoryPeriodOptions);
        Assert.Equal(TimeSpan.FromHours(2), viewModel.SelectedHistoryPeriod.Step);

        // E la tendina mostra HistoryPeriodOptions, non AllowedPeriods: una voce persa fra le due
        // liste sarebbe un periodo che si salva e non si sceglie.
        Assert.Equal(Preferences.AllowedPeriods, MainViewModel.HistoryPeriodOptions.Select(option => option.Key));

        // Un periodo inventato nel file non blocca la finestra su una tendina vuota.
        viewModel.HistoryPeriod = "90d";

        Assert.Equal(Preferences.AllowedPeriods[0], viewModel.SelectedHistoryPeriod.Key);
    }
}