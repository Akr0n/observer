using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// Il pannello dei processi: quando si apre, cosa ricorda, e cosa serve per terminare.
/// </summary>
/// <remarks>
/// E' l'unico posto dell'applicazione da cui si distrugge qualcosa, e le regole che contano
/// non sono quelle che si vedono. La selezione deve sopravvivere all'aggiornamento — l'elenco
/// si riscrive ogni secondo mentre l'utente punta la riga — e la conferma deve disarmarsi
/// cambiando riga, o il secondo clic ucciderebbe il processo sbagliato.
/// </remarks>
public class ProcessPanelTests
{
    private static MetricRow RowFor(string key) =>
        new(new MetricRowState(key, "etichetta", "valore", 0.5d, MetricSeverity.Ok));

    [Theory]
    [InlineData("cpu|cpu.usage.total|", "cpu")]
    [InlineData("memory|memory.used.percent|", "memory")]
    [InlineData("disk.activity|disk.busy.percent|Disk 0", "io")]
    public void TheCpuMemoryAndDiskActivityGaugesOpenTheList(string key, string expected) =>
        Assert.Equal(expected, ProcessResource.From(key));

    [Theory]
    [InlineData("disk|disk.used.percent|C:")]
    [InlineData("")]
    [InlineData(null)]
    public void TheDiskSpaceGaugesOpenNothing(string? key)
    {
        // Non e' una dimenticanza. Lo spazio occupato su un volume non e' attribuibile a un
        // processo IN ESECUZIONE — chi ha scritto quei file magari non c'e' piu' da mesi. Un
        // pannello che si aprisse con l'elenco della CPU sotto il titolo di un volume direbbe
        // una cosa falsa.
        Assert.Null(ProcessResource.From(key));
    }

    [Fact]
    public void OnlyDiskSpaceIsNotClickable()
    {
        Assert.False(RowFor("disk|disk.used.percent|C:").CanShowProcesses);
        Assert.True(RowFor("disk.activity|disk.busy.percent|Disk 0").CanShowProcesses);
        Assert.True(RowFor("cpu|cpu.usage.total|").CanShowProcesses);
    }

    [Fact]
    public async Task TheDiskActivityGaugeAsksForWholeMachineIo()
    {
        // Il quadrante e' di UN disco, l'elenco no: i contatori sono per processo, non per
        // dispositivo. Il titolo deve dirlo, e al servizio si chiede "io", non la CPU.
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("disk.activity|disk.busy.percent|Disk 0"));

        Assert.Equal(["io"], client.Requested);
        Assert.Contains("I/O", viewModel.ProcessesTitle, StringComparison.Ordinal);
        Assert.Contains("whole machine", viewModel.ProcessesTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIoRateIsFormattedAsBytesPerSecondAndAnUnknownOneAsADash()
    {
        // Un trattino e non "0 B/s": sono due affermazioni diverse, e la seconda su un elenco
        // ordinato per I/O sposterebbe l'attenzione sul programma sbagliato.
        Assert.Equal("1.5 MiB/s", ProcessRowState.From(new ProcessWire(1, "copia", 0d, 10, 1_572_864d)).Io);
        Assert.Equal("—", ProcessRowState.From(new ProcessWire(1, "ignoto", 0d, 10, null)).Io);
    }

    [Fact]
    public async Task TheSelectionSurvivesTheRefresh()
    {
        // L'elenco si riscrive una volta al secondo. Senza tenere la selezione sul PID, la
        // riga puntata si deselezionerebbe da sola mentre ci si prepara a terminarla.
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 22);

        // Stessi PID, valori nuovi: e' cio' che succede a ogni giro. Si passa per un altro
        // quadrante e non per lo stesso, perche' lo stesso quadrante una seconda volta CHIUDE
        // il pannello; un altro lo aggiorna sul posto, ed e' l'aggiornamento che qui conta.
        client.Cpu = ["9.0 %", "3.0 %"];
        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("memory|memory.used.percent|"));

