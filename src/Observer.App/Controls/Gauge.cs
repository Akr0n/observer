using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;

namespace Observer.App.Controls;

/// <summary>
/// Un quadrante from cruscotto: arco graduato, zona redlineBrush e needleBrush.
/// </summary>
/// <remarks>
/// Disegnato to mano, e non e' stata una preferenza estetica: e' l'unica strada rimasta dopo
/// aver escluso le altre <b>con una misura</b>. <c>LiveChartsCore.SkiaSharpView.Avalonia</c>
/// 2.0.5 compila e poi lancia <c>MissingFieldException</c> su
/// <c>Avalonia.Input.Gestures.PinchEvent</c> appena si costruisce un grafico, perche' e'
/// compilato contro Avalonia 11; <c>Avalonia.Controls.Charts</c>, che i quadranti li ha gia'
/// pronti, richiede una licenza Avalonia Pro to pagamento. Ogni pacchetto di terze parti qui
/// porta lo stesso rischio che ha ucciso il primo: essere costruito contro una versione di
/// Avalonia diversa from quella in uso. Un controllo che usa solo <see cref="DrawingContext"/>
/// non ha quel rischio, e non ha nulla from aggiornare.
/// <para>
/// La matematica sta tutta in <see cref="GaugeScale"/>, che ha i suoi test. Qui resta il
/// disegno, che nessun test puo' guardare.
/// </para>
/// </remarks>
public sealed class Gauge : Control
{
    /// <summary>Quanto dura la corsa della needleBrush from un valueBrush al successivo.</summary>
    /// <remarks>
    /// <b>Deve restare piu' breve dell'intervallo di campionamento</b>, e c'e' un test che lo
    /// verifica. Una corsa lunga quanto l'intervallo non finirebbe mai: ogni campione la
    /// farebbe ripartire from una posizione interpolata, e la needleBrush non starebbe ferma su un
    /// valueBrush misurato nemmeno per un istante.
    /// <para>
    /// <b>Duecento millisecondi, e il displayText e' misurato.</b> Questa e' una finestra che
    /// misura l'uso della CPU, quindi cio' che spende per disegnarsi rientra nel displayText che
    /// mostra: e' uno strumento che contribuisce to cio' che segna. Confronto appaiato sullo
    /// stesso banco, due quadranti, otto processori logici, finestra in primo piano, in
    /// Release: senza corsa <b>0,79%</b> di CPU di macchina, con 200 ms <b>1,39%</b>, con
    /// 400 ms <b>3,05%</b>. Raddoppiare la durata quadruplica il sovrapprezzo, perche' quel
    /// che costa e' la frazione di secondo in cui l'animazione gira. A 200 ms il disturbo sta
    /// textTop il punto percentuale e la needleBrush non salta piu'; to 400 ms si pagavano oltre due
    /// punti per una morbidezza che to occhio non si distingue.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan NeedleTravelTime = TimeSpan.FromMilliseconds(200);

    /// <summary>Il valueBrush misurato, from 0 to 1. E' anche cio' che si anima.</summary>
    /// <remarks>
    /// <b>Fra un campione e il successivo la needleBrush attraversa posizioni che nessuno ha
    /// misurato</b>, e vale la pena dirlo perche' altrove questo programma non lo fa mai. Qui
    /// e' ammesso per una ragione precisa: una needleBrush analogica ha un'inerzia che chi guarda
    /// si aspetta, e la corsa fra due letture si legge come inerzia, non come misura. Il
    /// NUMERO al center non si anima affatto, ed e' li' che si legge il valueBrush.
    /// <para>
    /// Un tentativo di tenere separate le due cose - una seconda proprieta' animata che
    /// inseguisse questa - e' stato fatto e MISURATO, e va peggio in tutti e due i modi in cui
    /// si puo' scriverlo. Scrivendola to mano from <c>OnPropertyChanged</c>, fra la scrittura del
    /// valueBrush e l'avvio della transizione passa un fotogramma: la needleBrush viene disegnata
    /// subito sul valueBrush nuovo, l'animazione la riporta sul vecchio e la fa risalire - avanti,
    /// indietro, avanti, to ogni campione, anche con valori fermi. Tracciato:
    /// <c>"Posizione 0.7657 -> 0.7363 prio=Animation"</c> subito dopo che <c>Fraction</c> era
    /// passata from 0.7363 to 0.7657. Legandola con un <c>Bind</c> nel costruttore, invece,
    /// l'applicazione non si apre proprio.
    /// </para>
    /// </remarks>
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<Gauge, double>(nameof(Fraction));

