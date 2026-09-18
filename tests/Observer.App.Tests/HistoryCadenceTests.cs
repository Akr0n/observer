using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// Ogni quanto si rilegge lo storico, e perche' non e' "ogni passo".
/// </summary>
/// <remarks>
/// Finche' la striscia mostrava un'ora sola, il passo era un minuto e qualunque errore di
/// cadenza durava sessanta secondi: invisibile. Con i periodi lunghi lo stesso errore dura due
/// ore, e diventa lo stato stabile della finestra. Le regole qui sono nate da difetti veri,
/// tutti trovati con la suite verde.
/// </remarks>
public class HistoryCadenceTests
{
    /// <summary>I tre periodi del selettore, come chiavi.</summary>
    public static TheoryData<string> Periods() => [.. Preferences.AllowedPeriods];

    [Theory]
    [MemberData(nameof(Periods))]
    public void AFailedReadIsRetriedSoonAndNotAWholeStepLater(string key)
    {
        HistoryPeriodOption period = new(key);

        TimeSpan retryDelay = MainViewModel.HistoryReadDelay(period, succeeded: false);

        // Il difetto era esattamente questo: la lettura dichiarava "andata bene" anche quando
        // OGNI striscia era fallita, quindi un timeout rimandava di un passo intero. A sette
        // giorni sono due ore di "No history" accanto a quadranti che si aggiornano ogni
        // secondo, su dati che il servizio ha ripreso a dare dopo un secondo.
        Assert.True(
            retryDelay < period.Step,
            $"{key}: dopo un guasto si aspetta {retryDelay}, cioe' quanto un passo ({period.Step})");

        // E presto vuol dire presto, non "un po' meno": mezzo minuto e' il tetto.
        Assert.True(retryDelay <= TimeSpan.FromSeconds(30), $"{key}: si riprova dopo {retryDelay}");
    }

    /// <summary>Ogni periodo con la cadenza esatta che gli tocca, in secondi.</summary>
    public static TheoryData<string, double> Cadences() => new()
    {
        { "1h", 60d },
        { "24h", 225d },
        { "7d", 1800d },
    };

    [Theory]
    [MemberData(nameof(Cadences))]
    public void TheBarInProgressHasTimeToGrow(string key, double seconds)
    {
        HistoryPeriodOption period = new(key);

        TimeSpan cadence = MainViewModel.HistoryReadDelay(period, succeeded: true);

        // I valori esatti e non solo la regola, come per le scale: un quarto di passo col
        // pavimento al minuto da' {1 min, 3 min 45 s, 30 min}, e chi li cambia deve vederli.
        Assert.Equal(TimeSpan.FromSeconds(seconds), cadence);

        // Rileggere ESATTAMENTE ogni passo sembra la cadenza giusta - piu' spesso non aggiunge
        // una barra - e non lo e': l'ultima barra e' l'intervallo in corso e si disegna larga
        // quanto ha coperto, quindi rileggendo al passo si guarderebbe ogni volta una barra
        // appena nata, sempre alla stessa frazione. A sette giorni l'estremo destro della
        // striscia - il punto che l'occhio legge come "adesso" - resterebbe congelato a quella
        // larghezza per tutta la sessione, e con una scelta fatta a inizio intervallo e' un
        // pixel. A un'ora il passo vale gia' un minuto e il pavimento vince: li' la barra non
        // cresce, ed e' una rinuncia dichiarata (dodici richieste ogni quindici secondi per
        // animare tredici pixel non si pagano).
        Assert.True(
            cadence <= period.Step,
            $"{key}: si rilegge ogni {cadence}, cioe' MENO spesso del passo ({period.Step})");

        Assert.True(
            key == "1h" || cadence <= period.Step / 2,
            $"{key}: si rilegge ogni {cadence} su barre da {period.Step}: la barra in corso non cresce");

        // E nemmeno di continuo: questa e' una finestra che misura la macchina che sta
        // interrogando, e cio' che spende per aggiornarsi rientra nel numero che mostra.
        Assert.True(cadence >= TimeSpan.FromMinutes(1), $"{key}: si rilegge ogni {cadence}");
    }

