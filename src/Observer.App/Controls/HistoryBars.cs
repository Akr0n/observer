using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Observer.App.Services;

namespace Observer.App.Controls;

/// <summary>
/// The history strip: one small bar per interval, from the oldest to the most recent.
/// </summary>
/// <remarks>
/// <b>The rule that governs this drawing: absence must not be quieter than low usage, it must
/// be louder.</b>
/// <para>
/// It is counter-intuitive and worth writing down. The instinctive solution for "there is
/// nothing to show here" is to draw nothing, or to draw something faint. But here a machine at
/// rest is <i>already</i> almost nothing: an idle CPU sits at two or three per cent, that is,
/// bars one pixel tall for the whole strip. Making absence fainter still would leave it
/// indistinguishable from rest, and the strip would say "all quiet" precisely in the periods
/// where nothing is known. That is why a gap carries a dashed line over the full height and
/// breaks the baseline, while a measurement, even of zero, always has a solid foot.
/// </para>
/// <para>
/// Height carries the main information and colour reinforces it, never the other way round:
/// someone who cannot tell red from green still reads the strip, because a busy interval is
/// <i>tall</i> before it is even coloured.
/// </para>
/// </remarks>
public sealed class HistoryBars : Control
{
    private const double BarGap = 1d;
    private const double BaselineThickness = 1.5d;

    /// <summary>The minimum height of a measured column.</summary>
    /// <remarks>
    /// A measured zero must have a visible foot. The floor may only OVERSTATE a small value,
    /// never understate it: erring high on a two per cent value costs a couple of pixels, erring
    /// low means making the evidence that something was being measured there disappear.
    /// </remarks>
    private const double MinBarHeight = 3d;

    /// <summary>The intervals to draw, from the oldest to the most recent.</summary>
    public static readonly StyledProperty<IReadOnlyList<HistoryBar>?> BarsProperty =
        AvaloniaProperty.Register<HistoryBars, IReadOnlyList<HistoryBar>?>(nameof(Bars));

    /// <summary>From where an interval counts as busy, from 0 to 1.</summary>
    public static readonly StyledProperty<double> RedlineProperty =
        AvaloniaProperty.Register<HistoryBars, double>(nameof(Redline), 0.85d);

    /// <summary>The strip's background, that is the area running from zero to the maximum.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<HistoryBars, IBrush?>(nameof(TrackBrush));

    /// <summary>The colour of a measured interval.</summary>
    public static readonly StyledProperty<IBrush?> ValueBrushProperty =
        AvaloniaProperty.Register<HistoryBars, IBrush?>(nameof(ValueBrush));

    /// <summary>The colour of an interval that touched the redline.</summary>
    public static readonly StyledProperty<IBrush?> RedlineBrushProperty =
        AvaloniaProperty.Register<HistoryBars, IBrush?>(nameof(RedlineBrush));

    /// <summary>The colour of the dashes and of the baseline.</summary>
    public static readonly StyledProperty<IBrush?> MissingBrushProperty =
        AvaloniaProperty.Register<HistoryBars, IBrush?>(nameof(MissingBrush));

    static HistoryBars()
    {
        AffectsRender<HistoryBars>(
            BarsProperty,
            RedlineProperty,
            TrackBrushProperty,
            ValueBrushProperty,
            RedlineBrushProperty,
            MissingBrushProperty);
    }

    /// <summary>The intervals to draw.</summary>
    public IReadOnlyList<HistoryBar>? Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    /// <summary>From where an interval counts as busy.</summary>
    public double Redline
    {
        get => GetValue(RedlineProperty);
        set => SetValue(RedlineProperty, value);
    }

