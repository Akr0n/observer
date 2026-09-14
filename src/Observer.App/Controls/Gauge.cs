using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;

namespace Observer.App.Controls;

/// <summary>
/// A dashboard gauge: graduated arc, redline zone and needle.
/// </summary>
/// <remarks>
/// Drawn by hand, and that was not an aesthetic preference: it is the only route left after
/// ruling the others out <b>with a measurement</b>. <c>LiveChartsCore.SkiaSharpView.Avalonia</c>
/// 2.0.5 compiles and then throws <c>MissingFieldException</c> on
/// <c>Avalonia.Input.Gestures.PinchEvent</c> as soon as a chart is constructed, because it is
/// built against Avalonia 11; <c>Avalonia.Controls.Charts</c>, which already has gauges ready to
/// use, requires a paid Avalonia Pro licence. Every third-party package here carries the same
/// risk that killed the first one: being built against a version of Avalonia different from the
/// one in use. A control that uses only <see cref="DrawingContext"/> does not carry that risk,
/// and has nothing to update.
/// <para>
/// All the arithmetic lives in <see cref="GaugeScale"/>, which has its own tests. What is left
/// here is the drawing, which no test can look at.
/// </para>
/// </remarks>
public sealed class Gauge : Control
{
    /// <summary>How long the needle takes to travel from one value to the next.</summary>
    /// <remarks>
    /// <b>It must stay shorter than the sampling interval</b>, and there is a test that checks
    /// it. A travel time as long as the interval would never end: every sample would restart it
    /// from an interpolated position, and the needle would not sit still on a measured value
    /// for even an instant.
    /// <para>
    /// <b>Two hundred milliseconds, and that number is measured.</b> This is a window that
    /// measures CPU usage, so what it spends drawing itself goes into the number it
    /// shows: it is an instrument that contributes to what it reads. Paired comparison on the
    /// same bench, two gauges, eight logical processors, window in the foreground, in
    /// Release: with no travel time <b>0.79%</b> of the machine's CPU, with 200 ms
    /// <b>1.39%</b>, with 400 ms <b>3.05%</b>. Doubling the duration quadruples the overhead,
    /// because the cost is the fraction of a second in which the animation runs. At 200 ms
    /// that overhead stays below one percentage point and the needle no longer jumps; at 400 ms
    /// you paid over two points for a smoothness that is indistinguishable by eye.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan NeedleTravelTime = TimeSpan.FromMilliseconds(200);

    /// <summary>The measured value, from 0 to 1. It is also what gets animated.</summary>
    /// <remarks>
    /// <b>Between one sample and the next the needle passes through positions nobody
    /// measured</b>, and it is worth saying because nowhere else does this program do that.
    /// Here it is allowed for a precise reason: an analogue needle has an inertia the viewer
    /// expects, and the travel between two readings reads as inertia, not as a measurement. The
    /// NUMBER in the center is not animated at all, and that is where the value is read.
    /// <para>
    /// An attempt to keep the two things apart - a second animated property following this one -
    /// was made and MEASURED, and it comes out worse in both of the ways it can be
    /// written. If it is written by hand from <c>OnPropertyChanged</c>, a frame goes by between
    /// writing the value and starting the transition: the needle is drawn straight away at the
    /// new value, the animation takes it back to the old one and makes it climb again - forward,
    /// back, forward, at every sample, even with the values standing still. Traced, and quoted
    /// as it was logged, with <c>Posizione</c> the Italian name that second property then had:
    /// <c>"Posizione 0.7657 -> 0.7363 prio=Animation"</c> right after <c>Fraction</c> had
    /// gone from 0.7363 to 0.7657. Binding it with a <c>Bind</c> in the constructor, on the
    /// other hand, stops the application from opening at all.
    /// </para>
    /// </remarks>
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<Gauge, double>(nameof(Fraction));

    /// <summary>The number written in the center, already formatted.</summary>
    public static readonly StyledProperty<string> DisplayProperty =
        AvaloniaProperty.Register<Gauge, string>(nameof(Display), string.Empty);

    /// <summary>What this gauge measures.</summary>
    public static readonly StyledProperty<string> CaptionProperty =
        AvaloniaProperty.Register<Gauge, string>(nameof(Caption), string.Empty);

