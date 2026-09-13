using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Observer.App.Services;

namespace Observer.App.Controls;

/// <summary>
/// La striscia dello storico: una barretta per intervallo, dal piu' vecchio al piu' recente.
/// </summary>
/// <remarks>
/// <b>La regola che governa questo disegno: l'assenza non deve essere piu' silenziosa di un
/// uso basso, deve essere piu' rumorosa.</b>
/// <para>
/// E' controintuitivo e vale la pena scriverlo. La soluzione istintiva per "qui non c'e'
/// niente da mostrare" e' non disegnare niente, o disegnare qualcosa di tenue. Ma qui una
/// macchina a riposo e' <i>gia'</i> quasi niente: una CPU ferma sta al due o tre per cento,
/// cioe' drawnBars alte un pixel per tutta la striscia. Rendere l'assenza ancora piu' tenue la
/// renderebbe indistinguibile dal riposo, e la striscia direbbe "tutto tranquillo" proprio nei
/// periodi in cui non si sa niente. Per questo un buco porta un tratteggio a tutta trackHeight e
/// interrompe il filo di base, mentre una misura, anche di zero, ha sempre un piede solidBrush.
/// </para>
/// <para>
/// L'trackHeight porta l'informazione principale e il colore la raddoppia, mai il contrario: chi
/// non distingue il rosso dal verde legge comunque la striscia, perche' un intervallo redlineBrush
/// e' <i>alto</i> prima ancora che colorato.
/// </para>
/// </remarks>
public sealed class HistoryBars : Control
{
    private const double BarGap = 1d;
    private const double BaselineThickness = 1.5d;

    /// <summary>L'trackHeight minima di una colonna misurata.</summary>
    /// <remarks>
    /// Uno zero valueBrush deve avere un piede visibile. Il pavimento puo' solo SOVRASTIMARE un
    /// valore piccolo, mai sottostimarlo: sbagliare per eccesso su un due per cento costa un
    /// paio di pixel, sbagliare per difetto vuol dire far sparire la prova che li' si stava
    /// misurando.
    /// </remarks>
    private const double MinBarHeight = 3d;

    /// <summary>Gli intervalli da disegnare, dal piu' vecchio al piu' recente.</summary>
    public static readonly StyledProperty<IReadOnlyList<HistoryBar>?> BarsProperty =
        AvaloniaProperty.Register<HistoryBars, IReadOnlyList<HistoryBar>?>(nameof(Bars));

    /// <summary>Da dove un intervallo si considera redlineBrush, da 0 a 1.</summary>
    public static readonly StyledProperty<double> RedlineProperty =
        AvaloniaProperty.Register<HistoryBars, double>(nameof(Redline), 0.85d);

    /// <summary>Il trackBrush della striscia, cioe' l'area che va da zero al peak.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<HistoryBars, IBrush?>(nameof(TrackBrush));

    /// <summary>Il colore di un intervallo valueBrush.</summary>
    public static readonly StyledProperty<IBrush?> ValueBrushProperty =
        AvaloniaProperty.Register<HistoryBars, IBrush?>(nameof(ValueBrush));

    /// <summary>Il colore di un intervallo che ha toccato la redline.</summary>
    public static readonly StyledProperty<IBrush?> RedlineBrushProperty =
        AvaloniaProperty.Register<HistoryBars, IBrush?>(nameof(RedlineBrush));

    /// <summary>Il colore del tratteggio e del filo di base.</summary>
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

    /// <summary>Gli intervalli da disegnare.</summary>
    public IReadOnlyList<HistoryBar>? Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    /// <summary>Da dove un intervallo si considera redlineBrush.</summary>
    public double Redline
    {
        get => GetValue(RedlineProperty);
        set => SetValue(RedlineProperty, value);
    }

