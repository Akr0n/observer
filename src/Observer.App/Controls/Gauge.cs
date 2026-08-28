using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Observer.App.Controls;

/// <summary>
/// Un quadrante da cruscotto: arco graduato, zona rossa e lancetta.
/// </summary>
/// <remarks>
/// Disegnato a mano, e non e' stata una preferenza estetica: e' l'unica strada rimasta dopo
/// aver escluso le altre <b>con una misura</b>. <c>LiveChartsCore.SkiaSharpView.Avalonia</c>
/// 2.0.5 compila e poi lancia <c>MissingFieldException</c> su
/// <c>Avalonia.Input.Gestures.PinchEvent</c> appena si costruisce un grafico, perche' e'
/// compilato contro Avalonia 11; <c>Avalonia.Controls.Charts</c>, che i quadranti li ha gia'
/// pronti, richiede una licenza Avalonia Pro a pagamento. Ogni pacchetto di terze parti qui
/// porta lo stesso rischio che ha ucciso il primo: essere costruito contro una versione di
/// Avalonia diversa da quella in uso. Un controllo che usa solo <see cref="DrawingContext"/>
/// non ha quel rischio, e non ha nulla da aggiornare.
/// <para>
/// La matematica sta tutta in <see cref="GaugeScale"/>, che ha i suoi test. Qui resta il
/// disegno, che nessun test puo' guardare.
/// </para>
/// </remarks>
public sealed class Gauge : Control
{
    /// <summary>Il valore mostrato, da 0 a 1.</summary>
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<Gauge, double>(nameof(Fraction));

    /// <summary>Il numero scritto al centro, gia' formattato.</summary>
    public static readonly StyledProperty<string> DisplayProperty =
        AvaloniaProperty.Register<Gauge, string>(nameof(Display), string.Empty);

    /// <summary>Che cosa misura questo quadrante.</summary>
    public static readonly StyledProperty<string> CaptionProperty =
        AvaloniaProperty.Register<Gauge, string>(nameof(Caption), string.Empty);

    /// <summary>Da dove comincia la zona rossa, da 0 a 1.</summary>
    public static readonly StyledProperty<double> RedlineProperty =
        AvaloniaProperty.Register<Gauge, double>(nameof(Redline), 0.85d);

    /// <summary>Il colore dell'arco non ancora percorso.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<Gauge, IBrush?>(nameof(TrackBrush));

    /// <summary>Il colore dell'arco percorso.</summary>
    public static readonly StyledProperty<IBrush?> ValueBrushProperty =
        AvaloniaProperty.Register<Gauge, IBrush?>(nameof(ValueBrush));

    /// <summary>Il colore della zona rossa e del valore quando ci entra.</summary>
    public static readonly StyledProperty<IBrush?> RedlineBrushProperty =
        AvaloniaProperty.Register<Gauge, IBrush?>(nameof(RedlineBrush));

    /// <summary>Il colore della lancetta e delle scritte.</summary>
    public static readonly StyledProperty<IBrush?> NeedleBrushProperty =
        AvaloniaProperty.Register<Gauge, IBrush?>(nameof(NeedleBrush));