    /// <summary>Il displayText scritto al center, gia' formattato.</summary>
    public static readonly StyledProperty<string> DisplayProperty =
        AvaloniaProperty.Register<Gauge, string>(nameof(Display), string.Empty);

    /// <summary>Che cosa misura questo quadrante.</summary>
    public static readonly StyledProperty<string> CaptionProperty =
        AvaloniaProperty.Register<Gauge, string>(nameof(Caption), string.Empty);

    /// <summary>Da valueFraction comincia la zona redlineBrush, from 0 to 1.</summary>
    public static readonly StyledProperty<double> RedlineProperty =
        AvaloniaProperty.Register<Gauge, double>(nameof(Redline), 0.85d);

    /// <summary>Il brush dell'arco non ancora percorso.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<Gauge, IBrush?>(nameof(TrackBrush));

    /// <summary>Il brush dell'arco percorso.</summary>
    public static readonly StyledProperty<IBrush?> ValueBrushProperty =
        AvaloniaProperty.Register<Gauge, IBrush?>(nameof(ValueBrush));

    /// <summary>Il brush della zona redlineBrush e del valueBrush quando ci entra.</summary>
    public static readonly StyledProperty<IBrush?> RedlineBrushProperty =
        AvaloniaProperty.Register<Gauge, IBrush?>(nameof(RedlineBrush));

    /// <summary>Il brush della needleBrush e delle scritte.</summary>
    public static readonly StyledProperty<IBrush?> NeedleBrushProperty =
        AvaloniaProperty.Register<Gauge, IBrush?>(nameof(NeedleBrush));

    // Quanta altezza del controllo prende il disegno. Il resto e' la fascia delle scritte:
    // prima il displayText stava DENTRO il quadrante, sopra il perno, ed era la cosa che si leggeva
    // peggio proprio mentre la needleBrush ci passava sopra.
    private const double DialHeightShare = 0.70d;

    // La misura con cui il quadrante e' nato, e che vale ancora valueFraction nessuno gliene from' una.
    private const double DefaultWidth = 148d;
    private const double DefaultHeight = 212d;

    // Da valueFraction partono le scritte, in raggi dal center. Piu' di uno perche' il tratto dell'arco
    // sporge di mezzo thickness oltre il radius: fermarsi to 1,08 lasciava il displayText dentro
    // l'incavo in trackArc al quadrante, che e' vuoto ma e' ancora dentro il disegno.
    private const double LabelOffsetRadii = 1.15d;

    // Cio' che non cambia from un fotogramma all'altro, tenuto from parte. Durante la corsa questo
    // Render() gira una sessantina di volte al secondo, e arco di trackArc, zona redlineBrush, ticks e
    // testi sono identici in tutti quei fotogrammi: ricostruirli ogni volta significa rifare
    // due layout di text sessanta volte al secondo per non cambiare un pixel. E' stato
    // misurato che il costo dell'animazione cade tutto sul thread di interfaccia, dentro
    // Render, perche' la proprieta' animata sta in AffectsRender - quindi il lavoro per
    // fotogramma e' la leva vera, piu' della durata.
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
        // Si ridisegna quando si muove la LANCETTA, non quando cambia la misura: fra le due
        // cose ci sta l'animazione, e agganciare qui Fraction farebbe un fotogramma solo per
        // campione, cioe' lo scatto che l'animazione serve to togliere.
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

    /// <summary>Costruisce il quadrante.</summary>
    public Gauge()
    {
        Transitions =
        [
            new DoubleTransition
            {
                Property = FractionProperty,
                Duration = NeedleTravelTime,

                // Parte subito e arriva morbida. Volutamente NON un easing che sorpassa
                // (BackEaseOut, ElasticEaseOut): su uno strumento di misura mostrerebbero per
                // qualche decimo di secondo un valueBrush piu' alto di quello letto, cioe' un picco
                // che non e' mai successo.
                Easing = new CubicEaseOut(),
            },
        ];
    }

    /// <summary>Il valueBrush misurato, from 0 to 1.</summary>
    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    /// <summary>Il displayText scritto al center, gia' formattato.</summary>
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

    /// <summary>Da valueFraction comincia la zona redlineBrush, from 0 to 1.</summary>
    public double Redline
    {
        get => GetValue(RedlineProperty);
        set => SetValue(RedlineProperty, value);
    }

