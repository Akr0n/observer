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

    /// <summary>Chi aveva il fuoco quando il pannello si e' aperto: di norma, il quadrante.</summary>
    private IInputElement? provenienza;

    /// <summary>Costruisce la finestra e la rimette dov'era.</summary>
    public MainWindow()
    {
        InitializeComponent();

        // I minimi scritti nel XAML sono quelli a scala 1: a scala 1,3 la stessa finestra
        // deve essere il 30% piu' grande per contenere lo stesso layout.
        larghezzaMinima = MinWidth;
        altezzaMinima = MinHeight;

        preferenze = PreferenzeStore.Leggi();
        Ricolloca(preferenze.Finestra);

        DataContextChanged += (_, _) => Osserva();
        Opened += (_, _) => RiapriATuttoSchermoSeLoEra();
        Closing += (_, _) => Ricorda();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Ridotta a icona, la finestra legge ogni dieci secondi invece che ogni secondo. Lo
        // stato lo sa solo la finestra; la cadenza la decide il view model.
        if (change.Property == WindowStateProperty && DataContext is MainViewModel modello)
        {
            modello.InSecondoPiano = change.GetNewValue<WindowState>() == WindowState.Minimized;
        }
    }

    /// <summary>Rimette la finestra dov'era, se quel posto esiste ancora.</summary>
    /// <remarks>
    /// Il controllo sugli schermi non e' pignoleria: con un monitor esterno scollegato la
    /// finestra riaprirebbe fuori da tutto, invisibile e senza modo di afferrarla. In quel caso
    /// si apre dove decide il sistema, come la prima volta.
    /// </remarks>
    private void Ricolloca(PosizioneFinestra? salvata)
    {
        if (salvata is null)
        {
            return;
        }

        List<PosizioneFinestra.AreaDiLavoro> aree = [.. Screens.All.Select(AreaDi)];

        if (salvata.SuUnoDegli(aree) is not { } dove)
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(dove.X, dove.Y);
        Width = dove.Width;
        Height = dove.Height;
    }

    private static PosizioneFinestra.AreaDiLavoro AreaDi(Screen schermo) => new(
        schermo.WorkingArea.X, schermo.WorkingArea.Y, schermo.WorkingArea.Width, schermo.WorkingArea.Height);

    /// <summary>A tutto schermo si torna solo DOPO che la finestra esiste: prima non ha uno schermo.</summary>
    private void RiapriATuttoSchermoSeLoEra()
    {
        if (preferenze.Finestra is { Maximized: true })
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Scrive dov'e' la finestra e quanto e' grande il testo, per la prossima volta.</summary>
    /// <remarks>
    /// A tutto schermo si ricordano la posizione e le misure di PRIMA, gia' salvate, piu' il
    /// fatto di esserlo: le misure di una finestra massimizzata sono quelle dello schermo, e
    /// ripristinarle darebbe una finestra "normale" grande come lo schermo. Ridotta a icona
    /// non si ricorda niente di nuovo: la posizione e' fuori da ogni schermo.
    /// </remarks>
    private void Ricorda()
    {
        double scala = DataContext is MainViewModel modello ? modello.ScalaTesto : preferenze.ScalaTesto;

        PosizioneFinestra? posizione = WindowState switch
        {
            WindowState.Minimized => preferenze.Finestra,
            WindowState.Maximized => (preferenze.Finestra ?? Attuale()) with { Maximized = true },
            _ => Attuale(),
        };

        preferenze = new Preferenze(posizione, scala);
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

        if (osservato is not null)
        {
            osservato.PropertyChanged += QuandoCambia;
        }

        if (DataContext is MainViewModel modello)
        {
            // La scala salvata entra nel view model (che la mostra nel selettore) e da li'
            // torna qui attraverso PropertyChanged, come ogni scelta successiva dell'utente.
            // Se e' quella normale non cambia niente e non scatta niente: si applica a mano.
            modello.ScalaTesto = preferenze.ScalaTesto;
            ApplicaScala(modello.ScalaTesto);
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
    private void ApplicaScala(double scala)
    {
        Radice.LayoutTransform = scala == 1d ? null : new ScaleTransform(scala, scala);
        MinWidth = larghezzaMinima * scala;
        MinHeight = altezzaMinima * scala;
    }
}