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
    private static readonly Point Center = new(100d, 100d);

    [Fact]
    public void ZeroSitsWhereTheScaleBegins()
    {
        Assert.Equal(GaugeScale.StartAngle, GaugeScale.AngleFor(0d), 9);
    }

    [Fact]
    public void FullSitsWhereTheScaleEnds()
    {
        Assert.Equal(GaugeScale.EndAngle, GaugeScale.AngleFor(1d), 9);
    }

    [Fact]
    public void HalfSitsAtTheTop()
    {
        // 135 + 135 = 270 gradi, cioe' dritto in alto: e' il punto in cui l'occhio verifica da
        // solo se la lancetta e' dove dovrebbe. Se questa cambia, il tachimetro non e' piu'
        // simmetrico e si legge male senza che nessun altro test se ne accorga.
        Assert.Equal(270d, GaugeScale.AngleFor(0.5d), 9);

        Point top = GaugeScale.PointAt(Center, 50d, GaugeScale.AngleFor(0.5d));

        Assert.Equal(Center.X, top.X, 6);
        Assert.Equal(Center.Y - 50d, top.Y, 6);
    }

    [Theory]
    [InlineData(-0.4d)]
    [InlineData(1.7d)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void OffScaleTheNeedleStaysOnTheArc(double offScale)
    {
        double angle = GaugeScale.AngleFor(offScale);

        Assert.InRange(angle, GaugeScale.StartAngle, GaugeScale.EndAngle);
    }

    [Fact]
    public void AnUnmeasurablePercentageDoesNotMakeTheGaugeVanish()
    {
        // Una metrica che non si e' potuta misurare arriva come NaN. Un NaN dentro un seno
        // esce come NaN nelle coordinate, e Avalonia una geometria con dentro un NaN non la
        // disegna affatto: il riquadro resterebbe vuoto, senza dire perche'.
        double angle = GaugeScale.AngleFor(double.NaN);

        Assert.False(double.IsNaN(angle));
        Assert.Equal(GaugeScale.StartAngle, angle, 9);

        Point pointOnArc = GaugeScale.PointAt(Center, 50d, angle);

        Assert.False(double.IsNaN(pointOnArc.X));
        Assert.False(double.IsNaN(pointOnArc.Y));
    }

    [Fact]
    public void TheTicksSpanTheWholeArcWithAConstantStep()
    {
        const int intervals = 10;

        Assert.Equal(GaugeScale.StartAngle, GaugeScale.TickAngle(0, intervals), 9);
        Assert.Equal(GaugeScale.EndAngle, GaugeScale.TickAngle(intervals, intervals), 9);

        // Passo costante: una scala a passo variabile si legge come se i valori centrali
        // fossero piu' vicini fra loro di quanto sono.
        double step = GaugeScale.SweepAngle / intervals;

        for (int i = 1; i <= intervals; i++)
        {
            double delta = GaugeScale.TickAngle(i, intervals)
                - GaugeScale.TickAngle(i - 1, intervals);

            Assert.Equal(step, delta, 9);
        }
    }

    [Fact]
    public void AScaleWithNoIntervalsIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GaugeScale.TickAngle(0, 0));
    }

    [Fact]
    public void TheUncoveredArcSitsAtTheBottomAndIsSymmetric()
    {
        // Il pezzo di cerchio su cui la lancetta non passa mai deve stare in basso e centrato,
        // altrimenti il tachimetro appare storto. Sono i 90 gradi fra l'arrivo e la partenza.
        double uncovered = 360d - GaugeScale.SweepAngle;

        Assert.Equal(90d, uncovered, 9);

        Point zero = GaugeScale.PointAt(Center, 50d, GaugeScale.StartAngle);
        Point fullScale = GaugeScale.PointAt(Center, 50d, GaugeScale.EndAngle);

        // Stessa altezza, sotto il centro, e speculari rispetto all'asse verticale.
        Assert.Equal(zero.Y, fullScale.Y, 6);
        Assert.True(zero.Y > Center.Y);
        Assert.Equal(Center.X - zero.X, fullScale.X - Center.X, 6);
    }
}