    /// <summary>Where the redline zone begins, from 0 to 1.</summary>
    public static readonly StyledProperty<double> RedlineProperty =
        AvaloniaProperty.Register<Gauge, double>(nameof(Redline), 0.85d);

    /// <summary>The brush of the arc not yet travelled.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<Gauge, IBrush?>(nameof(TrackBrush));

    /// <summary>The brush of the arc already travelled.</summary>
    public static readonly StyledProperty<IBrush?> ValueBrushProperty =
        AvaloniaProperty.Register<Gauge, IBrush?>(nameof(ValueBrush));

    /// <summary>The brush of the redline zone, and of the value once it enters it.</summary>
    public static readonly StyledProperty<IBrush?> RedlineBrushProperty =
        AvaloniaProperty.Register<Gauge, IBrush?>(nameof(RedlineBrush));

    /// <summary>The brush of the needle and of the labels.</summary>
    public static readonly StyledProperty<IBrush?> NeedleBrushProperty =
        AvaloniaProperty.Register<Gauge, IBrush?>(nameof(NeedleBrush));

    // How much of the control's height the drawing takes. The rest is the band of the labels:
    // the number used to sit INSIDE the gauge, above the pivot, and it was the thing that read
    // worst precisely while the needle went over it.
    private const double DialHeightShare = 0.70d;

    // The size the gauge was first drawn at, and which still applies where nobody gives it one.
    private const double DefaultWidth = 148d;
    private const double DefaultHeight = 212d;

    // Where the labels start, in radii from the center. More than one because the stroke of the arc
    // sticks out half a thickness past the radius: stopping at 1.08 left the number inside the
    // notch at the bottom of the gauge, which is empty but is still inside the drawing.
    private const double LabelOffsetRadii = 1.15d;

    // What does not change from one frame to the next, cached. While the needle travels this
    // Render() runs about sixty times a second, and the background arc, the redline zone, the
    // ticks and the texts are identical in all those frames: rebuilding them every time means
    // redoing two text layouts sixty times a second to change not one pixel. It was
    // measured that the whole cost of the animation falls on the UI thread, inside
    // Render, because the animated property is in AffectsRender - so the work per
    // frame is the real lever, more than the duration.
    private double drawnRadius;
    private double drawnRedline;
    private StreamGeometry? trackArc;
    private StreamGeometry? redlineArc;
    private StreamGeometry? ticks;
    private FormattedText? displayText;
    private FormattedText? captionText;
    private string? drawnDisplay;
    private string? drawnCaption;
    private IBrush? drawnTextBrush;

    static Gauge()
    {
        // It redraws when the NEEDLE moves, not when the measurement changes: the animation
        // sits between the two, and hooking Fraction up here would give one frame per
        // sample, that is, the jump the animation is there to remove.
        AffectsRender<Gauge>(
            FractionProperty,
            DisplayProperty,
            CaptionProperty,
            RedlineProperty,
            TrackBrushProperty,
            ValueBrushProperty,
            RedlineBrushProperty,
            NeedleBrushProperty);
    }

    /// <summary>Builds the gauge.</summary>
    public Gauge()
    {
        Transitions =
        [
            new DoubleTransition
            {
                Property = FractionProperty,
                Duration = NeedleTravelTime,

                // It starts at once and settles gently. Deliberately NOT an easing that
                // overshoots (BackEaseOut, ElasticEaseOut): on a measuring instrument those
                // would show, for a few tenths of a second, a value higher than the one read,
                // that is, a peak that never happened.
                Easing = new CubicEaseOut(),
            },
        ];
    }

    /// <summary>The measured value, from 0 to 1.</summary>
    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    /// <summary>The number written in the center, already formatted.</summary>
    public string Display
    {
        get => GetValue(DisplayProperty);
        set => SetValue(DisplayProperty, value);
    }

    /// <summary>What this gauge measures.</summary>
    public string Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <summary>Where the redline zone begins, from 0 to 1.</summary>
    public double Redline
    {
        get => GetValue(RedlineProperty);
        set => SetValue(RedlineProperty, value);
    }

