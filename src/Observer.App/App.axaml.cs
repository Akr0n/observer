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
            ClientConfigurationResult configurazione = ClientConfiguration.Read();

            MetricsClient? client = configurazione.Options is { } opzioni
                ? new MetricsClient(opzioni)
                : null;

            // Variabile mutabile e non solo il parametro: se la configurazione compare mentre
            // la finestra e' aperta, il client adottato dopo va comunque chiuso all'uscita.
            MetricsClient? clientCorrente = client;

            MainViewModel viewModel = new(
                client,
                configurazione.Problem,
                rileggiConfigurazione: () =>
                {
                    // Rilegge dal disco: il messaggio a schermo dice all'utente di creare un
                    // file, e crearlo deve bastare. Senza questa rilettura seguirebbe le
                    // istruzioni alla lettera e non succederebbe nulla fino al riavvio.
                    ClientConfigurationResult riletta = ClientConfiguration.Read();

                    if (riletta.Options is not { } opzioniComparse)
                    {
                        return null;
                    }

                    clientCorrente = new MetricsClient(opzioniComparse);
                    return clientCorrente;
                });

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
                clientCorrente?.Dispose();
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