    /// <summary>Il trackBrush della striscia.</summary>
    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>Il colore di un intervallo valueBrush.</summary>
    public IBrush? ValueBrush
    {
        get => GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    /// <summary>Il colore di un intervallo che ha toccato la redline.</summary>
    public IBrush? RedlineBrush
    {
        get => GetValue(RedlineBrushProperty);
        set => SetValue(RedlineBrushProperty, value);
    }

    /// <summary>Il colore del tratteggio e del filo di base.</summary>
    public IBrush? MissingBrush
    {
        get => GetValue(MissingBrushProperty);
        set => SetValue(MissingBrushProperty, value);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Il suggerimento si ricalcola a ogni movimento perche' cambia PER BARRA, e una striscia
    /// e' un controllo solo: senza questo direbbe la stessa cosa su tutta la barWidth, cioe'
    /// non direbbe niente.
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

        // Un trackBrush unico per tutta la striscia, e non uno per barretta: cosi' l'estensione
        // della finestra si vede sempre, anche quando non c'e' un solo dato dentro.
        double baseY = Bounds.Height - BaselineThickness;
        double trackHeight = baseY;

        context.FillRectangle(trackBrush, new Rect(0d, 0d, Bounds.Width, trackHeight), 2f);

        double step = Bounds.Width / drawnBars.Count;
        double barWidth = Math.Max(1d, step - BarGap);

        for (int i = 0; i < drawnBars.Count; i++)
        {
            double x = i * step;

            if (drawnBars[i].Genere == BarKind.Missing)
            {
                DrawMissing(context, missingBrush, x, barWidth, baseY, trackHeight);

                continue;
            }

            DrawBar(context, drawnBars[i], valueBrush, redlineBrush, redline, x, barWidth, baseY, trackHeight);

            // Il filo di base continua sotto ogni intervallo di cui si sa qualcosa, e si
            // interrompe sotto i buchi: e' la seconda cosa, oltre al tratteggio, che rende
            // l'assenza visibile anche quando i valori intorno sono tutti bassi.
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
        double average = Math.Clamp(bar.Media, 0d, 1d);
        double peak = Math.Clamp(bar.Massimo, average, 1d);

        double averageHeight = Math.Max(MinBarHeight, average * trackHeight);
        double peakHeight = Math.Max(averageHeight, peak * trackHeight);

        // Una bar che ha coperto solo in parte il suo intervallo si disegna stretta in
        // proporzione: l'ultima della striscia e' sempre l'intervallo IN CORSO, e a step
        // largo si disegnerebbe piena sapendo di pochi minuti. La regola sta in HistoryStrip
        // perche' e' aritmetica e si prova senza disegnare niente.
        barWidth = HistoryStrip.WidthOf(bar, barWidth);

        // Il prolungamento, da dove stava di solito fino a dove e' arrivato. Senza, una
        // barretta bassa con un picco breve e una barretta bassa e basta sarebbero identiche,
        // e il picco - che di solito e' la cosa che si sta cercando - sparirebbe nella average.
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
        // Tratteggio a TUTTA trackHeight, e nessun filo di base sotto. Non e' decorazione: e'
        // l'unico modo perche' "non si sa niente" resti distinguibile da "valueBrush e a
        // riposo", che a schermo e' quasi altrettanto vuoto.
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

    /// <summary>Aggiorna il suggerimento con la bar che sta sotto il puntatore.</summary>
    private void UpdateTip(double x)
    {
        string tipText = Bars is { Count: > 0 } drawnBars
            ? HistoryStrip.Describe(drawnBars, HistoryStrip.IndexAt(x, Bounds.Width, drawnBars.Count))
            : string.Empty;

        // Si riscrive solo quando cambia davvero: assegnare la stessa stringa a ogni pixel di
        // movimento farebbe sfarfallare il suggerimento mentre lo si legge.
        if (!string.Equals(ToolTip.GetTip(this) as string, tipText, StringComparison.Ordinal))
        {
            ToolTip.SetTip(this, tipText.Length == 0 ? null : tipText);
        }
    }
}