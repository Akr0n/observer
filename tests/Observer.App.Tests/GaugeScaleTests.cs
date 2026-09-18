using Avalonia;
using Observer.App.Controls;

namespace Observer.App.Tests;

/// <summary>
/// The gauge arithmetic.
/// </summary>
/// <remarks>
/// These test something no other test can catch: drawing never fails, it only gets things
/// wrong. A needle halfway along its travel while the value is at the maximum breaks nothing,
/// throws nothing, and whoever is looking reads a wrong number believing it was measured.
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
        // 135 + 135 = 270 degrees, that is straight up: it is the point where the eye checks
        // on its own whether the needle is where it should be. If this changes, the gauge is
        // no longer symmetric and reads badly with no other test noticing.
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
        // A metric that could not be measured arrives as NaN. A NaN inside a sine comes out as
        // NaN in the coordinates, and Avalonia does not draw a geometry with a NaN in it at
        // all: the box would stay empty, without saying why.
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

        // Constant step: a scale with a varying step reads as if the middle values were closer
        // to each other than they are.
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
        // The piece of circle the needle never crosses must sit at the bottom and centred, or
        // the gauge looks crooked. It is the 90 degrees between the end and the start.
        double uncovered = 360d - GaugeScale.SweepAngle;

        Assert.Equal(90d, uncovered, 9);

        Point zero = GaugeScale.PointAt(Center, 50d, GaugeScale.StartAngle);
        Point fullScale = GaugeScale.PointAt(Center, 50d, GaugeScale.EndAngle);

        // Same height, below the centre, and mirrored about the vertical axis.
        Assert.Equal(zero.Y, fullScale.Y, 6);
        Assert.True(zero.Y > Center.Y);
        Assert.Equal(Center.X - zero.X, fullScale.X - Center.X, 6);
    }
}