    /// <summary>The brush of the arc not yet travelled.</summary>
    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>The brush of the arc already travelled.</summary>
    public IBrush? ValueBrush
    {
        get => GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    /// <summary>The brush of the redline zone.</summary>
    public IBrush? RedlineBrush
    {
        get => GetValue(RedlineBrushProperty);
        set => SetValue(RedlineBrushProperty, value);
    }

    /// <summary>The brush of the needle and of the labels.</summary>
    public IBrush? NeedleBrush
    {
        get => GetValue(NeedleBrushProperty);
        set => SetValue(NeedleBrushProperty, value);
    }

    /// <summary>How much space it asks for: ALL of what it is offered.</summary>
    /// <param name="availableSize">The available space.</param>
    /// <returns>The available space itself, or the original size where there is no limit.</returns>
    /// <remarks>
    /// A Control with no size of its own reports zero, and zero is what the gauge grid gave it
    /// on day one: a box of the right height and no gauge inside it. Here the gauge fills the
    /// cell the panel decided for it; where there is no cell - measured with no constraint -
    /// the size it was first drawn at applies, 148x212.
    /// </remarks>
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? DefaultWidth : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? DefaultHeight : availableSize.Height);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The dial takes the width, not the whole height: below it a band is left for the
        // labels, which used to sit inside the drawing.
        double side = Math.Min(Bounds.Width, Bounds.Height * DialHeightShare);

        if (side <= 0d)
        {
            return;
        }

        double thickness = Math.Max(3d, side * 0.11d);
        double radius = (side / 2d) - (thickness / 2d) - 1d;

        if (radius <= 0d)
        {
            return;
        }

        // The center sits at the middle of the width and inside the dial's band, not at half
        // the control: what is below is text, and centering on the whole height would push
        // the drawing down over the labels.
        Point center = new(Bounds.Width / 2d, (side / 2d) + (radius * 0.10d));

        IBrush trackBrush = TrackBrush ?? Brushes.Gainsboro;
        IBrush valueBrush = ValueBrush ?? Brushes.SteelBlue;
        IBrush redlineBrush = RedlineBrush ?? Brushes.IndianRed;
        IBrush needleBrush = NeedleBrush ?? Brushes.DimGray;

        double valueFraction = GaugeScale.ClampFraction(Fraction);
        double redlineFraction = GaugeScale.ClampFraction(Redline);

        RebuildStaticGeometry(center, radius, thickness, redlineFraction);

        context.DrawGeometry(null, PenFor(trackBrush, thickness), trackArc!);

        // The redline zone is visible even when the needle is far from it, and that is what
        // makes it a threshold instead of an alarm: a bar that turns red only when it is too
        // late never says how much room is left.
        if (redlineArc is not null)
        {
            context.DrawGeometry(null, PenFor(redlineBrush, thickness * 0.42d), redlineArc);
        }

        if (valueFraction > 0d)
        {
            context.DrawGeometry(
                null,
                PenFor(valueFraction >= redlineFraction ? redlineBrush : valueBrush, thickness),
                ArcBetween(center, radius, 0d, valueFraction));
        }

        context.DrawGeometry(null, PenFor(needleBrush, Math.Max(1d, thickness * 0.09d)), ticks!);

