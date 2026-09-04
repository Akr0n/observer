using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Views;

/// <summary>
/// La finestra. Il code-behind fa le cose che il view model non puo' fare perche' non sa
/// cos'e' una finestra: portare in vista il pannello dei processi quando si apre, dire al view
/// model quando la finestra e' ridotta a icona, ricordare dov'era e quanto grande, e scalare
/// tutto quando cambia la misura del testo.
/// </summary>
/// <remarks>
/// Le regole che si possono provare senza una finestra stanno altrove: cosa ricordare alla
/// chiusura e' <see cref="PosizioneFinestra.AllaChiusura"/>, e se una posizione sta su uno
/// schermo e' <see cref="PosizioneFinestra.SuUnoDegli"/>. Qui restano solo le letture e le
/// scritture delle proprieta' della finestra.
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>Quanto del pannello dei processi portare in vista quando si apre.</summary>
    /// <remarks>
    /// Il titolo, le intestazioni e le prime righe: abbastanza da vedere che si e' aperto e
    /// cosa contiene. Non tutto il pannello — quindici righe sono piu' alte della finestra
    /// predefinita, e portarlo in vista per intero spingeva fuori i quadranti, compreso
    /// quello appena cliccato.
    /// </remarks>
    private const double AltezzaDaMostrare = 160d;

    private readonly double larghezzaMinima;
    private readonly double altezzaMinima;

    private INotifyPropertyChanged? osservato;
    private Preferenze preferenze;

    /// <summary>L'ultima geometria vista in stato normale in questa sessione, se c'e' stata.</summary>
    private PosizioneFinestra? ultimaNormale;

    /// <summary>Se l'ultimo stato non ridotto a icona era a tutto schermo.</summary>
    private bool eraMassimizzata;

    /// <summary>La scala applicata adesso.</summary>
    private double scala = 1d;

    /// <summary>Chi aveva il fuoco quando il pannello si e' aperto: di norma, il quadrante.</summary>
    private IInputElement? provenienza;

    /// <summary>Costruttore che il compilatore XAML di Avalonia esige, e che nessuno chiama.</summary>
    /// <remarks>
    /// L'applicazione usa sempre quello con le preferenze, gia' lette e gia' applicate per il
    /// tema. Questo esiste solo perche' senza un costruttore pubblico senza argomenti il XAML
    /// della finestra non compila (AVLN3000).
    /// </remarks>
    public MainWindow()
        : this(PreferenzeStore.Leggi())
    {
    }

    /// <summary>Costruisce la finestra e la rimette dov'era.</summary>
    /// <param name="preferenze">Le preferenze gia' lette, e gia' applicate per il tema.</param>
    public MainWindow(Preferenze preferenze)
    {
        ArgumentNullException.ThrowIfNull(preferenze);

        InitializeComponent();

        // I minimi scritti nel XAML sono quelli a scala 1: a scala 1,3 la stessa finestra
        // deve essere il 30% piu' grande per contenere lo stesso layout, e a 0,75 puo' essere
        // il 25% piu' piccola.
        larghezzaMinima = MinWidth;
        altezzaMinima = MinHeight;

        this.preferenze = preferenze;
        Ricolloca(preferenze.Finestra);

        // Con un Post, non nel gestore: su Windows lo stato "a tutto schermo" arriva con la
        // stessa raffica di eventi che porta la nuova posizione e misura, e chi legge
        // WindowState dentro il gestore rischia di annotare la geometria massimizzata come
        // se fosse quella normale. Rimandato in coda, il controllo gira a raffica finita.
        PositionChanged += (_, _) => Dispatcher.UIThread.Post(AnnotaSeNormale);
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(AnnotaSeNormale);

        DataContextChanged += (_, _) => Osserva();
        Closing += (_, _) => Ricorda();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != WindowStateProperty)
        {
            return;
        }

        WindowState stato = change.GetNewValue<WindowState>();

        // Ridotta a icona non dice niente su com'era: si ricorda l'ultimo stato pieno.
        if (stato != WindowState.Minimized)
        {
            eraMassimizzata = stato == WindowState.Maximized;
        }

        // Ridotta a icona, la finestra legge ogni dieci secondi invece che ogni secondo. Lo
        // stato lo sa solo la finestra; la cadenza la decide il view model.
        if (DataContext is MainViewModel modello)
        {
            modello.InSecondoPiano = stato == WindowState.Minimized;
        }
    }

    /// <summary>Rimette la finestra dov'era, se quel posto esiste ancora.</summary>
    /// <remarks>
    /// Il controllo sugli schermi non e' pignoleria: con un monitor esterno scollegato la
    /// finestra riaprirebbe fuori da tutto, invisibile e senza modo di afferrarla. In quel caso
    /// si apre dove decide il sistema, come la prima volta. A tutto schermo si torna anche
    /// allora: lo stato e' dello schermo che c'e', non di quello che manca. E lo si imposta
    /// PRIMA che la finestra si mostri, cosi' appare gia' piena invece di saltarci dopo.
    /// </remarks>
    private void Ricolloca(PosizioneFinestra? salvata)
    {
        if (salvata is null)
        {
            return;
        }

        if (salvata.SuUnoDegli(Aree()) is { } dove)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(dove.X, dove.Y);
            Width = dove.Width;
            Height = dove.Height;
            ultimaNormale = dove with { Maximized = false };
        }

        if (salvata.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private List<PosizioneFinestra.AreaDiLavoro> Aree() => [.. Screens.All.Select(AreaDi)];

    private static PosizioneFinestra.AreaDiLavoro AreaDi(Screen schermo) => new(
        schermo.WorkingArea.X, schermo.WorkingArea.Y, schermo.WorkingArea.Width, schermo.WorkingArea.Height);

    /// <summary>Annota la geometria, se la finestra e' normale e sta su uno schermo.</summary>
    private void AnnotaSeNormale()
    {
        if (WindowState == WindowState.Normal && Attuale().SuUnoDegli(Aree()) is { } normale)
        {
            ultimaNormale = normale;
        }

        // La finestra puo' essere passata a uno schermo piu' piccolo: il tetto ai minimi si
        // ricalcola su quello.
        ApplicaMinimi();
    }

    /// <summary>Scrive dov'e' la finestra, quanto e' grande il testo e il tema, per la prossima volta.</summary>
    private void Ricorda()
    {
        MainViewModel? modello = DataContext as MainViewModel;
        double scalaDaSalvare = modello?.ScalaTesto ?? preferenze.ScalaTesto;
        string temaDaSalvare = modello?.Tema ?? preferenze.Tema;

        PosizioneFinestra? posizione = PosizioneFinestra.AllaChiusura(
            ridottaAIcona: WindowState == WindowState.Minimized,
            massimizzata: eraMassimizzata,
            ultimaNormale,
            preferenze.Finestra,
            Attuale());

        preferenze = new Preferenze(posizione, scalaDaSalvare, temaDaSalvare);
        PreferenzeStore.Scrivi(preferenze);
    }

    private PosizioneFinestra Attuale() => new(
        Position.X, Position.Y, (int)Math.Round(Width), (int)Math.Round(Height), Maximized: false);

    private void Osserva()
    {
        if (osservato is not null)
        {
            osservato.PropertyChanged -= QuandoCambia;
        }

        osservato = DataContext as INotifyPropertyChanged;

        if (DataContext is MainViewModel modello)
        {
            // I valori salvati entrano nel view model PRIMA di iscriversi a PropertyChanged:
            // il tema lo ha gia' applicato l'applicazione e la scala si applica a mano qui
            // sotto, quindi il gestore non deve scattare. Scattava, e riscriveva identico a
            // ogni avvio il file appena letto.
            modello.ScalaTesto = preferenze.ScalaTesto;
            ApplicaScala(modello.ScalaTesto);
            modello.Tema = preferenze.Tema;
        }

        if (osservato is not null)
        {
            osservato.PropertyChanged += QuandoCambia;
        }
    }

    private void QuandoCambia(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not MainViewModel modello)
        {
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.ScalaTesto))
        {
            ApplicaScala(modello.ScalaTesto);
            preferenze = preferenze with { ScalaTesto = modello.ScalaTesto };
            PreferenzeStore.Scrivi(preferenze);

            return;
        }

        if (e.PropertyName == nameof(MainViewModel.Tema))
        {
            (Application.Current as App)?.ApplicaTema(modello.Tema);
            preferenze = preferenze with { Tema = modello.Tema };
            PreferenzeStore.Scrivi(preferenze);

            return;
        }

        if (e.PropertyName != nameof(MainViewModel.ProcessiVisibili))
        {
            return;
        }

        if (!modello.ProcessiVisibili)
        {
            // Chiuso il pannello, il fuoco torna da dove era partito invece di sparire con
            // l'elenco: da tastiera, un fuoco perso vuol dire ricominciare dall'inizio.
            provenienza?.Focus();
            provenienza = null;

            return;
        }

        provenienza = FocusManager?.GetFocusedElement();

        // DOPO il layout, non subito: il pannello appena reso visibile non ha ancora una
        // dimensione, e portare in vista un rettangolo vuoto non porta da nessuna parte. E il
        // fuoco va nell'elenco, cosi' le frecce scelgono la riga e Invio non cade nel vuoto.
        // Senza questo, alla dimensione predefinita il clic sul quadrante apriva il pannello
        // sotto la piega e sembrava non aver fatto niente: era il difetto piu' grave della
        // ricognizione, e questo e' l'intero rimedio.
        Dispatcher.UIThread.Post(
            () =>
            {
                Rect pannello = PannelloProcessi.Bounds;
                PannelloProcessi.BringIntoView(
                    new Rect(0d, 0d, pannello.Width, Math.Min(pannello.Height, AltezzaDaMostrare)));
                ElencoProcessi.Focus();
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>Scala tutta la finestra, e con lei la sua misura minima.</summary>
    /// <remarks>
    /// Sotto 1 e' l'unico vincolo d'ordine nuovo: la larghezza salvata nel file puo' stare
    /// sotto il minimo del XAML (a 0,75 il minimo e' 540), quindi questo deve girare prima del
    /// primo layout. Gira da <see cref="Osserva"/>, all'assegnazione del DataContext, che in
    /// <c>App</c> precede lo Show.
    /// </remarks>
    private void ApplicaScala(double nuova)
    {
        scala = nuova;
        Radice.LayoutTransform = nuova == 1d ? null : new ScaleTransform(nuova, nuova);
        ApplicaMinimi();
    }

    /// <summary>I minimi del XAML per la scala, ma mai piu' grandi dello schermo.</summary>
    /// <remarks>
    /// A 150% i minimi del XAML diventano 1080x780 logici, e su un portatile 1366x768 l'area
    /// di lavoro e' alta 720: senza tetto la finestra si allungherebbe oltre lo schermo e non
    /// si potrebbe piu' rimpicciolire. Sotto il minimo di progetto il contenuto scorre, che e'
    /// cio' che lo ScrollViewer c'e' a fare. L'area di lavoro e' in pixel fisici e i minimi in
    /// logici: si divide per la scala dello schermo.
    /// </remarks>
    private void ApplicaMinimi()
    {
        Screen? schermo = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        double fattore = schermo?.Scaling ?? 1d;
        double larghezzaSchermo = schermo is null ? double.PositiveInfinity : schermo.WorkingArea.Width / fattore;
        double altezzaSchermo = schermo is null ? double.PositiveInfinity : schermo.WorkingArea.Height / fattore;

        MinWidth = Math.Min(larghezzaMinima * scala, larghezzaSchermo);
        MinHeight = Math.Min(altezzaMinima * scala, altezzaSchermo);
    }
}