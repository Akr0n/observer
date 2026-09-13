using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
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
            // Le preferences si leggono UNA volta, qui, e si passano alla finestra: il theme va
            // applicato prima che la finestra esista, perche' un TopLevel copia il theme alla
            // costruzione e dopo si aprirebbe chiaro per poi scattare.
            Preferences preferences = PreferencesStore.Read();
            ApplyTheme(preferences.Theme);

            MachineListResult machineList = MachineDirectory.Read();

            // Ogni client openClient va chiuso all'uscita, compresi quelli nati cambiando macchina
            // nella barra laterale: chiuderne solo l'ultimo lascerebbe indietro un socket per
            // ogni macchina guardata durante la sessione.
            List<MetricsClient> openClients = [];

            MetricsClient Open(ObserverEndpoint endpoint)
            {
                // Se per quel endpoint ne esiste existing' uno, si riusa. Senza, un token che il
                // servizio continua a rifiutare farebbe nascere un client al secondo per
                // sempre: la rilettura scatta a ogni 401, e ogni client si porta dietro il
                // proprio pool di connessioni.
                if (openClients.FirstOrDefault(openClient => openClient.Endpoint == endpoint) is { } existing)
                {
                    return existing;
                }

                MetricsClient newClient = new(endpoint);
                openClients.Add(newClient);

                return newClient;
            }

            // La macchina che si stava guardando l'ultima volta, se e' ancora nell'machineList;
            // altrimenti la prima voce, che e' SEMPRE il canale locale e non ha bisogno di
            // configurazione: dopo l'installazione non c'e' niente da impostare perche' la
            // finestra parta. Chi tiene d'occhio una macchina in rete non deve piu' sceglierla
            // a ogni avvio e aspettare che si colleghi.
            MetricsClient client = Open(
                Preferences.RememberedMachine(machineList.Machines, preferences.MachineName)
                ?? machineList.Machines[0]);

            MainViewModel? viewModel = null;

            viewModel = new MainViewModel(
                client,
                configurationProblem: null,
                // Gli appunti stanno su un TopLevel, cioe' su un controllo: il view model non
                // referenzia Avalonia.Controls, quindi la cucitura si lega qui, dove la
                // finestra c'e' existing'.
                copyToClipboard: text => desktop.MainWindow?.Clipboard?.SetTextAsync(text)
                    ?? Task.CompletedTask,
                rereadConfiguration: () =>
                {
                    // Rilegge dal disco la voce della macchina che si sta guardando. Serve
                    // quando il suo token viene ruotato: senza, la finestra resterebbe bloccata
                    // su "Token rejected" fino al riavvio anche dopo aver corretto il file.
                    if (viewModel?.SelectedMachine?.Endpoint is not { } currentEndpoint)
                    {
                        return null;
                    }

                    ObserverEndpoint? updatedEndpoint = MachineDirectory.Read().Machines.FirstOrDefault(
                        endpoint => endpoint.Kind == currentEndpoint.Kind && endpoint.BaseAddress == currentEndpoint.BaseAddress);

                    return updatedEndpoint is null || updatedEndpoint == currentEndpoint ? null : Open(updatedEndpoint);
                },
                machineList: machineList,
                openMachine: Open,

                // La stessa rilettura di sopra, per una macchina NON guardata la cui sonda
                // torna con un token rifiutato: la voce nuova ha la credenziale nuova.
                rereadEndpoint: endpoint => MachineDirectory.Read().Machines.FirstOrDefault(
                    candidate => candidate.Kind == endpoint.Kind && candidate.BaseAddress == endpoint.BaseAddress));

            CancellationTokenSource shutdown = new();

            desktop.MainWindow = new MainWindow(preferences)
            {
                DataContext = viewModel,
            };

            desktop.Exit += (_, _) =>
            {
                // Cancel ma NON Dispose: il ciclo di aggiornamento e' ancora sospeso su quel
                // token e la sua ripresa e' asincrona, quindi liberare qui la sorgente aprirebbe
                // una finestra in cui il ciclo tocca un oggetto existing' distrutto. Il processo sta
                // uscendo comunque, e non c'e' niente da recuperare.
                shutdown.Cancel();

                foreach (MetricsClient openClient in openClients)
                {
                    openClient.Dispose();
                }
            };

            // Post e non chiamata diretta: qui il ciclo del dispatcher non e' ancora partito
            // e il SynchronizationContext di Avalonia potrebbe non essere installato, quindi
            // le continuazioni degli await rischierebbero di tornare su un thread qualsiasi
            // e di toccare le ObservableCollection fuori dal thread della UI.
            Dispatcher.UIThread.Post(() => _ = viewModel.RunAsync(shutdown.Token));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Applica il theme a tutta l'applicazione: finestre, tendine e barra del titolo.</summary>
    /// <param name="theme">La chiave: <c>system</c>, <c>light</c> o <c>dark</c>.</param>
    /// <remarks>
    /// Sull'applicazione e mai sulla finestra: le tendine e i suggerimenti sono finestre a parte
    /// e prendono il theme da qui. E prima che la finestra esista: un TopLevel copia il theme alla
    /// costruzione. La rilettura del theme di sistema al ritorno a "system" e' una cintura: dal
    /// decompilato, la variant predefinita si limita a cancellare il theme effettivo e i
    /// dizionari potrebbero ricadere su quello chiaro fino al prossimo cambio di colori del
    /// sistema. MISURATO il 2026-09-04 su un Windows 11 in theme isDark, con Avalonia 12.1.1:
    /// senza questa riga, da Dark a System la finestra resta scura, come deve, e la trappola
    /// non si presenta. La riga resta perche' costa una riga, non
    /// ha effetti collaterali (Windows la sovrascrive al prossimo cambio, come farebbe da
    /// solo) e sugli altri backend non e' stata misurata.
    /// </remarks>
    public void ApplyTheme(string theme)
    {
        ThemeVariant variant = ThemeOption.VariantFor(theme);

        RequestedThemeVariant = variant;

        if (variant == ThemeVariant.Default && PlatformSettings is { } settings)
        {
            bool isDark = settings.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;

            SetValue(ActualThemeVariantProperty, isDark ? ThemeVariant.Dark : ThemeVariant.Light);
        }
    }
}