    static Gauge()
    {
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

    /// <summary>Il valore mostrato, da 0 a 1.</summary>
    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    /// <summary>Il numero scritto al centro, gia' formattato.</summary>
    public string Display
    {
        get => GetValue(DisplayProperty);
        set => SetValue(DisplayProperty, value);
    }

    /// <summary>Che cosa misura questo quadrante.</summary>
    public string Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <summary>Da dove comincia la zona rossa, da 0 a 1.</summary>
    public double Redline
    {
        get => GetValue(RedlineProperty);
        set => SetValue(RedlineProperty, value);
    }

    /// <summary>Il colore dell'arco non ancora percorso.</summary>
    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>Il colore dell'arco percorso.</summary>
    public IBrush? ValueBrush
    {
        get => GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    /// <summary>Il colore della zona rossa.</summary>
    public IBrush? RedlineBrush
    {
        get => GetValue(RedlineBrushProperty);
        set => SetValue(RedlineBrushProperty, value);
    }

    /// <summary>Il colore della lancetta e delle scritte.</summary>
    public IBrush? NeedleBrush
    {
        get => GetValue(NeedleBrushProperty);
        set => SetValue(NeedleBrushProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        double lato = Math.Min(Bounds.Width, Bounds.Height);

        if (lato <= 0d)
        {
            return;
        }

        double spessore = Math.Max(3d, lato * 0.11d);
        double raggio = (lato / 2d) - (spessore / 2d) - 1d;

        if (raggio <= 0d)
        {
            return;
        }

        // Il centro sta al centro della larghezza, ma piu' in basso della meta' dell'altezza:
        // l'arco occupa solo i tre quarti superiori del cerchio, e centrarlo davvero lascerebbe
        // uno spazio vuoto in cima mentre le scritte finirebbero contro il bordo di sotto.
        Point centro = new(Bounds.Width / 2d, (Bounds.Height / 2d) + (raggio * 0.10d));

        IBrush traccia = TrackBrush ?? Brushes.Gainsboro;
        IBrush valore = ValueBrush ?? Brushes.SteelBlue;
        IBrush rossa = RedlineBrush ?? Brushes.IndianRed;
        IBrush lancetta = NeedleBrush ?? Brushes.DimGray;

        double frazione = GaugeScale.Frazione(Fraction);
        double soglia = GaugeScale.Frazione(Redline);

        DisegnaArco(context, centro, raggio, spessore, traccia, 0d, 1d);

        // La zona rossa si vede anche quando la lancetta e' lontana, ed e' questo che la rende
        // una soglia invece di un allarme: una barra che diventa rossa solo quando e' troppo
        // tardi non dice mai quanto manca.
        if (soglia < 1d)
        {
            DisegnaArco(context, centro, raggio, spessore * 0.42d, rossa, soglia, 1d);
        }

        if (frazione > 0d)
        {
            DisegnaArco(
                context,
                centro,
                raggio,
                spessore,
                frazione >= soglia ? rossa : valore,
                0d,
                frazione);
        }

        DisegnaTacche(context, centro, raggio, spessore, lancetta);
        DisegnaLancetta(context, centro, raggio, spessore, frazione, lancetta);
        DisegnaScritte(context, centro, raggio, lancetta);
    }

    private static void DisegnaArco(
        DrawingContext context,
        Point centro,
        double raggio,
        double spessore,
        IBrush colore,
        double da,
        double a)
    {
        double angoloIniziale = GaugeScale.Angolo(da);
        double angoloFinale = GaugeScale.Angolo(a);

        // Sotto il centesimo di grado l'arco e' piu' corto del proprio tratto arrotondato:
        // disegnarlo lascerebbe un pallino all'inizio della scala anche a valore zero.
        if (angoloFinale - angoloIniziale < 0.01d)
        {
            return;
        }

        StreamGeometry geometria = new();

        using (StreamGeometryContext penna = geometria.Open())
        {
            penna.BeginFigure(GaugeScale.Punto(centro, raggio, angoloIniziale), isFilled: false);

            penna.ArcTo(
                GaugeScale.Punto(centro, raggio, angoloFinale),
                new Size(raggio, raggio),
                rotationAngle: 0d,
                isLargeArc: angoloFinale - angoloIniziale > 180d,
                sweepDirection: SweepDirection.Clockwise,
                isStroked: true);

            penna.EndFigure(isClosed: false);
        }

        context.DrawGeometry(
            null,
            new Pen(colore, spessore) { LineCap = PenLineCap.Round },
            geometria);
    }

    private static void DisegnaTacche(
        DrawingContext context,
        Point centro,
        double raggio,
        double spessore,
        IBrush colore)
    {
        const int intervalli = 10;

        double esterno = raggio - (spessore / 2d) - 2d;
        double interno = esterno - Math.Max(2d, spessore * 0.45d);

        if (interno <= 0d)
        {
            return;
        }

        Pen penna = new(colore, Math.Max(1d, spessore * 0.09d)) { LineCap = PenLineCap.Round };

        for (int i = 0; i <= intervalli; i++)
        {
            double angolo = GaugeScale.AngoloDellaTacca(i, intervalli);

            context.DrawLine(
                penna,
                GaugeScale.Punto(centro, interno, angolo),
                GaugeScale.Punto(centro, esterno, angolo));
        }
    }

    private static void DisegnaLancetta(
        DrawingContext context,
        Point centro,
        double raggio,
        double spessore,
        double frazione,
        IBrush colore)
    {
        double angolo = GaugeScale.Angolo(frazione);
        double lunghezza = raggio - spessore;

        if (lunghezza <= 0d)
        {
            return;
        }

        // Un pezzetto di lancetta prosegue oltre il perno, come sui quadranti veri: e' cio' che
        // fa leggere l'oggetto come una lancetta imperniata invece che come un raggio.
        context.DrawLine(
            new Pen(colore, Math.Max(1.5d, spessore * 0.22d)) { LineCap = PenLineCap.Round },
            GaugeScale.Punto(centro, -(spessore * 0.5d), angolo),
            GaugeScale.Punto(centro, lunghezza, angolo));

        context.DrawEllipse(colore, null, centro, spessore * 0.30d, spessore * 0.30d);
    }

    private void DisegnaScritte(DrawingContext context, Point centro, double raggio, IBrush colore)
    {
        double corpo = Math.Max(9d, raggio * 0.34d);

        // DrawText posiziona l'ANGOLO IN ALTO A SINISTRA del testo, non la sua linea di base.
        // La didascalia parte quindi da dove il numero finisce davvero, e non da un multiplo
        // scelto a occhio del corpo del numero: quel multiplo era giusto per un corpo solo, e
        // a quadrante piu' piccolo le due scritte si sovrapponevano.
        double sotto = centro.Y + (raggio * 0.28d);

        if (!string.IsNullOrEmpty(Display))
        {
            FormattedText numero = Testo(Display, corpo, colore);

            context.DrawText(numero, new Point(centro.X - (numero.Width / 2d), sotto));

            sotto += numero.Height;
        }

        if (!string.IsNullOrEmpty(Caption))
        {
            FormattedText didascalia = Testo(Caption, Math.Max(8d, corpo * 0.46d), colore);

            context.DrawText(didascalia, new Point(centro.X - (didascalia.Width / 2d), sotto));
        }
    }

    private static FormattedText Testo(string testo, double corpo, IBrush colore) =>
        new(
            testo,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            corpo,
            colore);
}