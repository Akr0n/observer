using Avalonia;
using Observer.App.Controls;

namespace Observer.App.Tests;

/// <summary>
/// La matematica del tachimetro.
/// </summary>
/// <remarks>
/// Sono i test di una cosa che nessun altro test puo' cogliere: il disegno non fallisce mai,
/// sbaglia soltanto. Una lancetta a meta' corsa quando il valore e' al massimo non rompe
/// niente, non lancia niente, e chi guarda legge un numero sbagliato credendolo misurato.
/// </remarks>
public class GaugeScaleTests
{
    private static readonly Point Centro = new(100d, 100d);

    [Fact]
    public void LoZeroStaDoveComincaLaScala()
    {
        Assert.Equal(GaugeScale.Partenza, GaugeScale.Angolo(0d), 9);
    }

    [Fact]
    public void IlPienoStaAlFondoScala()
    {
        Assert.Equal(GaugeScale.Arrivo, GaugeScale.Angolo(1d), 9);
    }

    [Fact]
    public void LaMetaStaInCima()
    {
        // 135 + 135 = 270 gradi, cioe' dritto in alto: e' il punto in cui l'occhio verifica da
        // solo se la lancetta e' dove dovrebbe. Se questa cambia, il tachimetro non e' piu'
        // simmetrico e si legge male senza che nessun altro test se ne accorga.
        Assert.Equal(270d, GaugeScale.Angolo(0.5d), 9);

        Point cima = GaugeScale.Punto(Centro, 50d, GaugeScale.Angolo(0.5d));

        Assert.Equal(Centro.X, cima.X, 6);
        Assert.Equal(Centro.Y - 50d, cima.Y, 6);
    }

    [Theory]
    [InlineData(-0.4d)]
    [InlineData(1.7d)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FuoriScalaLaLancettaRestaSullArco(double fuori)
    {
        double angolo = GaugeScale.Angolo(fuori);

        Assert.InRange(angolo, GaugeScale.Partenza, GaugeScale.Arrivo);
    }

    [Fact]
    public void UnaPercentualeNonMisurabileNonFaSparireIlTachimetro()
    {
        // Una metrica che non si e' potuta misurare arriva come NaN. Un NaN dentro un seno
        // esce come NaN nelle coordinate, e Avalonia una geometria con dentro un NaN non la
        // disegna affatto: il riquadro resterebbe vuoto, senza dire perche'.
        double angolo = GaugeScale.Angolo(double.NaN);

        Assert.False(double.IsNaN(angolo));
        Assert.Equal(GaugeScale.Partenza, angolo, 9);

        Point punto = GaugeScale.Punto(Centro, 50d, angolo);

        Assert.False(double.IsNaN(punto.X));
        Assert.False(double.IsNaN(punto.Y));
    }

    [Fact]
    public void LeTaccheCopronoLArcoDaCimaAFondo()
    {
        const int intervalli = 10;

        Assert.Equal(GaugeScale.Partenza, GaugeScale.AngoloDellaTacca(0, intervalli), 9);
        Assert.Equal(GaugeScale.Arrivo, GaugeScale.AngoloDellaTacca(intervalli, intervalli), 9);

        // Passo costante: una scala a passo variabile si legge come se i valori centrali
        // fossero piu' vicini fra loro di quanto sono.
        double passo = GaugeScale.Apertura / intervalli;

        for (int i = 1; i <= intervalli; i++)
        {
            double delta = GaugeScale.AngoloDellaTacca(i, intervalli)
                - GaugeScale.AngoloDellaTacca(i - 1, intervalli);

            Assert.Equal(passo, delta, 9);
        }
    }

    [Fact]
    public void UnaScalaSenzaIntervalliVieneRifiutata()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GaugeScale.AngoloDellaTacca(0, 0));
    }

    [Fact]
    public void LArcoScopertoStaInBassoEStaSimmetrico()
    {
        // Il pezzo di cerchio su cui la lancetta non passa mai deve stare in basso e centrato,
        // altrimenti il tachimetro appare storto. Sono i 90 gradi fra l'arrivo e la partenza.
        double scoperto = 360d - GaugeScale.Apertura;

        Assert.Equal(90d, scoperto, 9);

        Point zero = GaugeScale.Punto(Centro, 50d, GaugeScale.Partenza);
        Point fondo = GaugeScale.Punto(Centro, 50d, GaugeScale.Arrivo);

        // Stessa altezza, sotto il centro, e speculari rispetto all'asse verticale.
        Assert.Equal(zero.Y, fondo.Y, 6);
        Assert.True(zero.Y > Centro.Y);
        Assert.Equal(Centro.X - zero.X, fondo.X - Centro.X, 6);
    }
}