    [Fact]
    public async Task AFailingHistoryDoesNotFreezeTheStripForAWholeStep()
    {
        // A sette giorni il passo e' due ore: se la scadenza si spostasse lo stesso dopo un
        // guasto, la seconda lettura non partirebbe per mezz'ora di orologio. Qui l'orologio
        // avanza di venti secondi e la seconda lettura deve esserci gia'.
        FakeClock clock = new();
        ClientWithoutHistory client = new();

        MainViewModel viewModel = new(client, configurationProblem: null, clock: clock.Now)
        {
            HistoryPeriod = "7d",
        };

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
        Task loop = viewModel.RunAsync(stop.Token);

        while (!stop.IsCancellationRequested && client.Reads == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        int afterFirstRound = client.Reads;

        Assert.True(afterFirstRound > 0, "la prima lettura di storico non e' mai partita");

        clock.Advance(TimeSpan.FromSeconds(20));

        while (!stop.IsCancellationRequested && client.Reads <= afterFirstRound)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(
            client.Reads > afterFirstRound,
            "venti secondi dopo un guasto nessuno ha riprovato: la striscia resta ferma un passo intero");

        await End(stop, loop);
    }

    [Fact]
    public async Task AfterSwitchingMachineTheStripDoesNotWaitForThePreviousDeadline()
    {
        // La scadenza dello storico e' un derivato della macchina guardata, come i quadranti e
        // il catalogo. Senza azzerarla, le righe della macchina nuova nascono senza striscia E
        // senza nota - ne' barre ne' il motivo per cui non ci sono - e restano cosi' fino alla
        // scadenza ereditata: mezz'ora a sette giorni, con i quadranti sopra gia' vivi.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint other = ObserverEndpoint.Remote(
            new Uri("https://altra:5058/"), "token", "altra", new string('a', 64));

        FakeClock clock = new();
        ClientWithHistory secondClient = new(other);

        MainViewModel viewModel = new(
            client: new ClientWithHistory(local),
            configurationProblem: null,
            clock: clock.Now,
            machineList: new MachineListResult([local, other], []),
            openMachine: _ => secondClient)
        {
            HistoryPeriod = "7d",
        };

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
        Task loop = viewModel.RunAsync(stop.Token);

        while (!stop.IsCancellationRequested && !viewModel.Gauges.Any(row => row.ShowHistory))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Contains(viewModel.Gauges, row => row.ShowHistory);

        viewModel.SelectedMachine = viewModel.Machines.Single(entry => entry.Endpoint == other);

        Assert.Empty(viewModel.Gauges);

        // Senza avanzare l'orologio: la striscia della macchina nuova deve tornare nei secondi
        // del ciclo, non fra mezz'ora.
        while (!stop.IsCancellationRequested && !viewModel.Gauges.Any(row => row.ShowHistory))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Contains(viewModel.Gauges, row => row.ShowHistory);

        await End(stop, loop);
    }

    [Fact]
    public async Task WithNothingDrawnTheTitleFollowsTheSelectedPeriod()
    {
        // Con la macchina che non risponde lo storico non si rilegge affatto: se il titolo
        // seguisse il selettore, resterebbe per sempre "Last 7 days" sopra le barre da un
        // minuto lette prima del guasto, e una macchina a riposo da un'ora si leggerebbe come
        // a riposo da una settimana.
        MainViewModel viewModel = new(new SilentClient(), configurationProblem: null);

        string titleAtStart = viewModel.HistoryTitle;

        viewModel.HistoryPeriod = "7d";

        // Niente di disegnato, quindi il titolo segue il selettore: non c'e' striscia da
        // contraddire, e all'avvio con "7d" nel file dire "Last hour" sarebbe sbagliato e basta.
        Assert.Equal(new HistoryPeriodOption("7d").Title, viewModel.HistoryTitle);
        Assert.NotEqual(titleAtStart, viewModel.HistoryTitle);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(stop.Token);

        await Task.Delay(1500, CancellationToken.None);

        // Nessuna lettura e' andata a buon fine, quindi non c'e' niente da rinominare.
        Assert.Empty(viewModel.Gauges);
        Assert.Equal(new HistoryPeriodOption("7d").Title, viewModel.HistoryTitle);

        await End(stop, loop);
    }

    private static async Task End(CancellationTokenSource stop, Task loop)
    {
        await stop.CancelAsync();

        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
            // Fine del test.
        }
    }

    /// <summary>Campiona benissimo e non ha storico: il guasto che la cadenza deve vedere.</summary>
    private sealed class ClientWithoutHistory : IMetricsClient
    {
        private int readCount;

        public int Reads => Volatile.Read(ref readCount);

        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.LocalChannel();

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Bench.Snapshot());

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Bench.Catalog());

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref readCount);

            return Task.FromResult(new HistoryFetch(ServiceOutcome.Unreachable, "persistenza spenta", null));
        }
    }

    /// <summary>Risponde a tutto, storico compreso.</summary>
    private sealed class ClientWithHistory(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Bench.Snapshot());

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Bench.Catalog());

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));
    }

    /// <summary>Non risponde mai: la macchina spenta.</summary>
    private sealed class SilentClient : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.LocalChannel();

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.Unreachable, "spenta", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Unreachable, "spenta", null));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Unreachable, "spenta", null));
    }

    /// <summary>Una CPU, che e' quanto basta per avere un quadrante.</summary>
    private static class Bench
    {
        public static SnapshotFetch Snapshot() =>
            new(ServiceOutcome.Ok,
                string.Empty,
                new MachineSnapshot(
                    MachineSnapshot.CurrentSchemaVersion,
                    DateTimeOffset.UnixEpoch,
                    [
                        new MetricSnapshot("cpu", CollectorStatus.Ok, null,
                        [
                            MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(42d)),
                        ]),
                    ]));

        public static CatalogFetch Catalog() =>
            new(ServiceOutcome.Ok,
                string.Empty,
                new MetricCatalog(
                [
                    new CollectorCatalogEntry("cpu",
                    [
                        new MetricDescriptor("cpu.usage.total", "CPU usage", MetricUnit.Percent, IsPerInstance: false),
                    ]),
                ]));
    }
}