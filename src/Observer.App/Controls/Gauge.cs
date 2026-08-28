using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
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
    /// <summary>Quanto dura la corsa della lancetta da un valore al successivo.</summary>
    /// <remarks>
    /// <b>Deve restare piu' breve dell'intervallo di campionamento</b>, e c'e' un test che lo
    /// verifica. Una corsa lunga quanto l'intervallo non finirebbe mai: ogni campione la
    /// farebbe ripartire da una posizione interpolata, e la lancetta non starebbe ferma su un
    /// valore misurato nemmeno per un istante.
    /// <para>
    /// <b>Duecento millisecondi, e il numero e' misurato.</b> Questa e' una finestra che
    /// misura l'uso della CPU, quindi cio' che spende per disegnarsi rientra nel numero che
    /// mostra: e' uno strumento che contribuisce a cio' che segna. Confronto appaiato sullo
    /// stesso banco, due quadranti, otto processori logici, finestra in primo piano, in
    /// Release: senza corsa <b>0,79%</b> di CPU di macchina, con 200 ms <b>1,39%</b>, con
    /// 400 ms <b>3,05%</b>. Raddoppiare la durata quadruplica il sovrapprezzo, perche' quel
    /// che costa e' la frazione di secondo in cui l'animazione gira. A 200 ms il disturbo sta
    /// sotto il punto percentuale e la lancetta non salta piu'; a 400 ms si pagavano oltre due
    /// punti per una morbidezza che a occhio non si distingue.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan Corsa = TimeSpan.FromMilliseconds(200);

    /// <summary>Il valore misurato, da 0 a 1. E' anche cio' che si anima.</summary>
    /// <remarks>
    /// <b>Fra un campione e il successivo la lancetta attraversa posizioni che nessuno ha
    /// misurato</b>, e vale la pena dirlo perche' altrove questo programma non lo fa mai. Qui
    /// e' ammesso per una ragione precisa: una lancetta analogica ha un'inerzia che chi guarda
    /// si aspetta, e la corsa fra due letture si legge come inerzia, non come misura. Il
    /// NUMERO al centro non si anima affatto, ed e' li' che si legge il valore.
    /// <para>
    /// Un tentativo di tenere separate le due cose - una seconda proprieta' animata che
    /// inseguisse questa - e' stato fatto e MISURATO, e va peggio in tutti e due i modi in cui
    /// si puo' scriverlo. Scrivendola a mano da <c>OnPropertyChanged</c>, fra la scrittura del
    /// valore e l'avvio della transizione passa un fotogramma: la lancetta viene disegnata
    /// subito sul valore nuovo, l'animazione la riporta sul vecchio e la fa risalire - avanti,
    /// indietro, avanti, a ogni campione, anche con valori fermi. Tracciato:
    /// <c>"Posizione 0.7657 -> 0.7363 prio=Animation"</c> subito dopo che <c>Fraction</c> era
    /// passata da 0.7363 a 0.7657. Legandola con un <c>Bind</c> nel costruttore, invece,
    /// l'applicazione non si apre proprio.
    /// </para>
    /// </remarks>
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

    // Cio' che non cambia da un fotogramma all'altro, tenuto da parte. Durante la corsa questo
    // Render() gira una sessantina di volte al secondo, e arco di fondo, zona rossa, tacche e
    // testi sono identici in tutti quei fotogrammi: ricostruirli ogni volta significa rifare
    // due layout di testo sessanta volte al secondo per non cambiare un pixel. E' stato
    // misurato che il costo dell'animazione cade tutto sul thread di interfaccia, dentro
    // Render, perche' la proprieta' animata sta in AffectsRender - quindi il lavoro per
    // fotogramma e' la leva vera, piu' della durata.
    private double raggioDisegnato;
    private double sogliaDisegnata;
    private StreamGeometry? fondo;
    private StreamGeometry? zonaRossa;
    private StreamGeometry? tacche;
    private FormattedText? numero;
    private FormattedText? didascalia;
    private string? numeroScritto;
    private string? didascaliaScritta;
    private IBrush? inchiostroDelleScritte;

    static Gauge()
    {
        // Si ridisegna quando si muove la LANCETTA, non quando cambia la misura: fra le due
        // cose ci sta l'animazione, e agganciare qui Fraction farebbe un fotogramma solo per
        // campione, cioe' lo scatto che l'animazione serve a togliere.
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
                Duration = Corsa,

                // Parte subito e arriva morbida. Volutamente NON un easing che sorpassa
                // (BackEaseOut, ElasticEaseOut): su uno strumento di misura mostrerebbero per
                // qualche decimo di secondo un valore piu' alto di quello letto, cioe' un picco
                // che non e' mai successo.
                Easing = new CubicEaseOut(),
            },
        ];
    }

    /// <summary>Il valore misurato, da 0 a 1.</summary>
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

        double dove = GaugeScale.Frazione(Fraction);
        double soglia = GaugeScale.Frazione(Redline);

        RifaiCioCheNonCambia(centro, raggio, spessore, soglia);

        context.DrawGeometry(null, Penna(traccia, spessore), fondo!);

        // La zona rossa si vede anche quando la lancetta e' lontana, ed e' questo che la rende
        // una soglia invece di un allarme: una barra che diventa rossa solo quando e' troppo
        // tardi non dice mai quanto manca.
        if (zonaRossa is not null)
        {
            context.DrawGeometry(null, Penna(rossa, spessore * 0.42d), zonaRossa);
        }

        if (dove > 0d)
        {
            context.DrawGeometry(
                null,
                Penna(dove >= soglia ? rossa : valore, spessore),
                Arco(centro, raggio, 0d, dove));
        }

        context.DrawGeometry(null, Penna(lancetta, Math.Max(1d, spessore * 0.09d)), tacche!);

        DisegnaLancetta(context, centro, raggio, spessore, dove, lancetta);
        DisegnaScritte(context, centro, raggio, lancetta);
    }

    private static Pen Penna(IBrush colore, double spessore) =>
        new(colore, spessore) { LineCap = PenLineCap.Round };

    private static StreamGeometry Arco(Point centro, double raggio, double da, double a)
    {
        double angoloIniziale = GaugeScale.Angolo(da);
        double angoloFinale = GaugeScale.Angolo(a);

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

        return geometria;
    }

    private void RifaiCioCheNonCambia(Point centro, double raggio, double spessore, double soglia)
    {
        if (fondo is not null
            && Math.Abs(raggioDisegnato - raggio) < 0.01d
            && Math.Abs(sogliaDisegnata - soglia) < 0.001d)
        {
            return;
        }

        raggioDisegnato = raggio;
        sogliaDisegnata = soglia;

        fondo = Arco(centro, raggio, 0d, 1d);

        // Sotto il millesimo l'arco e' piu' corto del proprio tratto arrotondato: disegnarlo
        // lascerebbe un pallino sul fondo scala anche dove la zona rossa non comincia.
        zonaRossa = soglia < 0.999d ? Arco(centro, raggio, soglia, 1d) : null;

        tacche = Tacche(centro, raggio, spessore);

        // I testi sono misurati sul raggio: se il raggio e' cambiato, il loro corpo pure.
        numeroScritto = null;
        didascaliaScritta = null;
    }

    private static StreamGeometry Tacche(Point centro, double raggio, double spessore)
    {
        const int intervalli = 10;

        double esterno = raggio - (spessore / 2d) - 2d;
        double interno = Math.Max(1d, esterno - Math.Max(2d, spessore * 0.45d));

        StreamGeometry geometria = new();

        using (StreamGeometryContext penna = geometria.Open())
        {
            for (int i = 0; i <= intervalli; i++)
            {
                double angolo = GaugeScale.AngoloDellaTacca(i, intervalli);

                penna.BeginFigure(GaugeScale.Punto(centro, interno, angolo), isFilled: false);
                penna.LineTo(GaugeScale.Punto(centro, esterno, angolo), isStroked: true);
                penna.EndFigure(isClosed: false);
            }
        }

        return geometria;
    }

    private static void DisegnaLancetta(
        DrawingContext context,
        Point centro,
        double raggio,
        double spessore,
        double dove,
        IBrush colore)
    {
        double angolo = GaugeScale.Angolo(dove);
        double lunghezza = raggio - spessore;

        if (lunghezza <= 0d)
        {
            return;
        }

        // Un pezzetto di lancetta prosegue oltre il perno, come sui quadranti veri: e' cio' che
        // fa leggere l'oggetto come una lancetta imperniata invece che come un raggio.
        context.DrawLine(
            Penna(colore, Math.Max(1.5d, spessore * 0.22d)),
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

        if (!ReferenceEquals(inchiostroDelleScritte, colore))
        {
            // Il colore e' dentro il testo gia' impaginato: se cambia il tema mentre la
            // finestra e' aperta, un testo tenuto da parte resterebbe del colore di prima -
            // scritta chiara su fondo chiaro, cioe' invisibile.
            inchiostroDelleScritte = colore;
            numeroScritto = null;
            didascaliaScritta = null;
        }

        if (!string.IsNullOrEmpty(Display))
        {
            if (numero is null || !string.Equals(numeroScritto, Display, StringComparison.Ordinal))
            {
                numero = Testo(Display, corpo, colore);
                numeroScritto = Display;
            }

            context.DrawText(numero, new Point(centro.X - (numero.Width / 2d), sotto));

            sotto += numero.Height;
        }

        if (!string.IsNullOrEmpty(Caption))
        {
            if (didascalia is null
                || !string.Equals(didascaliaScritta, Caption, StringComparison.Ordinal))
            {
                didascalia = Testo(Caption, Math.Max(8d, corpo * 0.46d), colore);
                didascaliaScritta = Caption;
            }

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