        Assert.NotNull(viewModel.SelectedProcess);
        Assert.Equal(22, viewModel.SelectedProcess!.Pid);
    }

    [Fact]
    public async Task ChangingTheSelectedRowDisarmsTheConfirmation()
    {
        // La regola che evita l'incidente peggiore: conferma armata su un processo, l'utente
        // cambia idea e ne seleziona un altro, e il clic successivo terminerebbe quello nuovo
        // senza averlo mai confermato.
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        viewModel.SelectedProcess = viewModel.Processes.First();
        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        Assert.True(viewModel.IsAwaitingEndConfirmation);
        Assert.Empty(client.Killed);

        viewModel.SelectedProcess = viewModel.Processes.Last();

        Assert.False(viewModel.IsAwaitingEndConfirmation);
        Assert.Empty(client.Killed);
    }

    [Fact]
    public async Task EndingAProcessTakesTwoClicks()
    {
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));
        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 11);

        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);
        Assert.Empty(client.Killed);

        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        Assert.Equal([11], client.Killed);
        Assert.False(viewModel.IsAwaitingEndConfirmation);
    }

    [Fact]
    public async Task ClickingTheSameGaugeAgainClosesThePanel()
    {
        // Il gesto che chiunque prova per primo per far sparire cio' che ha appena fatto
        // comparire. Prima riapriva lo stesso elenco, e l'unico modo di chiuderlo era il
        // pulsante Close in fondo a destra.
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));
        Assert.True(viewModel.IsProcessPanelOpen);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        Assert.False(viewModel.IsProcessPanelOpen);
        Assert.Empty(viewModel.Processes);
        Assert.Equal(["cpu"], client.Requested);
    }

    [Fact]
    public async Task ClickingAnotherGaugeSwitchesTheListWithoutClosing()
    {
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));
        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("memory|memory.used.percent|"));

        Assert.True(viewModel.IsProcessPanelOpen);
        Assert.Contains("memory", viewModel.ProcessesTitle, StringComparison.Ordinal);
        Assert.Equal(["cpu", "memory"], client.Requested);
    }

    [Fact]
    public async Task TheButtonSaysWhenTheSecondClickIsNeeded()
    {
        // Un pulsante solo, che cambia scritta: cosi' il fuoco della tastiera resta dov'e'.
        // Con due pulsanti alternati, al primo clic quello premuto spariva.
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));
        viewModel.SelectedProcess = viewModel.Processes.First();

        Assert.Equal("End process", viewModel.EndButtonText);

        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        Assert.Equal("Click again to end it", viewModel.EndButtonText);

        viewModel.SelectedProcess = viewModel.Processes.Last();

        Assert.Equal("End process", viewModel.EndButtonText);
    }

    [Fact]
    public void ARowReadsInFullWithItsHeadings() =>
        Assert.Equal(
            "claude, CPU 15.1 %, memory 228.5 MiB, I/O 1.1 MiB/s",
            new ProcessRowState(1, "claude", "15.1 %", "228.5 MiB", "1.1 MiB/s").AccessibleName);

    [Fact]
    public async Task ClosingThePanelForgetsEverything()
    {
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));
        viewModel.SelectedProcess = viewModel.Processes.First();

        viewModel.CloseProcessPanelCommand.Execute(parameter: null);

        Assert.False(viewModel.IsProcessPanelOpen);
        Assert.Empty(viewModel.Processes);
        Assert.Null(viewModel.SelectedProcess);
        Assert.False(viewModel.IsAwaitingEndConfirmation);
    }

    [Fact]
    public async Task AClickWhileTheFirstReadIsInFlightIsNotDropped()
    {
        // Macchina remota lenta: la prima lettura dell'elenco non torna subito. Nel frattempo
        // chi ha cliccato clicca ancora — per chiudere, o per passare a un altro quadrante —
        // e quel clic deve contare. Prima veniva scartato: il comando e' UNO per tutti i
        // quadranti, e un comando asincrono in esecuzione rifiuta le esecuzioni concorrenti.
        FakeProcessClient client = new() { PendingRead = new TaskCompletionSource<ProcessFetch>() };
        MainViewModel viewModel = new(client, configurationProblem: null);

        Task first = viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        Assert.True(viewModel.IsProcessPanelOpen);
        Assert.True(viewModel.OpenProcessesCommand.CanExecute(RowFor("memory|memory.used.percent|")));

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        Assert.False(viewModel.IsProcessPanelOpen);

        // E la risposta arrivata in ritardo per un pannello ormai chiuso non lo riempie.
        client.PendingRead.SetResult(new ProcessFetch(
            ServiceOutcome.Ok, string.Empty, [new ProcessRowState(99, "in ritardo", "99 %", "1 MiB")]));
        await first;

        Assert.False(viewModel.IsProcessPanelOpen);
        Assert.Empty(viewModel.Processes);
    }

    [Fact]
    public async Task ALateResponseDoesNotLandUnderTheWrongTitle()
    {
        FakeProcessClient client = new();
        TaskCompletionSource<ProcessFetch> inFlight = new();
        client.PendingRead = inFlight;
        MainViewModel viewModel = new(client, configurationProblem: null);

        Task cpu = viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        // Il secondo quadrante risponde subito; il primo, dopo.
        client.PendingRead = null;
        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("memory|memory.used.percent|"));

        Assert.Contains("memory", viewModel.ProcessesTitle, StringComparison.Ordinal);
        Assert.Equal(["affamato", "tranquillo"], viewModel.Processes.Select(row => row.Name));

        inFlight.SetResult(new ProcessFetch(
            ServiceOutcome.Ok, string.Empty, [new ProcessRowState(99, "in ritardo", "99 %", "1 MiB")]));
        await cpu;

        Assert.Equal(["affamato", "tranquillo"], viewModel.Processes.Select(row => row.Name));
    }

    private sealed class FakeProcessClient : IMetricsClient
    {
        public IReadOnlyList<string> Cpu { get; set; } = ["5.0 %", "1.0 %"];

        /// <summary>Se impostata, la prossima lettura dell'elenco risponde solo quando lo dice il test.</summary>
        public TaskCompletionSource<ProcessFetch>? PendingRead { get; set; }

        public List<int> Killed { get; } = [];

        public List<string> Requested { get; } = [];

        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.LocalChannel();

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.Unreachable, "spenta", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Ok, string.Empty, MetricCatalog.Empty));

        public Task<HistoryFetch> GetHistoryAsync(
            HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));

        public Task<ProcessFetch> GetProcessesAsync(
            string by, int top, CancellationToken cancellationToken)
        {
            Requested.Add(by);

            if (PendingRead is { } expected)
            {
                return expected.Task;
            }

            return Task.FromResult(new ProcessFetch(
                ServiceOutcome.Ok,
                string.Empty,
                [
                    new ProcessRowState(11, "affamato", Cpu[0], "100 MiB"),
                    new ProcessRowState(22, "tranquillo", Cpu[1], "10 MiB"),
                ]));
        }

        public Task<KillFetch> KillProcessAsync(int pid, CancellationToken cancellationToken)
        {
            Killed.Add(pid);

            return Task.FromResult(new KillFetch(ServiceOutcome.Ok, string.Empty));
        }
    }
}
