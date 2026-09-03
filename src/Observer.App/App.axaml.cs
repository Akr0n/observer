using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.App.Views;

namespace Observer.App;

/// <summary>
/// Radice di composizione dell'applicazione.
/// </summary>
/// <remarks>
/// Niente container di dependency injection: i pezzi da collegare sono tre e un container
/// aggiungerebbe un livello di indirezione senza togliere una riga di codice.
/// </remarks>
public partial class App : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MachineListResult elenco = MachineDirectory.Read();

            // Ogni client aperto va chiuso all'uscita, compresi quelli nati cambiando macchina
            // nella barra laterale: chiuderne solo l'ultimo lascerebbe indietro un socket per
            // ogni macchina guardata durante la sessione.
            List<MetricsClient> aperti = [];

            MetricsClient Apri(ObserverEndpoint punto)
            {
                // Se per quel punto ne esiste gia' uno, si riusa. Senza, un token che il
                // servizio continua a rifiutare farebbe nascere un client al secondo per
                // sempre: la rilettura scatta a ogni 401, e ogni client si porta dietro il
                // proprio pool di connessioni.
                if (aperti.FirstOrDefault(aperto => aperto.Endpoint == punto) is { } gia)
                {
                    return gia;
                }

                MetricsClient nuovo = new(punto);
                aperti.Add(nuovo);

                return nuovo;
            }

            // La prima voce e' SEMPRE il canale locale, che non ha bisogno di configurazione:
            // dopo l'installazione non c'e' niente da impostare perche' la finestra parta.
            MetricsClient client = Apri(elenco.Machines[0]);

            MainViewModel? viewModel = null;

            viewModel = new MainViewModel(
                client,
                problemaDiConfigurazione: null,
                rileggiConfigurazione: () =>
                {
                    // Rilegge dal disco la voce della macchina che si sta guardando. Serve
                    // quando il suo token viene ruotato: senza, la finestra resterebbe bloccata
                    // su "Token rejected" fino al riavvio anche dopo aver corretto il file.
                    if (viewModel?.MacchinaSelezionata?.Punto is not { } corrente)
                    {
                        return null;
                    }

                    ObserverEndpoint? aggiornata = MachineDirectory.Read().Machines.FirstOrDefault(
                        punto => punto.Kind == corrente.Kind && punto.BaseAddress == corrente.BaseAddress);

                    return aggiornata is null || aggiornata == corrente ? null : Apri(aggiornata);
                },
                elenco: elenco,
                apriMacchina: Apri,

                // La stessa rilettura di sopra, per una macchina NON guardata la cui sonda
                // torna con un token rifiutato: la voce nuova ha la credenziale nuova.
                rileggiPunto: punto => MachineDirectory.Read().Machines.FirstOrDefault(
                    candidato => candidato.Kind == punto.Kind && candidato.BaseAddress == punto.BaseAddress));

            CancellationTokenSource arresto = new();

            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };

            desktop.Exit += (_, _) =>
            {
                // Cancel ma NON Dispose: il ciclo di aggiornamento e' ancora sospeso su quel
                // token e la sua ripresa e' asincrona, quindi liberare qui la sorgente aprirebbe
                // una finestra in cui il ciclo tocca un oggetto gia' distrutto. Il processo sta
                // uscendo comunque, e non c'e' niente da recuperare.
                arresto.Cancel();

                foreach (MetricsClient aperto in aperti)
                {
                    aperto.Dispose();
                }
            };

            // Post e non chiamata diretta: qui il ciclo del dispatcher non e' ancora partito
            // e il SynchronizationContext di Avalonia potrebbe non essere installato, quindi
            // le continuazioni degli await rischierebbero di tornare su un thread qualsiasi
            // e di toccare le ObservableCollection fuori dal thread della UI.
            Dispatcher.UIThread.Post(() => _ = viewModel.EseguiAsync(arresto.Token));
        }

        base.OnFrameworkInitializationCompleted();
    }
}