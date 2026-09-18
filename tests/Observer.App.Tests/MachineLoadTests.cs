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
public class MachineLoadTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 12, 9, 0, 0, TimeSpan.Zero);

    private static MachineSnapshot Snapshot(params MetricSnapshot[] collectors) =>
        new(MachineSnapshot.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, collectors);

    private static MetricSnapshot Group(string id, params MetricPoint[] points) =>
        new(id, CollectorStatus.Ok, null, points);

    private static MachineRow RemoteRow() => new(ObserverEndpoint.Remote(
        new Uri("https://altra:5058/"), "token", "machines.json", new string('a', 64), "altra"));

    [Fact]
    public void BothNumbersComeFromOneSnapshotAndTheCaptionRoundsThem()
    {
        MachineLoad load = MachineLoad.From(Snapshot(
            Group("cpu", MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(42.7d))),
            Group("memory", MetricPoint.Measured(MemoryCollector.UsedPercentMetricId, null, MetricValue.FromNumber(61.2d)))));

        Assert.Equal(42.7d, load.Cpu);
        Assert.Equal(61.2d, load.Memory);

        // Interi: la domanda e' "quale macchina e' in affanno", e un decimo di punto non ci
        // aggiunge niente mentre attira l'occhio a ogni lettura.
        Assert.Equal("CPU 43% · RAM 61%", load.Caption);
    }

    [Fact]
    public void APerCorePointIsNeverTakenForTheWholeMachine()
    {
        // Il per-core passa dalla stessa interfaccia, con lo STESSO identificativo e un'istanza
        // valorizzata. Prendere il primo punto che capita darebbe il carico di un core solo
        // spacciato per quello della macchina - e sarebbe verosimile, quindi invisibile.
        // In ENTRAMBI gli ordini, perche' l'ordine dei punti non e' dichiarato da nessuna
        // parte: con i core solo in coda, un codice che prende il primo punto passerebbe.
        MachineLoad machinePointLast = MachineLoad.From(Snapshot(Group(
            "cpu",
            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, "0", MetricValue.FromNumber(99d)),
            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, "1", MetricValue.FromNumber(97d)),
            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(12d)))));

        Assert.Equal(12d, machinePointLast.Cpu);
        Assert.Equal("CPU 12%", machinePointLast.Caption);

        MachineLoad machinePointFirst = MachineLoad.From(Snapshot(Group(
            "cpu",
            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(12d)),
            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, "0", MetricValue.FromNumber(99d)))));

        Assert.Equal(12d, machinePointFirst.Cpu);
    }

    [Fact]
    public void AMissingMetricDoesNotHideTheOther()
    {
        // Su una piattaforma dove la CPU non e' leggibile la memoria lo e' lo stesso, e meta'
        // risposta e' meglio di nessuna.
        MachineLoad memoryOnly = MachineLoad.From(Snapshot(
            Group("cpu", MetricPoint.Unsupported(CpuCollector.TotalUsageMetricId, null, "non misurabile qui")),
            Group("memory", MetricPoint.Measured(MemoryCollector.UsedPercentMetricId, null, MetricValue.FromNumber(61d)))));

        Assert.Null(memoryOnly.Cpu);
        Assert.Equal("RAM 61%", memoryOnly.Caption);

        MachineLoad cpuOnly = MachineLoad.From(Snapshot(
            Group("cpu", MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(8d)))));

        Assert.Null(cpuOnly.Memory);
        Assert.Equal("CPU 8%", cpuOnly.Caption);
    }

    [Fact]
    public void WithNoSnapshotItDoesNotInventAZero()
    {
        // Uno zero accanto al nome di una macchina si legge "ferma", che e' l'opposto di "non
        // si sa". La differenza conta proprio sulle macchine che non rispondono.
        Assert.Equal(MachineLoad.None, MachineLoad.From(null));
        Assert.Equal(string.Empty, MachineLoad.None.Caption);
        Assert.Null(MachineLoad.None.Cpu);
        Assert.Equal(string.Empty, MachineLoad.From(Snapshot()).Caption);
    }

    [Fact]
    public void AValueThatIsNotANumberDoesNotBecomeZero()
    {
        MachineLoad load = MachineLoad.From(Snapshot(Group(
            "cpu",
            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromText("parecchio")))));

        Assert.Null(load.Cpu);
        Assert.Equal(string.Empty, load.Caption);
    }

    [Fact]
    public void AMachineThatGoesDownLosesTheLoadItHad()
    {
        // Il carico si azzera dentro Record e non nei chiamanti: sono tre, e uno si
        // dimenticherebbe, lasciando sotto il nome di una macchina spenta i numeri di quando
        // rispondeva - numeri veri, riferiti a un momento che non c'e' piu'.
        MachineRow row = RemoteRow();

        row.Record(
            ServiceOutcome.Ok,
            string.Empty,
            T0,
            Snapshot(Group("cpu", MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(50d)))));

        Assert.Equal("CPU 50%", row.Subtitle);

        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0 + TimeSpan.FromMinutes(1));

        // Il carico se ne va SUBITO, mentre la durata non c'e' ancora: dentro i dieci secondi
        // di tolleranza StatusEscalation non dice niente, di proposito. La riga resta quindi
        // vuota per un momento, ed e' la ragione per cui lo spazio sotto il nome e' riservato
        // sempre invece di comparire e sparire - il vuoto non deve far saltare la voce.
        Assert.Equal(MachineLoad.None, row.MachineLoad);
        Assert.Equal(string.Empty, row.Subtitle);

        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0 + TimeSpan.FromMinutes(4));

        Assert.Equal(MachineLoad.None, row.MachineLoad);
        Assert.Equal("for 3 min", row.Subtitle);
    }

    [Fact]
    public void TheFaultDurationTakesPrecedenceOverTheLoad()
    {
        // La precedenza si prova solo se i due CONVIVONO, e per costruzione non convivono mai:
        // Record azzera il carico su ogni esito non Ok. Quindi si forza la convivenza dal di
        // fuori, che e' l'unico modo di mettere alla prova la regola invece del ramo che oggi
        // la rende irraggiungibile - e di accorgersene se un giorno smettesse di esserlo.
        MachineRow row = RemoteRow();

        row.Record(ServiceOutcome.TokenRejected, "rejected", T0);
        row.Record(ServiceOutcome.TokenRejected, "rejected", T0 + TimeSpan.FromMinutes(2));

        Assert.Equal(MachineLoad.None, row.MachineLoad);
        Assert.Equal("for 2 min", row.Subtitle);

        row.MachineLoad = new MachineLoad(80d, 90d);

        Assert.Equal("for 2 min", row.Subtitle);
        Assert.Contains("for 2 min", row.ToolTipText, StringComparison.Ordinal);
        Assert.DoesNotContain("CPU", row.ToolTipText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWatchedMachineShowsNoNumbersInTheSidebar()
    {
        // E' la decisione che tiene insieme tutto il resto: i numeri della macchina guardata
        // sono nei quadranti, e ripeterli accanto al nome vorrebbe dire due letture della
        // stessa macchina a cadenze diverse - quindici secondi contro uno - che si
        // contraddicono a vista. La voce guardata e' anche l'unica sempre selezionata, cioe'
        // l'unica che un lettore di schermo riannuncia: con i numeri il suo nome accessibile
        // cambierebbe a ogni secondo, per sempre.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint other = ObserverEndpoint.Remote(
            new Uri("https://altra:5058/"), "token", "machines.json", new string('a', 64), "altra");

        MainViewModel viewModel = new(
            client: null,
            configurationProblem: null,
            machineList: new MachineListResult([local, other], []));

        MachineRow row = viewModel.Machines.Single(v => v.Endpoint == other);

        // La sonda le ha scritto il carico mentre NON era guardata: e' il caso normale.
        row.Record(
            ServiceOutcome.Ok,
            string.Empty,
            T0,
            Snapshot(
                Group("cpu", MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(42d))),
                Group("memory", MetricPoint.Measured(MemoryCollector.UsedPercentMetricId, null, MetricValue.FromNumber(61d)))));

        Assert.Equal("CPU 42% · RAM 61%", row.Subtitle);

        // Il clic. SUBITO, senza aspettare un giro: fra la selezione e la prima risposta
        // passano fino a otto secondi di timeout, e in quel tempo la riga evidenziata direbbe
        // che la macchina sta lavorando mentre la barra di stato dice "Connecting".
        viewModel.SelectedMachine = row;

        Assert.Equal(MachineLoad.None, row.MachineLoad);
        Assert.Equal(string.Empty, row.Subtitle);
        Assert.DoesNotContain("CPU", row.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLoadIsAnnouncedEvenWithoutSeeingTheRow()
    {
        // Il suggerimento del mouse e il nome accessibile passano dallo stesso testo della
        // riga, cosi' non possono divergere. Il punto medio separa due fatti accostati.
        MachineRow row = RemoteRow();

        // Una prima lettura riuscita SENZA carico, cosi' Status e Detail sono gia' al valore
        // finale: da qui in poi l'unica cosa che cambia e' il carico, e le notifiche che si
        // osservano possono venire solo da lui. Senza questo passo le tre asserzioni sarebbero
        // soddisfatte da Detail, che notifica ToolTipText e AccessibleName per conto suo.
        row.Record(ServiceOutcome.Ok, string.Empty, T0);

        List<string> notified = [];
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? string.Empty);

        row.Record(
            ServiceOutcome.Ok,
            string.Empty,
            T0 + TimeSpan.FromSeconds(15),
            Snapshot(
                Group("cpu", MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(42d))),
                Group("memory", MetricPoint.Measured(MemoryCollector.UsedPercentMetricId, null, MetricValue.FromNumber(61d)))));

        Assert.Equal("Reachable · CPU 42% · RAM 61%", row.ToolTipText);
        Assert.Contains("CPU 42%", row.AccessibleName, StringComparison.Ordinal);

        // Senza queste notifiche la riga direbbe ancora la cosa di prima, con la suite verde.
        Assert.Contains(nameof(MachineRow.Subtitle), notified);
        Assert.Contains(nameof(MachineRow.ToolTipText), notified);
        Assert.Contains(nameof(MachineRow.AccessibleName), notified);
    }

    [Fact]
    public async Task AProbeThatReturnsAfterTheMachineBecameWatchedDoesNotWrite()
    {
        // La sonda PARTE filtrando la macchina guardata, ma TORNA fino a otto secondi dopo, e
        // in quel tempo un clic basta. Da li' in poi scriverebbero in due sulla stessa voce -
        // la sonda ogni quindici secondi, il giro principale ogni secondo - e la riga
        // mostrerebbe a strappi due letture diverse della STESSA macchina.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint other = ObserverEndpoint.Remote(
            new Uri("https://altra:5058/"), "token", "machines.json", new string('a', 64), "altra");

        HeldClient held = new();

        MainViewModel viewModel = new(
            client: new HeldClient(alreadyReleased: true),
            configurationProblem: null,
            machineList: new MachineListResult([local, other], []),
            openMachine: _ => held);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
        Task loop = viewModel.RunAsync(stop.Token);

        MachineRow row = viewModel.Machines.Single(v => v.Endpoint == other);

        // Si aspetta che la sonda sia DAVVERO in volo, non che sia passato del tempo.
        while (!stop.IsCancellationRequested && !held.HasEntered)
        {
            await Task.Delay(20, CancellationToken.None);
        }

        Assert.True(held.HasEntered, "la sonda non e' mai partita");

        // Il clic, mentre la risposta e' ancora per aria.
        viewModel.SelectedMachine = row;

        held.Release();

        while (!stop.IsCancellationRequested && !held.HasExited)
        {
            await Task.Delay(20, CancellationToken.None);
        }

        // La sonda aveva in mano una CPU al 99 %: se avesse scritto, la riga lo direbbe.
        Assert.Equal(MachineLoad.None, row.MachineLoad);
        Assert.Equal(string.Empty, row.Subtitle);

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

    /// <summary>Risponde solo quando il test lo libera, e la prima volta sola.</summary>
    private sealed class HeldClient(bool alreadyReleased = false) : IMetricsClient
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int entries;

        public bool HasEntered => Volatile.Read(ref entries) > 0;

        public bool HasExited { get; private set; }

        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.LocalChannel();

        public void Release() => gate.TrySetResult();

        public async Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken)
        {
            if (!alreadyReleased && Interlocked.Increment(ref entries) == 1)
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                HasExited = true;
            }

            return new SnapshotFetch(
                ServiceOutcome.Ok,
                string.Empty,
                Snapshot(Group(
                    "cpu",
                    MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(99d)))));
        }

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Ok, string.Empty, new MetricCatalog([])));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));
    }
}