    /// <summary>The strip's background.</summary>
    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>The colour of a measured interval.</summary>
    public IBrush? ValueBrush
    {
        get => GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    /// <summary>The colour of an interval that touched the redline.</summary>
    public IBrush? RedlineBrush
    {
        get => GetValue(RedlineBrushProperty);
        set => SetValue(RedlineBrushProperty, value);
    }

    /// <summary>The colour of the dashes and of the baseline.</summary>
    public IBrush? MissingBrush
    {
        get => GetValue(MissingBrushProperty);
        set => SetValue(MissingBrushProperty, value);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The tooltip is recomputed on every move because it changes PER BAR, and a strip is a
    /// single control: without this it would say the same thing across the whole width, that
    /// is, it would say nothing.
    /// </remarks>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnPointerMoved(e);

        UpdateTip(e.GetPosition(this).X);
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        ToolTip.SetTip(this, null);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Bars is not { Count: > 0 } drawnBars || Bounds.Width <= 2d || Bounds.Height <= 6d)
        {
            return;
        }

        IBrush trackBrush = TrackBrush ?? Brushes.Gainsboro;
        IBrush valueBrush = ValueBrush ?? Brushes.SteelBlue;
        IBrush redlineBrush = RedlineBrush ?? Brushes.IndianRed;
        IBrush missingBrush = MissingBrush ?? Brushes.DimGray;

        double redline = Math.Clamp(Redline, 0d, 1d);

        // One background for the whole strip, and not one per bar: that way the extent of the
        // time window is always visible, even when there is not a single reading inside it.
        double baseY = Bounds.Height - BaselineThickness;
        double trackHeight = baseY;

        context.FillRectangle(trackBrush, new Rect(0d, 0d, Bounds.Width, trackHeight), 2f);

        double step = Bounds.Width / drawnBars.Count;
        double barWidth = Math.Max(1d, step - BarGap);

        for (int i = 0; i < drawnBars.Count; i++)
        {
            double x = i * step;

            if (drawnBars[i].Kind == BarKind.Missing)
            {
                DrawMissing(context, missingBrush, x, barWidth, baseY, trackHeight);

                continue;
            }

            DrawBar(context, drawnBars[i], valueBrush, redlineBrush, redline, x, barWidth, baseY, trackHeight);

            // The baseline continues under every interval something is known about, and breaks
            // under the gaps: it is the second thing, besides the dashes, that makes absence
            // visible even when the values around it are all low.
            context.FillRectangle(missingBrush, new Rect(x, baseY, barWidth, BaselineThickness));
        }
    }

    private static void DrawBar(
        DrawingContext context,
        HistoryBar bar,
        IBrush valueBrush,
        IBrush redlineBrush,
        double redline,
        double x,
        double barWidth,
        double baseY,
        double trackHeight)
    {
        double average = Math.Clamp(bar.Average, 0d, 1d);
        double peak = Math.Clamp(bar.Max, average, 1d);

        double averageHeight = Math.Max(MinBarHeight, average * trackHeight);
        double peakHeight = Math.Max(averageHeight, peak * trackHeight);

        // A bar that covered only part of its interval is drawn narrow in proportion: the last
        // one in the strip is always the interval IN PROGRESS, and with a wide step it would
        // be drawn full while only a few minutes are known. The rule lives in HistoryStrip
        // because it is arithmetic and can be tested without drawing anything.
        barWidth = HistoryStrip.WidthOf(bar, barWidth);

        // The extension, from where it usually sat up to where it got to. Without it, a low bar
        // with a brief peak and a plain low bar would be identical, and the peak - usually the
        // very thing being looked for - would vanish into the average.
        if (peakHeight - averageHeight > 0.5d)
        {
            context.FillRectangle(
                new SolidColorBrush(ColorOf(peak >= redline ? redlineBrush : valueBrush), 0.32d),
                new Rect(x, baseY - peakHeight, barWidth, peakHeight - averageHeight));
        }

        context.FillRectangle(
            average >= redline ? redlineBrush : valueBrush,
            new Rect(x, baseY - averageHeight, barWidth, averageHeight));
    }

    private static void DrawMissing(
        DrawingContext context,
        IBrush missingBrush,
        double x,
        double barWidth,
        double baseY,
        double trackHeight)
    {
        // Dashes over the FULL height, and no baseline underneath. It is not decoration: it is
        // the only way for "nothing is known" to stay distinguishable from "measured and at
        // rest", which on screen is almost as empty.
        double centerX = x + (barWidth / 2d) - 0.5d;
        double y = baseY - trackHeight;

        while (y < baseY)
        {
            double segmentEnd = Math.Min(y + 2d, baseY);

            context.FillRectangle(missingBrush, new Rect(centerX, y, 1d, segmentEnd - y));

            y += 4d;
        }
    }

    private static Color ColorOf(IBrush brush) =>
        brush is ISolidColorBrush solidBrush ? solidBrush.Color : Colors.Gray;

    /// <summary>Updates the tooltip with the bar sitting under the pointer.</summary>
    private void UpdateTip(double x)
    {
        string tipText = Bars is { Count: > 0 } drawnBars
            ? HistoryStrip.Describe(drawnBars, HistoryStrip.IndexAt(x, Bounds.Width, drawnBars.Count))
            : string.Empty;

        // It is rewritten only when it really changes: assigning the same string on every pixel
        // of movement would make the tooltip flicker while you are reading it.
        if (!string.Equals(ToolTip.GetTip(this) as string, tipText, StringComparison.Ordinal))
        {
            ToolTip.SetTip(this, tipText.Length == 0 ? null : tipText);
        }
    }
}