using Avalonia;

namespace Observer.App.Controls;

/// <summary>
/// A dial's scale: where a value falls on the arc, and where its ticks are.
/// </summary>
/// <remarks>
/// Separate from the control that does the drawing, and not for elegance: it is the only part
/// that can go wrong <b>silently</b>. An error in the arc arithmetic does not make anything fail
/// and does not throw anything — it draws a needle pointing at the wrong place, and whoever is
/// looking has no way of noticing, because the only thing they could compare it against is the
/// needle itself. An Avalonia control cannot be queried without a graphical environment; this
/// class can, and that is why it has its own tests.
/// <para>
/// Angles are in degrees and follow the convention used in graphics: zero at 3 o'clock,
/// increasing <b>clockwise</b>, because Y grows downwards. The scale starts at 135 degrees
/// (bottom left), opens 270 degrees and ends at 405 (bottom right). It is the shape of a car's
/// rev counter: the gap is at the bottom, where the needle never goes.
/// </para>
/// </remarks>
public static class GaugeScale
{
    /// <summary>The angle of the scale's zero, in degrees.</summary>
    public const double StartAngle = 135d;

    /// <summary>How far the scale opens, in degrees.</summary>
    public const double SweepAngle = 270d;

    /// <summary>The angle of full scale, in degrees.</summary>
    public const double EndAngle = StartAngle + SweepAngle;

    /// <summary>Brings a value back inside the scale.</summary>
    /// <param name="fraction">The value, expected between 0 and 1.</param>
    /// <returns>The same value, clamped to 0..1; zero if it is not a number.</returns>
    /// <remarks>
    /// NaN becomes zero, and that is not pedantry: a percentage that could not be measured
    /// arrives all the way here as NaN, and a NaN inside a sine propagates NaN into the
    /// coordinates. Avalonia does not draw a geometry with a NaN in it, so the dial would
    /// disappear entirely — a fault that shows up as "the box is empty", without naming its cause.
    /// </remarks>
    public static double ClampFraction(double fraction) =>
        double.IsNaN(fraction) ? 0d : Math.Clamp(fraction, 0d, 1d);

    /// <summary>The angle a value falls at.</summary>
    /// <param name="fraction">The value, between 0 and 1.</param>
    /// <returns>The angle in degrees, between <see cref="StartAngle"/> and <see cref="EndAngle"/>.</returns>
    public static double AngleFor(double fraction) =>
        StartAngle + (ClampFraction(fraction) * SweepAngle);

    /// <summary>The angle of one tick of the scale.</summary>
    /// <param name="index">Which tick, from 0 to <paramref name="intervals"/> inclusive: the last
    /// one is the end of the scale.</param>
    /// <param name="intervals">How many intervals the scale is divided into.</param>
    /// <returns>The angle in degrees.</returns>
    public static double TickAngle(int index, int intervals)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intervals, 1);

        return AngleFor((double)index / intervals);
    }

    /// <summary>The point at a given angle and a given distance from the center.</summary>
    /// <param name="center">The center of the scale.</param>
    /// <param name="radius">The distance from the center.</param>
    /// <param name="degrees">The angle, measured as described on the type.</param>
    /// <returns>The point.</returns>
    public static Point PointAt(Point center, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180d;

        return new Point(
            center.X + (radius * Math.Cos(radians)),
            center.Y + (radius * Math.Sin(radians)));
    }
}