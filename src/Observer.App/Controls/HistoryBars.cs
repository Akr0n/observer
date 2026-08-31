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
/// cioe' barrette alte un pixel per tutta la striscia. Rendere l'assenza ancora piu' tenue la
/// renderebbe indistinguibile dal riposo, e la striscia direbbe "tutto tranquillo" proprio nei
/// periodi in cui non si sa niente. Per questo un buco porta un tratteggio a tutta altezza e
/// interrompe il filo di base, mentre una misura, anche di zero, ha sempre un piede solido.
/// </para>
/// <para>
/// L'altezza porta l'informazione principale e il colore la raddoppia, mai il contrario: chi
/// non distingue il rosso dal verde legge comunque la striscia, perche' un intervallo carico
/// e' <i>alto</i> prima ancora che colorato.
/// </para>
/// </remarks>
public sealed class HistoryBars : Control
{
    private const double Distanza = 1d;
    private const double Filo = 1.5d;

    /// <summary>L'altezza minima di una colonna misurata.</summary>
    /// <remarks>
    /// Uno zero misurato deve avere un piede visibile. Il pavimento puo' solo SOVRASTIMARE un
    /// valore piccolo, mai sottostimarlo: sbagliare per eccesso su un due per cento costa un
    /// paio di pixel, sbagliare per difetto vuol dire far sparire la prova che li' si stava
    /// misurando.
    /// </remarks>
    private const double Pavimento = 3d;

    /// <summary>Gli intervalli da disegnare, dal piu' vecchio al piu' recente.</summary>
    public static readonly StyledProperty<IReadOnlyList<HistoryBar>?> BarsProperty =
        AvaloniaProperty.Register<HistoryBars, IReadOnlyList<HistoryBar>?>(nameof(Bars));

    /// <summary>Da dove un intervallo si considera carico, da 0 a 1.</summary>
    public static readonly StyledProperty<double> RedlineProperty =
        AvaloniaProperty.Register<HistoryBars, double>(nameof(Redline), 0.85d);

    /// <summary>Il fondo della striscia, cioe' l'area che va da zero al massimo.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<HistoryBars, IBrush?>(nameof(TrackBrush));

    /// <summary>Il colore di un intervallo misurato.</summary>
    public static readonly StyledProperty<IBrush?> ValueBrushProperty =
        AvaloniaProperty.Register<HistoryBars, IBrush?>(nameof(ValueBrush));

    /// <summary>Il colore di un intervallo che ha toccato la soglia.</summary>
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

    /// <summary>Da dove un intervallo si considera carico.</summary>
    public double Redline
    {
        get => GetValue(RedlineProperty);
        set => SetValue(RedlineProperty, value);
    }

    /// <summary>Il fondo della striscia.</summary>
    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>Il colore di un intervallo misurato.</summary>
    public IBrush? ValueBrush
    {
        get => GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    /// <summary>Il colore di un intervallo che ha toccato la soglia.</summary>
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
    /// e' un controllo solo: senza questo direbbe la stessa cosa su tutta la larghezza, cioe'
    /// non direbbe niente.
    /// </remarks>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnPointerMoved(e);

        Suggerisci(e.GetPosition(this).X);
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

        if (Bars is not { Count: > 0 } barrette || Bounds.Width <= 2d || Bounds.Height <= 6d)
        {
            return;
        }

        IBrush fondo = TrackBrush ?? Brushes.Gainsboro;
        IBrush misurato = ValueBrush ?? Brushes.SteelBlue;
        IBrush carico = RedlineBrush ?? Brushes.IndianRed;
        IBrush assente = MissingBrush ?? Brushes.DimGray;

        double soglia = Math.Clamp(Redline, 0d, 1d);

        // Un fondo unico per tutta la striscia, e non uno per barretta: cosi' l'estensione
        // della finestra si vede sempre, anche quando non c'e' un solo dato dentro.
        double baseY = Bounds.Height - Filo;
        double altezza = baseY;

        context.FillRectangle(fondo, new Rect(0d, 0d, Bounds.Width, altezza), 2f);

        double passo = Bounds.Width / barrette.Count;
        double larghezza = Math.Max(1d, passo - Distanza);

        for (int i = 0; i < barrette.Count; i++)
        {
            double x = i * passo;

            if (barrette[i].Genere == BarKind.Assente)
            {
                Buco(context, assente, x, larghezza, baseY, altezza);

                continue;
            }

            Colonna(context, barrette[i], misurato, carico, soglia, x, larghezza, baseY, altezza);

            // Il filo di base continua sotto ogni intervallo di cui si sa qualcosa, e si
            // interrompe sotto i buchi: e' la seconda cosa, oltre al tratteggio, che rende
            // l'assenza visibile anche quando i valori intorno sono tutti bassi.
            context.FillRectangle(assente, new Rect(x, baseY, larghezza, Filo));
        }
    }

    private static void Colonna(
        DrawingContext context,
        HistoryBar barra,
        IBrush misurato,
        IBrush carico,
        double soglia,
        double x,
        double larghezza,
        double baseY,
        double altezza)
    {
        double media = Math.Clamp(barra.Media, 0d, 1d);
        double massimo = Math.Clamp(barra.Massimo, media, 1d);

        double alta = Math.Max(Pavimento, media * altezza);
        double punta = Math.Max(alta, massimo * altezza);

        // Il prolungamento, da dove stava di solito fino a dove e' arrivato. Senza, una
        // barretta bassa con un picco breve e una barretta bassa e basta sarebbero identiche,
        // e il picco - che di solito e' la cosa che si sta cercando - sparirebbe nella media.
        if (punta - alta > 0.5d)
        {
            context.FillRectangle(
                new SolidColorBrush(Tinta(massimo >= soglia ? carico : misurato), 0.32d),
                new Rect(x, baseY - punta, larghezza, punta - alta));
        }

        context.FillRectangle(
            media >= soglia ? carico : misurato,
            new Rect(x, baseY - alta, larghezza, alta));
    }

    private static void Buco(
        DrawingContext context,
        IBrush assente,
        double x,
        double larghezza,
        double baseY,
        double altezza)
    {
        // Tratteggio a TUTTA altezza, e nessun filo di base sotto. Non e' decorazione: e'
        // l'unico modo perche' "non si sa niente" resti distinguibile da "misurato e a
        // riposo", che a schermo e' quasi altrettanto vuoto.
        double centro = x + (larghezza / 2d) - 0.5d;
        double y = baseY - altezza;

        while (y < baseY)
        {
            double fine = Math.Min(y + 2d, baseY);

            context.FillRectangle(assente, new Rect(centro, y, 1d, fine - y));

            y += 4d;
        }
    }

    private static Color Tinta(IBrush pennello) =>
        pennello is ISolidColorBrush solido ? solido.Color : Colors.Gray;

    /// <summary>Aggiorna il suggerimento con la barra che sta sotto il puntatore.</summary>
    private void Suggerisci(double x)
    {
        string testo = Bars is { Count: > 0 } barrette
            ? HistoryStrip.Descrivi(barrette, HistoryStrip.IndiceSotto(x, Bounds.Width, barrette.Count))
            : string.Empty;

        // Si riscrive solo quando cambia davvero: assegnare la stessa stringa a ogni pixel di
        // movimento farebbe sfarfallare il suggerimento mentre lo si legge.
        if (!string.Equals(ToolTip.GetTip(this) as string, testo, StringComparison.Ordinal))
        {
            ToolTip.SetTip(this, testo.Length == 0 ? null : testo);
        }
    }
}