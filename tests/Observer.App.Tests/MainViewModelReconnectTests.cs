using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>
/// Cosa succede quando la configurazione manca all'avvio e compare dopo.
/// </summary>
/// <remarks>
/// E' il caso NORMALE, non un caso limite: il messaggio "Configuration missing" dice
/// all'utente di creare un file. Se creare quel file non produce alcun effetto finche' non
/// riavvia — e il messaggio non glielo dice — l'utente segue le istruzioni alla lettera e
/// conclude che l'applicazione e' rotta. E' successo davvero.
/// </remarks>
public class MainViewModelReconnectTests
{
    [Fact]
    public async Task ConfigurationAppearsAfterStartup_TheApplicationConnectsWithoutARestart()
    {
        // All'avvio non c'e' configurazione; alla prima rilettura ne compare una valida.
        FakeMetricsClient client = new();
        int rereads = 0;

        MainViewModel viewModel = new(
            client: null,
            configurationProblem: "manca il token",
            rereadConfiguration: () =>
            {
                rereads++;
                return client;
            });

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
        Task loop = viewModel.RunAsync(stop.Token);

        // Attende che il client comparso venga davvero INTERROGATO, senza dipendere da un
        // ritardo fisso: e' la prova che il view model lo ha adottato.
        while (!stop.IsCancellationRequested && client.Queries == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(client.Queries >= 1, "il client comparso deve essere interrogato");
        Assert.True(rereads >= 1);

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task TokenRejectedOnAnAlreadyConnectedWindow_TheConfigurationIsReread()
    {
        // Il gemello del caso sopra, e finora scoperto: la rilettura avveniva SOLO all'avvio.
        // Una finestra gia' collegata che riceve 401 — perche' il token e' stato ruotato —
        // restava bloccata su "Token rejected" fino al riavvio, e nessun messaggio lo diceva.
        // E' lo stesso incidente di "Configuration missing", su un altro percorso.
        FakeMetricsClient oldClient = new(
            ObserverEndpoint.Remote(new Uri("http://vecchia:5057/"), "t", "dalla prova"),
            ServiceOutcome.TokenRejected);
        FakeMetricsClient newClient = new(
            ObserverEndpoint.Remote(new Uri("http://nuova:9999/"), "t", "dalla prova"),
            ServiceOutcome.Unreachable);

        MainViewModel viewModel = new(
            oldClient,
            configurationProblem: null,
            rereadConfiguration: () => newClient);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(stop.Token);

        while (!stop.IsCancellationRequested && newClient.Queries == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(newClient.Queries >= 1, "dopo un 401 il client riletto deve essere interrogato");

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task WithNoConfigurationAndNoReread_TheLoopExitsAtOnce()
    {
        // Il comportamento precedente resta valido quando non c'e' modo di rileggere:
        // martellare il servizio con richieste destinate al 401 non aiuterebbe nessuno.
        MainViewModel viewModel = new(client: null, configurationProblem: "manca il token");

        await viewModel.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("Observer", viewModel.Heading);
    }

    private sealed class FakeMetricsClient(ObserverEndpoint endpoint, ServiceOutcome outcome) : IMetricsClient
    {
        public FakeMetricsClient()
            : this(ObserverEndpoint.LocalChannel(), ServiceOutcome.Unreachable)
        {
        }

        public ObserverEndpoint Endpoint { get; } = endpoint;

        private int queryCount;

        /// <summary>Quante volte questo client e' stato interrogato.</summary>
        /// <remarks>
        /// E' il segnale con cui i test riconoscono che il view model ha ADOTTATO questo
        /// client. Prima guardavano l'intestazione, che conteneva il nome della macchina; ora
        /// l'intestazione e' sempre "Observer", e comunque era un indizio indiretto: diceva
        /// che una stringa era cambiata, non che il client nuovo venisse davvero usato.
        /// </remarks>
        public int Queries => Volatile.Read(ref queryCount);

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref queryCount);

            return Task.FromResult(new SnapshotFetch(outcome, "no service in this test", null));
        }

        // Lo storico NON incrementa il contatore: quel contatore dice se il view model ha
        // adottato questo client per il CAMPIONAMENTO, e mescolarci dentro una seconda
        // chiamata renderebbe il segnale ambiguo proprio nei test della riconnessione.
        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(outcome, "no service in this test", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(outcome, "no service in this test", null));

        /// <summary>Vero dopo Dispose: rende il metodo non statico e documenta l'esito.</summary>
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}