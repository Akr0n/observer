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
public class StoricoCadenzaTests
{
    /// <summary>I tre periodi del selettore, come chiavi.</summary>
    public static TheoryData<string> Periodi() => [.. Preferences.AllowedPeriods];

    [Theory]
    [MemberData(nameof(Periodi))]
    public void UnaLetturaFallitaSiRiprovaPrestoENonAlPassoSuccessivo(string chiave)
    {
        HistoryPeriodOption periodo = new(chiave);

        TimeSpan riprova = MainViewModel.HistoryReadDelay(periodo, succeeded: false);

        // Il difetto era esattamente questo: la lettura dichiarava "andata bene" anche quando
        // OGNI striscia era fallita, quindi un timeout rimandava di un passo intero. A sette
        // giorni sono due ore di "No history" accanto a quadranti che si aggiornano ogni
        // secondo, su dati che il servizio ha ripreso a dare dopo un secondo.
        Assert.True(
            riprova < periodo.Step,
            $"{chiave}: dopo un guasto si aspetta {riprova}, cioe' quanto un passo ({periodo.Step})");

        // E presto vuol dire presto, non "un po' meno": mezzo minuto e' il tetto.
        Assert.True(riprova <= TimeSpan.FromSeconds(30), $"{chiave}: si riprova dopo {riprova}");
    }

    /// <summary>Ogni periodo con la cadenza esatta che gli tocca, in secondi.</summary>
    public static TheoryData<string, double> Cadenze() => new()
    {
        { "1h", 60d },
        { "24h", 225d },
        { "7d", 1800d },
    };

    [Theory]
    [MemberData(nameof(Cadenze))]
    public void LaBarraInCorsoHaIlTempoDiCrescere(string chiave, double secondi)
    {
        HistoryPeriodOption periodo = new(chiave);

        TimeSpan cadenza = MainViewModel.HistoryReadDelay(periodo, succeeded: true);

        // I valori esatti e non solo la regola, come per le scale: un quarto di passo col
        // pavimento al minuto da' {1 min, 3 min 45 s, 30 min}, e chi li cambia deve vederli.
        Assert.Equal(TimeSpan.FromSeconds(secondi), cadenza);

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
            cadenza <= periodo.Step,
            $"{chiave}: si rilegge ogni {cadenza}, cioe' MENO spesso del passo ({periodo.Step})");

        Assert.True(
            chiave == "1h" || cadenza <= periodo.Step / 2,
            $"{chiave}: si rilegge ogni {cadenza} su barre da {periodo.Step}: la barra in corso non cresce");

