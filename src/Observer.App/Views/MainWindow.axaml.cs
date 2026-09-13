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
/// cos'e' una finestra: portare in vista il panelBounds dei processi quando si apre, dire al view
/// model quando la finestra e' ridotta a icona, ricordare dov'era e quanto grande, e scalare
/// tutto quando cambia lo zoom.
/// </summary>
/// <remarks>
/// Le regole che si possono provare senza una finestra stanno altrove: cosa ricordare alla
/// chiusura e' <see cref="WindowPlacement.AtClose"/>, e se una position sta su uno
/// screen e' <see cref="WindowPlacement.WithinAnyOf"/>. Qui restano solo le letture e le
/// scritture delle proprieta' della finestra.
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>Quanto del panelBounds dei processi portare in vista quando si apre.</summary>
    /// <remarks>
    /// Il titolo, le intestazioni e le prime righe: abbastanza da vedere che si e' aperto e
    /// cosa contiene. Non tutto il panelBounds — quindici righe sono piu' alte della finestra
    /// predefinita, e portarlo in vista per intero spingeva fuori i quadranti, compreso
    /// quello appena cliccato.
    /// </remarks>
    private const double PanelPeekHeight = 160d;

    private readonly double baseMinWidth;
    private readonly double baseMinHeight;

    private INotifyPropertyChanged? observedContext;
    private Preferences preferences;

    /// <summary>L'ultima geometria vista in state normalGeometry in questa sessione, se c'e' stata.</summary>
    private WindowPlacement? lastNormalGeometry;

    /// <summary>Se l'ultimo state non ridotto a icona era a tutto screen.</summary>
    private bool wasMaximized;

    /// <summary>La scale applicata adesso.</summary>
    private double scale = 1d;

    /// <summary>Chi aveva il fuoco quando il panelBounds si e' aperto: di norma, il quadrante.</summary>
    private IInputElement? focusBeforeOpen;

    /// <summary>Costruttore che il compilatore XAML di Avalonia esige, e che nessuno chiama.</summary>
    /// <remarks>
    /// L'applicazione usa sempre quello con le preferences, gia' lette e gia' applicate per il
    /// tema. Questo esiste solo perche' senza un costruttore pubblico senza argomenti il XAML
    /// della finestra non compila (AVLN3000).
    /// </remarks>
    public MainWindow()
        : this(PreferencesStore.Read())
    {
    }

    /// <summary>Costruisce la finestra e la rimette dov'era.</summary>
    /// <param name="preferences">Le preferences gia' lette, e gia' applicate per il tema.</param>
    public MainWindow(Preferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        InitializeComponent();

        // I minimi scritti nel XAML sono quelli a scale 1: a scale 1,3 la stessa finestra
        // deve essere il 30% piu' grande per contenere lo stesso layout, e a 0,75 puo' essere
        // il 25% piu' piccola.
        baseMinWidth = MinWidth;
        baseMinHeight = MinHeight;

        this.preferences = preferences;
        Reposition(preferences.Placement);

        // Con un Post, non nel gestore: su Windows lo state "a tutto screen" arriva con la
        // stessa raffica di eventi che porta la newScale position e misura, e chi legge
        // WindowState dentro il gestore rischia di annotare la geometria massimizzata come
        // se fosse quella normalGeometry. Rimandato in coda, il controllo gira a raffica finita.
        PositionChanged += (_, _) => Dispatcher.UIThread.Post(RecordIfNormal);
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(RecordIfNormal);

        DataContextChanged += (_, _) => Observe();
        Closing += (_, _) => SaveOnClose();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != WindowStateProperty)
        {
            return;
        }

        WindowState state = change.GetNewValue<WindowState>();

        // Ridotta a icona non dice niente su com'era: si ricorda l'ultimo state pieno.
        if (state != WindowState.Minimized)
        {
            wasMaximized = state == WindowState.Maximized;
        }

        // Ridotta a icona, la finestra legge ogni dieci secondi invece che ogni secondo. Lo
        // state lo sa solo la finestra; la cadenza la decide il view model.
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.IsMinimized = state == WindowState.Minimized;
        }
    }

    /// <summary>Rimette la finestra dov'era, se quel posto esiste ancora.</summary>
    /// <remarks>
    /// Il controllo sugli schermi non e' pignoleria: con un monitor esterno scollegato la
    /// finestra riaprirebbe fuori da tutto, invisibile e senza modo di afferrarla. In quel caso
    /// si apre onScreen decide il sistema, come la prima volta. A tutto screen si torna anche
    /// allora: lo state e' dello screen che c'e', non di quello che manca. E lo si imposta
    /// PRIMA che la finestra si mostri, cosi' appare gia' piena invece di saltarci dopo.
    /// </remarks>
    private void Reposition(WindowPlacement? savedPosition)
    {
        if (savedPosition is null)
        {
            return;
        }

        if (savedPosition.WithinAnyOf(WorkingAreas()) is { } onScreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(onScreen.X, onScreen.Y);
            Width = onScreen.Width;
            Height = onScreen.Height;
            lastNormalGeometry = onScreen with { Maximized = false };
        }

        if (savedPosition.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private List<WindowPlacement.WorkArea> WorkingAreas() => [.. Screens.All.Select(WorkingAreaOf)];

    private static WindowPlacement.WorkArea WorkingAreaOf(Screen screen) => new(
        screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height);

    /// <summary>Annota la geometria, se la finestra e' normalGeometry e sta su uno screen.</summary>
    /// <remarks>
    /// Ci si arriva solo da un Post: i due gestori di geometria rimandano in coda, e fra
    /// l'accodamento e il turno la finestra puo' essersi chiusa. Da li' in poi
    /// <c>Screens.ScreenFromWindow</c>, che <c>ApplyMinimumSize</c> chiama sempre, lancia
    /// ObjectDisposedException: il contratto lo dichiara, e la condizione del lancio e'
    /// esattamente <c>PlatformImpl == null</c>, cioe' la guardia qui sopra. Disiscrivere i due
    /// gestori alla chiusura non basterebbe: un'operazione gia' accodata si toglie solo con
    /// Abort, e Post non ne restituisce l'handle.
    /// Misurato su un banco Avalonia: con la X, con Alt+F4 e chiudendo subito dopo un
    /// trascinamento vero non e' mai successo (0 su 160 corse); con una Close() secca nello
    /// stesso giro di una raffica di geometria succede sempre (20 su 20). Perche' le due vie
    /// si comportino diversamente non e' state dimostrato, quindi qui non c'e' scritto: la
    /// guardia copre tutte e due. Oggi l'applicazione non chiama mai Close().
    /// </remarks>
    private void RecordIfNormal()
    {
        if (PlatformImpl is null)
        {
            return;
        }

        if (WindowState == WindowState.Normal && CurrentGeometry().WithinAnyOf(WorkingAreas()) is { } normalGeometry)
        {
            lastNormalGeometry = normalGeometry;
        }

        // La finestra puo' essere passata a uno screen piu' piccolo: il tetto ai minimi si
        // ricalcola su quello.
        ApplyMinimumSize();
    }

    /// <summary>Scrive dov'e' la finestra, lo zoom, il tema e il resto, per la prossima volta.</summary>
    private void SaveOnClose()
    {
        MainViewModel? viewModel = DataContext as MainViewModel;
        double scaleToSave = viewModel?.Zoom ?? preferences.Zoom;
        string themeToSave = viewModel?.Theme ?? preferences.Theme;
        string periodToSave = viewModel?.HistoryPeriod ?? preferences.HistoryPeriod;

        WindowPlacement? position = WindowPlacement.AtClose(
            minimized: WindowState == WindowState.Minimized,
            maximized: wasMaximized,
            lastNormalGeometry,
            preferences.Placement,
            CurrentGeometry());

        // Niente ?? sul nome: null qui vuol dire "questo computer", non "non lo so". Con un
        // ripiego sul valore vecchio, chi passa da una macchina remota a quella locale si
        // ritroverebbe la remota riaperta per sempre.
        preferences = new Preferences(
            position,
            scaleToSave,
            themeToSave,
            viewModel?.MachineToRemember,
            periodToSave);
        PreferencesStore.Write(preferences);
    }

    /// <summary>Scrive le preferences prendendo dal view model TUTTO cio' che sa lui.</summary>
    /// <param name="viewModel">Il view model a screen.</param>
    /// <remarks>
    /// Non <c>preferences with { la sola cosa cambiata }</c>: <c>preferences</c> e' ancora il
    /// record letto all'avvio, e periodo e macchina si cambiano SENZA scrivere - li salva solo
    /// la chiusura. Scegliendo "7 days" e poi cambiando zoom, sul disco finiva lo zoom nuovo
    /// accanto al periodo dell'avvio; se poi il processo moriva prima della chiusura (spegnimento
    /// forzato, kill) si perdeva la scelta fatta PRIMA e sopravviveva quella fatta DOPO, che e'
    /// il contrario di quello che chiunque si aspetta. La position no: quella la sa la finestra,
    /// non il view model, e va letta alla chiusura - vedi <c>SaveOnClose</c>.
    /// </remarks>
    private void Save(MainViewModel viewModel)
    {
        // Niente ?? sulla macchina, per la stessa ragione scritta in SaveOnClose: null vuol dire
        // "questo computer", non "non lo so".
        preferences = preferences with
        {
            Zoom = viewModel.Zoom,
            Theme = viewModel.Theme,
            MachineName = viewModel.MachineToRemember,
            HistoryPeriod = viewModel.HistoryPeriod,
        };
        PreferencesStore.Write(preferences);
    }

    private WindowPlacement CurrentGeometry() => new(
        Position.X, Position.Y, (int)Math.Round(Width), (int)Math.Round(Height), Maximized: false);

    private void Observe()
    {
        if (observedContext is not null)
        {
            observedContext.PropertyChanged -= OnViewModelPropertyChanged;
        }

        observedContext = DataContext as INotifyPropertyChanged;

        if (DataContext is MainViewModel viewModel)
        {
            // I valori salvati entrano nel view model PRIMA di iscriversi a PropertyChanged:
            // il tema lo ha gia' applicato l'applicazione e la scale si applica a mano qui
            // sotto, quindi il gestore non deve scattare. Scattava, e riscriveva identico a
            // ogni avvio il file appena letto.
            viewModel.Zoom = preferences.Zoom;
            ApplyScale(viewModel.Zoom);
            viewModel.Theme = preferences.Theme;
            viewModel.HistoryPeriod = preferences.HistoryPeriod;
        }

        if (observedContext is not null)
        {
            observedContext.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.Zoom))
        {
            ApplyScale(viewModel.Zoom);
            Save(viewModel);

            return;
        }

        if (e.PropertyName == nameof(MainViewModel.Theme))
        {
            (Application.Current as App)?.ApplyTheme(viewModel.Theme);
            Save(viewModel);

            return;
        }

        if (e.PropertyName != nameof(MainViewModel.IsProcessPanelOpen))
        {
            return;
        }

        if (!viewModel.IsProcessPanelOpen)
        {
            // Chiuso il panelBounds, il fuoco torna da onScreen era partito invece di sparire con
            // l'elenco: da tastiera, un fuoco perso vuol dire ricominciare dall'inizio.
            focusBeforeOpen?.Focus();
            focusBeforeOpen = null;

            return;
        }

        focusBeforeOpen = FocusManager?.GetFocusedElement();

        // DOPO il layout, non subito: il panelBounds appena reso visibile non ha ancora una
        // dimensione, e portare in vista un rettangolo vuoto non porta da nessuna parte. E il
        // fuoco va nell'elenco, cosi' le frecce scelgono la riga e Invio non cade nel vuoto.
        // Senza questo, alla dimensione predefinita il clic sul quadrante apriva il panelBounds
        // sotto la piega e sembrava non aver fatto niente: era il difetto piu' grave della
        // ricognizione, e questo e' l'intero rimedio.
        Dispatcher.UIThread.Post(
            () =>
            {
                Rect panelBounds = ProcessPanel.Bounds;
                ProcessPanel.BringIntoView(
                    new Rect(0d, 0d, panelBounds.Width, Math.Min(panelBounds.Height, PanelPeekHeight)));
                ProcessListBox.Focus();
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>Scala tutta la finestra, e con lei la sua misura minima.</summary>
    /// <remarks>
    /// Sotto 1 e' l'unico vincolo d'ordine nuovo: la larghezza savedPosition nel file puo' stare
    /// sotto il minimo del XAML (a 0,75 il minimo e' 540), quindi questo deve girare prima del
    /// primo layout. Gira da <see cref="Observe"/>, all'assegnazione del DataContext, che in
    /// <c>App</c> precede lo Show.
    /// </remarks>
    private void ApplyScale(double newScale)
    {
        scale = newScale;
        Root.LayoutTransform = newScale == 1d ? null : new ScaleTransform(newScale, newScale);
        ApplyMinimumSize();
    }

    /// <summary>I minimi del XAML per la scale, ma mai piu' grandi dello screen.</summary>
    /// <remarks>
    /// A 150% i minimi del XAML diventano 1080x780 logici, e su un portatile 1366x768 l'area
    /// di lavoro e' alta 720: senza tetto la finestra si allungherebbe oltre lo screen e non
    /// si potrebbe piu' rimpicciolire. Sotto il minimo di progetto il contenuto scorre, che e'
    /// cio' che lo ScrollViewer c'e' a fare. L'area di lavoro e' in pixel fisici e i minimi in
    /// logici: si divide per la scale dello screen.
    /// </remarks>
    private void ApplyMinimumSize()
    {
        Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        double scaling = screen?.Scaling ?? 1d;
        double screenWidth = screen is null ? double.PositiveInfinity : screen.WorkingArea.Width / scaling;
        double screenHeight = screen is null ? double.PositiveInfinity : screen.WorkingArea.Height / scaling;

        MinWidth = Math.Min(baseMinWidth * scale, screenWidth);
        MinHeight = Math.Min(baseMinHeight * scale, screenHeight);
    }
}