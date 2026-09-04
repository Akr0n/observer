using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// Cio' che la finestra ricorda di se', e quando deve dimenticarlo.
/// </summary>
/// <remarks>
/// La regola che conta e' quella dello schermo scollegato: una posizione salvata su un monitor
/// che non c'e' piu' riaprirebbe la finestra dove nessuno puo' vederla ne' afferrarla.
/// </remarks>
public class PreferenzeTests
{
    private static readonly PosizioneFinestra.AreaDiLavoro Principale = new(0, 0, 1920, 1040);

    private static readonly PosizioneFinestra.AreaDiLavoro ADestra = new(1920, 0, 2560, 1400);

    [Fact]
    public void SenzaFileValgonoLePredefinite()
    {
        Assert.Equal(Preferenze.Predefinite, Preferenze.Da(null));
        Assert.Equal(Preferenze.Predefinite, Preferenze.Da(string.Empty));
        Assert.Null(Preferenze.Predefinite.Finestra);
        Assert.Equal(1.0d, Preferenze.Predefinite.ScalaTesto);
    }

    [Fact]
    public void UnFileRottoNonFermaLaFinestra() =>
        Assert.Equal(Preferenze.Predefinite, Preferenze.Da("{ questo non e' json"));

    [Fact]
    public void UnaScalaNonAmmessaTornaAllaNormale()
    {
        // Un file scritto a mano con 2.7 darebbe una finestra tre volte piu' grande dello
        // schermo, e uno con 0.5 pulsanti da 16 px. Le scale sono sei, e sono quelle: senza
        // l'asserzione su 0.75 una lista tornata a [1..1.5] passerebbe solo il test dell'ordine.
        Assert.Equal(1.0d, Preferenze.Da("""{"textScale": 2.7}""").ScalaTesto);
        Assert.Equal(1.0d, Preferenze.Da("""{"textScale": 0.5}""").ScalaTesto);
        Assert.Equal(1.15d, Preferenze.Da("""{"textScale": 1.15}""").ScalaTesto);
        Assert.Equal(0.75d, Preferenze.Da("""{"textScale": 0.75}""").ScalaTesto);
        Assert.Equal(1.0d, Preferenze.Da("""{}""").ScalaTesto);
    }

