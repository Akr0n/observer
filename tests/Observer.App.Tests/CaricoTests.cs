using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Memory;

namespace Observer.App.Tests;

/// <summary>
/// I due numeri accanto al nome di una macchina che non si sta guardando.
/// </summary>
/// <remarks>
/// La barra laterale risponde a una domanda sola - "devo cambiare macchina?" - e i due numeri
/// esistono per quella. Le regole qui dicono soprattutto cosa succede quando la risposta non si
/// sa, che e' il caso in cui uno zero inventato fa piu' danno di un vuoto.
/// </remarks>
public class CaricoTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 12, 9, 0, 0, TimeSpan.Zero);

    private static MachineSnapshot Campionamento(params MetricSnapshot[] collector) =>
        new(MachineSnapshot.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, collector);

    private static MetricSnapshot Gruppo(string id, params MetricPoint[] punti) =>
        new(id, CollectorStatus.Ok, null, punti);

    private static MacchinaInElenco Voce() => new(ObserverEndpoint.Remoto(
        new Uri("https://altra:5058/"), "token", "machines.json", new string('a', 64), "altra"));

    [Fact]
    public void IDueNumeriSiLeggonoDalCampionamentoCheLaSondaHaGiaInMano()
    {
        Carico carico = Carico.Da(Campionamento(
            Gruppo("cpu", MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(42.7d))),
            Gruppo("memory", MetricPoint.Measured(MemoryCollector.UsedPercentMetricId, null, MetricValue.FromNumber(61.2d)))));

        Assert.Equal(42.7d, carico.Cpu);
        Assert.Equal(61.2d, carico.Memoria);

        // Interi: la domanda e' "quale macchina e' in affanno", e un decimo di punto non ci
        // aggiunge niente mentre attira l'occhio a ogni lettura.
        Assert.Equal("CPU 43% · RAM 61%", carico.Frase);
    }

    [Fact]
    public void IlPerCoreNonSiSpacciaPerLaMacchina()
    {
        // Il per-core passa dalla stessa interfaccia, con lo STESSO identificativo e un'istanza
        // valorizzata. Prendere il primo punto che capita darebbe il carico di un core solo
        // spacciato per quello della macchina - e sarebbe verosimile, quindi invisibile.
        Carico carico = Carico.Da(Campionamento(Gruppo(
            "cpu",
            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, "0", MetricValue.FromNumber(99d)),
            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, "1", MetricValue.FromNumber(97d)),
            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(12d)))));

        Assert.Equal(12d, carico.Cpu);
        Assert.Equal("CPU 12%", carico.Frase);
    }

    [Fact]
    public void UnaMetricaCheMancaNonPortaViaLAltra()
    {
        // Su una piattaforma dove la CPU non e' leggibile la memoria lo e' lo stesso, e meta'
        // risposta e' meglio di nessuna.
        Carico soloMemoria = Carico.Da(Campionamento(
            Gruppo("cpu", MetricPoint.Unsupported(CpuCollector.TotalUsageMetricId, null, "non misurabile qui")),
            Gruppo("memory", MetricPoint.Measured(MemoryCollector.UsedPercentMetricId, null, MetricValue.FromNumber(61d)))));

        Assert.Null(soloMemoria.Cpu);
        Assert.Equal("RAM 61%", soloMemoria.Frase);

        Carico soloCpu = Carico.Da(Campionamento(
            Gruppo("cpu", MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(8d)))));

        Assert.Null(soloCpu.Memoria);
        Assert.Equal("CPU 8%", soloCpu.Frase);
    }

    [Fact]
    public void SenzaCampionamentoNonSiInventaUnoZero()
    {
        // Uno zero accanto al nome di una macchina si legge "ferma", che e' l'opposto di "non
        // si sa". La differenza conta proprio sulle macchine che non rispondono.
        Assert.Equal(Carico.Nessuno, Carico.Da(null));
        Assert.Equal(string.Empty, Carico.Nessuno.Frase);
        Assert.Null(Carico.Nessuno.Cpu);
        Assert.Equal(string.Empty, Carico.Da(Campionamento()).Frase);
    }

    [Fact]
    public void UnValoreCheNonEUnNumeroNonDiventaZero()
    {
        Carico carico = Carico.Da(Campionamento(Gruppo(
            "cpu",
            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromText("parecchio")))));

        Assert.Null(carico.Cpu);
        Assert.Equal(string.Empty, carico.Frase);
    }

    [Fact]
    public void LaMacchinaGiuPerdeIlCaricoCheAveva()
    {
        // Il carico si azzera dentro Registra e non nei chiamanti: sono tre, e uno si
        // dimenticherebbe, lasciando sotto il nome di una macchina spenta i numeri di quando
        // rispondeva - numeri veri, riferiti a un momento che non c'e' piu'.
        MacchinaInElenco voce = Voce();

        voce.Registra(
            ServiceOutcome.Ok,
            string.Empty,
            T0,
            Campionamento(Gruppo("cpu", MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(50d)))));

        Assert.Equal("CPU 50%", voce.SottoIlNome);

        voce.Registra(ServiceOutcome.ConnessioneRifiutata, "refused", T0 + TimeSpan.FromMinutes(1));

        // Il carico se ne va SUBITO, mentre la durata non c'e' ancora: dentro i dieci secondi
        // di tolleranza StatusEscalation non dice niente, di proposito. La riga resta quindi
        // vuota per un momento, ed e' la ragione per cui lo spazio sotto il nome e' riservato
        // sempre invece di comparire e sparire - il vuoto non deve far saltare la voce.
        Assert.Equal(Carico.Nessuno, voce.Carico);
        Assert.Equal(string.Empty, voce.SottoIlNome);

        voce.Registra(ServiceOutcome.ConnessioneRifiutata, "refused", T0 + TimeSpan.FromMinutes(4));

        Assert.Equal(Carico.Nessuno, voce.Carico);
        Assert.Equal("for 3 min", voce.SottoIlNome);
    }

    [Fact]
    public void LaDurataDelGuastoVinceSulCarico()
    {
        MacchinaInElenco voce = Voce();

        voce.Registra(ServiceOutcome.TokenRifiutato, "rejected", T0);

        Assert.Equal(Carico.Nessuno, voce.Carico);
        Assert.StartsWith("for ", voce.SottoIlNome, StringComparison.Ordinal);
    }

    [Fact]
    public void IlCaricoSiSenteAncheSenzaVedereLaRiga()
    {
        // Il suggerimento del mouse e il nome accessibile passano dallo stesso testo della
        // riga, cosi' non possono divergere. Il punto medio separa due fatti accostati.
        MacchinaInElenco voce = Voce();
        List<string> notificate = [];
        voce.PropertyChanged += (_, e) => notificate.Add(e.PropertyName ?? string.Empty);

        voce.Registra(
            ServiceOutcome.Ok,
            string.Empty,
            T0,
            Campionamento(
                Gruppo("cpu", MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(42d))),
                Gruppo("memory", MetricPoint.Measured(MemoryCollector.UsedPercentMetricId, null, MetricValue.FromNumber(61d)))));

        Assert.Equal("Reachable · CPU 42% · RAM 61%", voce.Suggerimento);
        Assert.Contains("CPU 42%", voce.Descrizione, StringComparison.Ordinal);

        // Senza queste notifiche la riga direbbe ancora la cosa di prima, con la suite verde.
        Assert.Contains(nameof(MacchinaInElenco.SottoIlNome), notificate);
        Assert.Contains(nameof(MacchinaInElenco.Suggerimento), notificate);
        Assert.Contains(nameof(MacchinaInElenco.Descrizione), notificate);
    }

    [Fact]
    public async Task UnaSondaCheTornaSullaMacchinaDiventataGuardataNonScrive()
    {
        // La sonda PARTE filtrando la macchina guardata, ma TORNA fino a otto secondi dopo, e
        // in quel tempo un clic basta. Da li' in poi scriverebbero in due sulla stessa voce -
        // la sonda ogni quindici secondi, il giro principale ogni secondo - e la riga
        // mostrerebbe a strappi due letture diverse della STESSA macchina.
        ObserverEndpoint locale = ObserverEndpoint.CanaleLocale();
        ObserverEndpoint altra = ObserverEndpoint.Remoto(
            new Uri("https://altra:5058/"), "token", "machines.json", new string('a', 64), "altra");

        ClientTrattenuto lenta = new();

        MainViewModel viewModel = new(
            client: new ClientTrattenuto(gia: true),
            problemaDiConfigurazione: null,
            elenco: new MachineListResult([locale, altra], []),
            apriMacchina: _ => lenta);

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(20));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        MacchinaInElenco voce = viewModel.Macchine.Single(v => v.Punto == altra);

        // Si aspetta che la sonda sia DAVVERO in volo, non che sia passato del tempo.
        while (!arresto.IsCancellationRequested && !lenta.Entrata)
        {
            await Task.Delay(20, CancellationToken.None);
        }

        Assert.True(lenta.Entrata, "la sonda non e' mai partita");

        // Il clic, mentre la risposta e' ancora per aria.
        viewModel.MacchinaSelezionata = voce;

        lenta.Libera();

        while (!arresto.IsCancellationRequested && !lenta.Uscita)
        {
            await Task.Delay(20, CancellationToken.None);
        }

        // La sonda aveva in mano una CPU al 99 %: se avesse scritto, la riga lo direbbe.
        Assert.Equal(Carico.Nessuno, voce.Carico);
        Assert.Equal(string.Empty, voce.SottoIlNome);

        await arresto.CancelAsync();

        try
        {
            await ciclo;
        }
        catch (OperationCanceledException)
        {
            // Fine del test.
        }
    }

    /// <summary>Risponde solo quando il test lo libera, e la prima volta sola.</summary>
    private sealed class ClientTrattenuto(bool gia = false) : IMetricsClient
    {
        private readonly TaskCompletionSource cancello = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int entrate;

        public bool Entrata => Volatile.Read(ref entrate) > 0;

        public bool Uscita { get; private set; }

        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.CanaleLocale();

        public void Libera() => cancello.TrySetResult();

        public async Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken)
        {
            if (!gia && Interlocked.Increment(ref entrate) == 1)
            {
                await cancello.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                Uscita = true;
            }

            return new SnapshotFetch(
                ServiceOutcome.Ok,
                string.Empty,
                Campionamento(Gruppo(
                    "cpu",
                    MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(99d)))));
        }

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Ok, string.Empty, new MetricCatalog([])));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery richiesta, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));
    }
}