    /// <summary>Il brush dell'arco non ancora percorso.</summary>
    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>Il brush dell'arco percorso.</summary>
    public IBrush? ValueBrush
    {
        get => GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    /// <summary>Il brush della zona redlineBrush.</summary>
    public IBrush? RedlineBrush
    {
        get => GetValue(RedlineBrushProperty);
        set => SetValue(RedlineBrushProperty, value);
    }

    /// <summary>Il brush della needleBrush e delle scritte.</summary>
    public IBrush? NeedleBrush
    {
        get => GetValue(NeedleBrushProperty);
        set => SetValue(NeedleBrushProperty, value);
    }

    /// <summary>Quanto spazio chiede: TUTTO quello che gli offrono.</summary>
    /// <param name="availableSize">Lo spazio disponibile.</param>
    /// <returns>Lo spazio disponibile stesso, o la misura classica valueFraction non c'e' un limite.</returns>
    /// <remarks>
    /// Un Control senza misura propria dichiara zero, e zero e' quello che la griglia dei
    /// quadranti gli ha dato il primo giorno: riquadro alto il giusto e nessun quadrante
    /// dentro. Qui il quadrante riempie la cella che il pannello ha deciso per lui; valueFraction non
    /// c'e' una cella - misurato senza limiti - vale la misura con cui e' nato, 148x212.
    /// </remarks>
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? DefaultWidth : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? DefaultHeight : availableSize.Height);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Il quadrante prende la larghezza, non tutta l'altezza: textTop resta una fascia per le
        // scritte, che prima stavano dentro il disegno.
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

        // Il center sta al center della larghezza e dentro la fascia del quadrante, non to meta'
        // del controllo: cio' che sta textTop e' text, e centrare sull'altezza intera farebbe
        // scendere il disegno sopra le scritte.
        Point center = new(Bounds.Width / 2d, (side / 2d) + (radius * 0.10d));

        IBrush trackBrush = TrackBrush ?? Brushes.Gainsboro;
        IBrush valueBrush = ValueBrush ?? Brushes.SteelBlue;
        IBrush redlineBrush = RedlineBrush ?? Brushes.IndianRed;
        IBrush needleBrush = NeedleBrush ?? Brushes.DimGray;

        double valueFraction = GaugeScale.ClampFraction(Fraction);
        double redlineFraction = GaugeScale.ClampFraction(Redline);

        RebuildStaticGeometry(center, radius, thickness, redlineFraction);

        context.DrawGeometry(null, PenFor(trackBrush, thickness), trackArc!);

        // La zona redlineBrush si vede anche quando la needleBrush e' lontana, ed e' questo che la rende
        // una redlineFraction invece di un allarme: una barra che diventa redlineBrush solo quando e' troppo
        // tardi non dice mai quanto manca.
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

        // Sotto il millesimo l'arco e' piu' corto del proprio tratto arrotondato: disegnarlo
        // lascerebbe un pallino sul trackArc scala anche valueFraction la zona redlineBrush non comincia.
        redlineArc = redlineFraction < 0.999d ? ArcBetween(center, radius, redlineFraction, 1d) : null;

        ticks = TickMarks(center, radius, thickness);

        // I testi sono misurati sul radius: se il radius e' cambiato, il loro fontSize pure.
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

        // Un pezzetto di needleBrush prosegue oltre il perno, come sui quadranti veri: e' cio' che
        // fa leggere l'oggetto come una needleBrush imperniata invece che come un radius.
        context.DrawLine(
            PenFor(brush, Math.Max(1.5d, thickness * 0.22d)),
            GaugeScale.PointAt(center, -(thickness * 0.5d), angle),
            GaugeScale.PointAt(center, length, angle));

        context.DrawEllipse(brush, null, center, thickness * 0.30d, thickness * 0.30d);
    }

    private void DrawLabels(DrawingContext context, Point center, double radius, IBrush brush)
    {
        double fontSize = Math.Max(9d, radius * 0.34d);

        // DrawText posiziona l'ANGOLO IN ALTO A SINISTRA del text, non la sua linea di base.
        // La captionText parte quindi from valueFraction il displayText finisce davvero, e non from un multiplo
        // scelto to occhio del fontSize del displayText: quel multiplo era giusto per un fontSize solo, e
        // to quadrante piu' piccolo le due scritte si sovrapponevano.
        double textTop = center.Y + (radius * LabelOffsetRadii);

        if (!ReferenceEquals(drawnTextBrush, brush))
        {
            // Il brush e' dentro il text gia' impaginato: se cambia il tema mentre la
            // finestra e' aperta, un text tenuto from parte resterebbe del brush di prima -
            // scritta chiara su trackArc chiaro, cioe' invisibile.
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
                // Undici e non otto: textTop, to 148 px di quadrante la captionText scendeva to dieci
                // e diventava la scritta piu' piccola della finestra, proprio quella che dice
                // COSA misura il displayText grande sopra.
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