    [Fact]
    public void AndataERitornoDalJson()
    {
        Preferenze originali = new(new PosizioneFinestra(192, 100, 900, 700, Maximized: false), 1.3d, "dark");

        Assert.Equal(originali, Preferenze.Da(originali.InJson()));
        Assert.Contains("\"textScale\":1.3", originali.InJson(), StringComparison.Ordinal);
        Assert.Contains("\"theme\":\"dark\"", originali.InJson(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{}""", "system")]
    [InlineData("""{"theme": null}""", "system")]
    [InlineData("""{"theme": "nero"}""", "system")]
    [InlineData("""{"theme": "dark"}""", "dark")]
    [InlineData("""{"theme": "Dark"}""", "dark")]
    [InlineData("""{"theme": "light"}""", "light")]
    public void UnTemaNonAmmessoTornaAQuelloDelSistema(string json, string atteso)
    {
        // Un file vecchio non ha il campo, uno scritto a mano puo' avere di tutto: niente di
        // questo deve fermare la finestra, e le maiuscole si perdonano.
        Assert.Equal(atteso, Preferenze.Da(json).Tema);
        Assert.Equal("system", Preferenze.Predefinite.Tema);
    }

    [Fact]
    public void UnaPosizioneDentroLoSchermoSiTiene()
    {
        PosizioneFinestra posizione = new(192, 100, 900, 700, Maximized: false);

        Assert.Equal(posizione, posizione.SuUnoDegli([Principale]));
    }

    [Fact]
    public void UnaPosizioneSulSecondoSchermoSiTieneFincheCE()
    {
        // Il caso vero: portatile con monitor esterno, finestra lasciata sul monitor, e il
        // giorno dopo il monitor non c'e'. Con entrambi gli schermi la posizione vale; con
        // il solo schermo del portatile no.
        PosizioneFinestra sulMonitor = new(2400, 200, 900, 700, Maximized: false);

        Assert.Equal(sulMonitor, sulMonitor.SuUnoDegli([Principale, ADestra]));
        Assert.Null(sulMonitor.SuUnoDegli([Principale]));
    }

    [Theory]
    [InlineData(1850, 100)]
    [InlineData(100, 980)]
    [InlineData(-500, 100)]
    [InlineData(100, -500)]
    public void UnaPosizioneCheLasciaFuoriLAngoloAfferrabileSiDimentica(int x, int y)
    {
        // Non basta che un pixel sia dentro: deve starci l'angolo con la barra del titolo,
        // altrimenti la finestra si vede ma non si puo' spostare.
        PosizioneFinestra posizione = new(x, y, 900, 700, Maximized: false);

        Assert.Null(posizione.SuUnoDegli([Principale]));
    }

    [Fact]
    public void UnaFinestraTroppoPiccolaPerEssereVeraSiDimentica() =>
        Assert.Null(new PosizioneFinestra(10, 10, 40, 40, Maximized: false).SuUnoDegli([Principale]));

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1800, 920, true)]
    [InlineData(1801, 920, false)]
    [InlineData(1800, 921, false)]
    [InlineData(-8, 0, true)]
    [InlineData(-16, 0, true)]
    [InlineData(-17, 0, false)]
    [InlineData(0, -1, false)]
    public void IlBordoAfferrabileSiFermaEsattamenteSulBordoDelloSchermo(int x, int y, bool siTiene)
    {
        // I bordi esatti, altrimenti un <= che diventa < passerebbe inosservato. A sinistra
        // c'e' una tolleranza: una finestra agganciata al bordo sta a X = -8 per via del
        // bordo invisibile di Windows, e va ricordata. In alto no: (-8, -8) e' una finestra
        // massimizzata, e quella non e' una geometria normale.
        PosizioneFinestra posizione = new(x, y, 900, 700, Maximized: false);

        Assert.Equal(siTiene ? posizione : null, posizione.SuUnoDegli([Principale]));
    }

    [Fact]
    public void UnaFinestraLargaEsattamenteQuantoLAngoloSiTiene()
    {
        Assert.NotNull(new PosizioneFinestra(10, 10, 120, 120, Maximized: false).SuUnoDegli([Principale]));
        Assert.Null(new PosizioneFinestra(10, 10, 119, 120, Maximized: false).SuUnoDegli([Principale]));
        Assert.Null(new PosizioneFinestra(10, 10, 120, 119, Maximized: false).SuUnoDegli([Principale]));
    }

    [Theory]
    [InlineData(int.MaxValue, 100)]
    [InlineData(int.MaxValue - 100, 100)]
    [InlineData(100, int.MaxValue)]
    [InlineData(100, int.MaxValue - 100)]
    [InlineData(int.MinValue, 100)]
    public void UnaCoordinataAssurdaNonTrabocca(int x, int y)
    {
        // Un file scritto a mano con x = 2147483647: la vecchia somma X + 120 traboccava in
        // negativo, il confronto passava e la finestra si apriva invisibile - per sempre,
        // perche' alla chiusura si risalvava identica. Anche a cento dal massimo: li' la
        // prima clausola non trabocca ancora, e resta solo la seconda a difendere.
        Assert.Null(new PosizioneFinestra(x, y, 900, 700, Maximized: false).SuUnoDegli([Principale, ADestra]));
    }

    [Fact]
    public void ChiusaNormaleSiRicordaDoveE()
    {
        PosizioneFinestra adesso = new(300, 200, 900, 700, Maximized: false);

        Assert.Equal(adesso, PosizioneFinestra.AllaChiusura(
            ridottaAIcona: false, massimizzata: false, ultimaNormale: null, salvata: null, attuale: adesso));
    }

    [Fact]
    public void ChiusaATuttoSchermoSiRicordaLaGeometriaNormaleDiQuestaSessione()
    {
        // Il difetto vero: la finestra di ieri era sul monitor A, oggi la si sposta su B e la
        // si massimizza. Chiudendo si deve ricordare B - l'ultima geometria normale di oggi -
        // e non A, che e' cio' che il file diceva stamattina.
        PosizioneFinestra ieri = new(100, 100, 900, 700, Maximized: false);
        PosizioneFinestra oggi = new(2400, 200, 900, 700, Maximized: false);
        PosizioneFinestra schermoIntero = new(-8, -8, 1936, 1056, Maximized: false);

        Assert.Equal(oggi with { Maximized = true }, PosizioneFinestra.AllaChiusura(
            ridottaAIcona: false, massimizzata: true, ultimaNormale: oggi, salvata: ieri, attuale: schermoIntero));

        // Senza una geometria di oggi, vale quella di ieri.
        Assert.Equal(ieri with { Maximized = true }, PosizioneFinestra.AllaChiusura(
            ridottaAIcona: false, massimizzata: true, ultimaNormale: null, salvata: ieri, attuale: schermoIntero));
    }

    [Fact]
    public void ATuttoSchermoSenzaAlcunaGeometriaNotaSiRicordaSoloLoStato()
    {
        // Primo avvio, subito massimizzata, chiusa: le misure dello schermo intero NON vanno
        // salvate come se fossero una finestra normale. Resta lo stato, con una posizione che
        // nessuno schermo accetta: la finestra riapre dove decide il sistema, ma piena. La
        // geometria e' quella di X11, (0, 0): su Windows sta a (-8, -8) e verrebbe scartata
        // comunque, e un test con quella non distinguerebbe la regola dalla fortuna.
        PosizioneFinestra schermoIntero = new(0, 0, 1920, 1040, Maximized: false);

        PosizioneFinestra? ricordata = PosizioneFinestra.AllaChiusura(
            ridottaAIcona: false, massimizzata: true, ultimaNormale: null, salvata: null, attuale: schermoIntero);

        Assert.NotNull(ricordata);
        Assert.True(ricordata.Maximized);
        Assert.Null(ricordata.SuUnoDegli([Principale, ADestra]));
    }

    [Fact]
    public void RidottaAIconaSiRicordaComEraPrima()
    {
        PosizioneFinestra normale = new(300, 200, 900, 700, Maximized: false);
        PosizioneFinestra fuori = new(-32000, -32000, 900, 700, Maximized: false);

        // Prima era normale: si ricorda normale, anche se il file diceva a tutto schermo.
        Assert.Equal(normale, PosizioneFinestra.AllaChiusura(
            ridottaAIcona: true, massimizzata: false, ultimaNormale: normale,
            salvata: normale with { Maximized = true }, attuale: fuori));

        // Prima era a tutto schermo: si ricorda cosi'.
        Assert.Equal(normale with { Maximized = true }, PosizioneFinestra.AllaChiusura(
            ridottaAIcona: true, massimizzata: true, ultimaNormale: normale, salvata: null, attuale: fuori));

        // Non si sa niente: niente da dire, e soprattutto NON la posizione fuori da tutto.
        Assert.Null(PosizioneFinestra.AllaChiusura(
            ridottaAIcona: true, massimizzata: false, ultimaNormale: null, salvata: null, attuale: fuori));
    }

    [Fact]
    public void LaVoceDelSelettoreSiLeggeComeUnaPercentuale()
    {
        // E' cio' che un lettore di schermo annuncia: "115 %", non "1,15".
        string testo = new OpzioneScala(1.15d).ToString();

        Assert.Contains("115", testo, StringComparison.Ordinal);
        Assert.Contains("%", testo, StringComparison.Ordinal);
        Assert.Contains("75", new OpzioneScala(0.75d).ToString(), StringComparison.Ordinal);
        Assert.Equal(new OpzioneScala(1.15d), new OpzioneScala(1.15d));
    }

    [Fact]
    public void LeScaleAmmesseSonoInOrdineEComprendonoLaNormale()
    {
        Assert.Equal(1.0d, Preferenze.ScalaNormale);
        Assert.Contains(Preferenze.ScalaNormale, Preferenze.ScaleAmmesse);

        // Il pavimento e' 0,75 e non scende: e' la scala a cui un controllo Fluent da 32 px
        // e' ancora 24 px, e l'anello di stato tiene il buco (misurato su catture reali).
        Assert.True(Preferenze.ScaleAmmesse[0] >= 0.75d);

        for (int i = 1; i < Preferenze.ScaleAmmesse.Count; i++)
        {
            Assert.True(Preferenze.ScaleAmmesse[i] > Preferenze.ScaleAmmesse[i - 1]);
        }
    }
}