        // E nemmeno di continuo: questa e' una finestra che misura la macchina che sta
        // interrogando, e cio' che spende per aggiornarsi rientra nel numero che mostra.
        Assert.True(cadenza >= TimeSpan.FromMinutes(1), $"{chiave}: si rilegge ogni {cadenza}");
    }

    [Fact]
    public async Task UnoStoricoCheFallisceNonCongelaLaStrisciaPerUnPasso()
    {
        // A sette giorni il passo e' due ore: se la scadenza si spostasse lo stesso dopo un
        // guasto, la seconda lettura non partirebbe per mezz'ora di clock. Qui l'clock
        // avanza di venti secondi e la seconda lettura deve esserci gia'.
        OrologioFinto clock = new();
        ClientSenzaStorico cliente = new();

        MainViewModel viewModel = new(cliente, configurationProblem: null, clock: clock.Adesso)
        {
            HistoryPeriod = "7d",
        };

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(20));
        Task ciclo = viewModel.RunAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && cliente.Letture == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        int dopoIlPrimoGiro = cliente.Letture;

        Assert.True(dopoIlPrimoGiro > 0, "la prima lettura di storico non e' mai partita");

        clock.Avanza(TimeSpan.FromSeconds(20));

        while (!arresto.IsCancellationRequested && cliente.Letture <= dopoIlPrimoGiro)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(
            cliente.Letture > dopoIlPrimoGiro,
            "venti secondi dopo un guasto nessuno ha riprovato: la striscia resta ferma un passo intero");

        await End(arresto, ciclo);
    }

    [Fact]
    public async Task CambiandoMacchinaLaStrisciaNonAspettaLaScadenzaDellaPrecedente()
    {
        // La scadenza dello storico e' un derivato della macchina guardata, come i quadranti e
        // il catalogo. Senza azzerarla, le righe della macchina nuova nascono senza striscia E
        // senza nota - ne' barre ne' il motivo per cui non ci sono - e restano cosi' fino alla
        // scadenza ereditata: mezz'ora a sette giorni, con i quadranti sopra gia' vivi.
        ObserverEndpoint locale = ObserverEndpoint.LocalChannel();
        ObserverEndpoint altra = ObserverEndpoint.Remote(
            new Uri("https://altra:5058/"), "token", "altra", new string('a', 64));

        OrologioFinto clock = new();
        ClientConStorico seconda = new(altra);

        MainViewModel viewModel = new(
            client: new ClientConStorico(locale),
            configurationProblem: null,
            clock: clock.Adesso,
            machineList: new MachineListResult([locale, altra], []),
            openMachine: _ => seconda)
        {
            HistoryPeriod = "7d",
        };

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(20));
        Task ciclo = viewModel.RunAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && !viewModel.Gauges.Any(riga => riga.ShowHistory))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Contains(viewModel.Gauges, riga => riga.ShowHistory);

        viewModel.SelectedMachine = viewModel.Machines.Single(voce => voce.Endpoint == altra);

        Assert.Empty(viewModel.Gauges);

        // Senza avanzare l'clock: la striscia della macchina nuova deve tornare nei secondi
        // del ciclo, non fra mezz'ora.
        while (!arresto.IsCancellationRequested && !viewModel.Gauges.Any(riga => riga.ShowHistory))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Contains(viewModel.Gauges, riga => riga.ShowHistory);

        await End(arresto, ciclo);
    }

    [Fact]
    public async Task IlTitoloDiceIlPeriodoDisegnatoNonQuelloScelto()
    {
        // Con la macchina che non risponde lo storico non si rilegge affatto: se il titolo
        // seguisse il selettore, resterebbe per sempre "Last 7 days" sopra le barre da un
        // minuto lette prima del guasto, e una macchina a riposo da un'ora si leggerebbe come
        // a riposo da una settimana.
        MainViewModel viewModel = new(new ClientMuto(), configurationProblem: null);

        string primaDiTutto = viewModel.HistoryTitle;

        viewModel.HistoryPeriod = "7d";

        // Niente di disegnato, quindi il titolo segue il selettore: non c'e' striscia da
        // contraddire, e all'avvio con "7d" nel file dire "Last hour" sarebbe sbagliato e basta.
        Assert.Equal(new HistoryPeriodOption("7d").Title, viewModel.HistoryTitle);
        Assert.NotEqual(primaDiTutto, viewModel.HistoryTitle);

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.RunAsync(arresto.Token);

        await Task.Delay(1500, CancellationToken.None);

        // Nessuna lettura e' andata a buon fine, quindi non c'e' niente da rinominare.
        Assert.Empty(viewModel.Gauges);
        Assert.Equal(new HistoryPeriodOption("7d").Title, viewModel.HistoryTitle);

        await End(arresto, ciclo);
    }

    private static async Task End(CancellationTokenSource arresto, Task ciclo)
    {
        await arresto.CancelAsync();

        try
        {
            await ciclo;
        }
        catch (OperationCanceledException)
        {
            // End del test.
        }
    }

    /// <summary>Campiona benissimo e non ha storico: il guasto che la cadenza deve vedere.</summary>
    private sealed class ClientSenzaStorico : IMetricsClient
    {
        private int letture;

        public int Letture => Volatile.Read(ref letture);

        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.LocalChannel();

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Banco.Istantanea());

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Banco.Catalogo());

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref letture);

            return Task.FromResult(new HistoryFetch(ServiceOutcome.Unreachable, "persistenza spenta", null));
        }
    }

    /// <summary>Risponde a tutto, storico compreso.</summary>
    private sealed class ClientConStorico(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Banco.Istantanea());

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Banco.Catalogo());

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));
    }

    /// <summary>Non risponde mai: la macchina spenta.</summary>
    private sealed class ClientMuto : IMetricsClient
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
    private static class Banco
    {
        public static SnapshotFetch Istantanea() =>
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

        public static CatalogFetch Catalogo() =>
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