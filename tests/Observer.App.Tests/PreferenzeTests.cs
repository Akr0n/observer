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
        // schermo. Le scale sono quattro, e sono quelle.
        Assert.Equal(1.0d, Preferenze.Da("""{"textScale": 2.7}""").ScalaTesto);
        Assert.Equal(1.15d, Preferenze.Da("""{"textScale": 1.15}""").ScalaTesto);
        Assert.Equal(1.0d, Preferenze.Da("""{}""").ScalaTesto);
    }

    [Fact]
    public void AndataERitornoDalJson()
    {
        Preferenze originali = new(new PosizioneFinestra(192, 100, 900, 700, Maximized: false), 1.3d);

        Assert.Equal(originali, Preferenze.Da(originali.InJson()));
        Assert.Contains("\"textScale\":1.3", originali.InJson(), StringComparison.Ordinal);
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

    [Fact]
    public void LeScaleAmmesseSonoInOrdineEPartonoDallaNormale()
    {
        Assert.Equal(1.0d, Preferenze.ScaleAmmesse[0]);

        for (int i = 1; i < Preferenze.ScaleAmmesse.Count; i++)
        {
            Assert.True(Preferenze.ScaleAmmesse[i] > Preferenze.ScaleAmmesse[i - 1]);
        }
    }
}