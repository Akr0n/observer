using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Observer.App.ViewModels;

namespace Observer.App.Views;

/// <summary>
/// La finestra. Il code-behind fa le DUE cose che il view model non puo' fare perche' non sa
/// cos'e' una finestra: portare in vista il pannello dei processi quando si apre, e dire al
/// view model quando la finestra e' ridotta a icona.
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

    private INotifyPropertyChanged? osservato;

    /// <summary>Chi aveva il fuoco quando il pannello si e' aperto: di norma, il quadrante.</summary>
    private IInputElement? provenienza;

    /// <summary>Costruisce la finestra.</summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Osserva();
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
    }

    private void QuandoCambia(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ProcessiVisibili)
            || DataContext is not MainViewModel modello)
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
}