        DrawNeedle(context, center, radius, thickness, valueFraction, needleBrush);
        DrawLabels(context, center, radius, needleBrush);
    }

    private static Pen PenFor(IBrush brush, double thickness) =>
        new(brush, thickness) { LineCap = PenLineCap.Round };

    private static StreamGeometry ArcBetween(Point center, double radius, double from, double to)
    {
        double startAngle = GaugeScale.AngleFor(from);
        double endAngle = GaugeScale.AngleFor(to);

        StreamGeometry geometry = new();

        using (StreamGeometryContext geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(GaugeScale.PointAt(center, radius, startAngle), isFilled: false);

            geometryContext.ArcTo(
                GaugeScale.PointAt(center, radius, endAngle),
                new Size(radius, radius),
                rotationAngle: 0d,
                isLargeArc: endAngle - startAngle > 180d,
                sweepDirection: SweepDirection.Clockwise,
                isStroked: true);

            geometryContext.EndFigure(isClosed: false);
        }

        return geometry;
    }

    private void RebuildStaticGeometry(Point center, double radius, double thickness, double redlineFraction)
    {
        if (trackArc is not null
            && Math.Abs(drawnRadius - radius) < 0.01d
            && Math.Abs(drawnRedline - redlineFraction) < 0.001d)
        {
            return;
        }

        drawnRadius = radius;
        drawnRedline = redlineFraction;

        trackArc = ArcBetween(center, radius, 0d, 1d);

        // Below a thousandth the arc is shorter than its own rounded cap: drawing it would
        // leave a dot at the full-scale end even where the redline zone does not begin.
        redlineArc = redlineFraction < 0.999d ? ArcBetween(center, radius, redlineFraction, 1d) : null;

        ticks = TickMarks(center, radius, thickness);

        // The texts are measured against the radius: if the radius changed, so did their font size.
        drawnDisplay = null;
        drawnCaption = null;
    }

    private static StreamGeometry TickMarks(Point center, double radius, double thickness)
    {
        const int intervals = 10;

        double outerRadius = radius - (thickness / 2d) - 2d;
        double innerRadius = Math.Max(1d, outerRadius - Math.Max(2d, thickness * 0.45d));

        StreamGeometry geometry = new();

        using (StreamGeometryContext geometryContext = geometry.Open())
        {
            for (int i = 0; i <= intervals; i++)
            {
                double angle = GaugeScale.TickAngle(i, intervals);

                geometryContext.BeginFigure(GaugeScale.PointAt(center, innerRadius, angle), isFilled: false);
                geometryContext.LineTo(GaugeScale.PointAt(center, outerRadius, angle), isStroked: true);
                geometryContext.EndFigure(isClosed: false);
            }
        }

        return geometry;
    }

    private static void DrawNeedle(
        DrawingContext context,
        Point center,
        double radius,
        double thickness,
        double valueFraction,
        IBrush brush)
    {
        double angle = GaugeScale.AngleFor(valueFraction);
        double length = radius - thickness;

        if (length <= 0d)
        {
            return;
        }

        // A short piece of the needle carries on past the pivot, as on real gauges: that is what
        // makes the object read as a needle on a pivot rather than as a radius.
        context.DrawLine(
            PenFor(brush, Math.Max(1.5d, thickness * 0.22d)),
            GaugeScale.PointAt(center, -(thickness * 0.5d), angle),
            GaugeScale.PointAt(center, length, angle));

        context.DrawEllipse(brush, null, center, thickness * 0.30d, thickness * 0.30d);
    }

    private void DrawLabels(DrawingContext context, Point center, double radius, IBrush brush)
    {
        double fontSize = Math.Max(9d, radius * 0.34d);

        // DrawText positions the TOP LEFT CORNER of the text, not its baseline.
        // The caption therefore starts where the number really ends, and not at a multiple of
        // the number's font size picked by eye: that multiple was right for one font size only,
        // and on a smaller gauge the two labels overlapped.
        double textTop = center.Y + (radius * LabelOffsetRadii);

        if (!ReferenceEquals(drawnTextBrush, brush))
        {
            // The brush is inside the already laid-out text: if the theme changes while the
            // window is open, a cached text would keep the previous brush -
            // light text on a light background, that is, invisible.
            drawnTextBrush = brush;
            drawnDisplay = null;
            drawnCaption = null;
        }

        if (!string.IsNullOrEmpty(Display))
        {
            if (displayText is null || !string.Equals(drawnDisplay, Display, StringComparison.Ordinal))
            {
                displayText = FormatText(Display, fontSize, brush);
                drawnDisplay = Display;
            }

            context.DrawText(displayText, new Point(center.X - (displayText.Width / 2d), textTop));

            textTop += displayText.Height;
        }

        if (!string.IsNullOrEmpty(Caption))
        {
            if (captionText is null
                || !string.Equals(drawnCaption, Caption, StringComparison.Ordinal))
            {
                // Eleven and not eight: any lower and, on a 148 px gauge, the caption dropped
                // to ten and became the smallest text in the window, the very one that says
                // WHAT the big number above measures.
                captionText = FormatText(Caption, Math.Max(11d, fontSize * 0.46d), brush);
                drawnCaption = Caption;
            }

            context.DrawText(captionText, new Point(center.X - (captionText.Width / 2d), textTop));
        }
    }

    private static FormattedText FormatText(string text, double fontSize, IBrush brush) =>
        new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            fontSize,
            brush);
}