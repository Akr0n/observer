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
public class PannelloProcessiTests
{
    private static MetricRow Riga(string chiave) =>
        new(new MetricRowState(chiave, "etichetta", "valore", 0.5d, MetricSeverity.Ok));

    [Theory]
    [InlineData("cpu|cpu.usage.total|", "cpu")]
    [InlineData("memory|memory.used.percent|", "memory")]
    [InlineData("disk.activity|disk.busy.percent|Disk 0", "io")]
    public void DaiQuadrantiDiCpuMemoriaEAttivitaDiscoSiApreLElenco(string chiave, string attesa) =>
        Assert.Equal(attesa, ProcessResource.Da(chiave));

    [Theory]
    [InlineData("disk|disk.used.percent|C:")]
    [InlineData("")]
    [InlineData(null)]
    public void DaiQuadrantiDelloSpazioDiscoNonSiApreNiente(string? chiave)
    {
        // Non e' una dimenticanza. Lo spazio occupato su un volume non e' attribuibile a un
        // processo IN ESECUZIONE — chi ha scritto quei file magari non c'e' piu' da mesi. Un
        // pannello che si aprisse con l'elenco della CPU sotto il titolo di un volume direbbe
        // una cosa falsa.
        Assert.Null(ProcessResource.Da(chiave));
    }

    [Fact]
    public void SoloLoSpazioDeiDischiNonECliccabile()
    {
        Assert.False(Riga("disk|disk.used.percent|C:").PuoMostrareProcessi);
        Assert.True(Riga("disk.activity|disk.busy.percent|Disk 0").PuoMostrareProcessi);
        Assert.True(Riga("cpu|cpu.usage.total|").PuoMostrareProcessi);
    }

    [Fact]
    public async Task DalQuadranteDellAttivitaDiscoSiChiedeLIoDellInteraMacchina()
    {
        // Il quadrante e' di UN disco, l'elenco no: i contatori sono per processo, non per
        // dispositivo. Il titolo deve dirlo, e al servizio si chiede "io", non la CPU.
        ClienteConProcessi cliente = new();
        MainViewModel viewModel = new(cliente, problemaDiConfigurazione: null);

        await viewModel.ApriProcessiCommand.ExecuteAsync(Riga("disk.activity|disk.busy.percent|Disk 0"));

        Assert.Equal(["io"], cliente.Chiesti);
        Assert.Contains("I/O", viewModel.ProcessiTitolo, StringComparison.Ordinal);
        Assert.Contains("whole machine", viewModel.ProcessiTitolo, StringComparison.Ordinal);
    }

    [Fact]
    public void UnTassoDiIoSiFormattaInBytePerSecondoEUnoIgnotoEUnTrattino()
    {
        // Un trattino e non "0 B/s": sono due affermazioni diverse, e la seconda su un elenco
        // ordinato per I/O sposterebbe l'attenzione sul programma sbagliato.
        Assert.Equal("1.5 MiB/s", ProcessoMostrato.Da(new ProcessWire(1, "copia", 0d, 10, 1_572_864d)).Io);
        Assert.Equal("—", ProcessoMostrato.Da(new ProcessWire(1, "ignoto", 0d, 10, null)).Io);
    }

    [Fact]
    public async Task LaSelezioneSopravviveAllAggiornamento()
    {
        // L'elenco si riscrive una volta al secondo. Senza tenere la selezione sul PID, la
        // riga puntata si deselezionerebbe da sola mentre ci si prepara a terminarla.
        ClienteConProcessi cliente = new();
        MainViewModel viewModel = new(cliente, problemaDiConfigurazione: null);

        await viewModel.ApriProcessiCommand.ExecuteAsync(Riga("cpu|cpu.usage.total|"));

        viewModel.ProcessoSelezionato = viewModel.Processi.Single(riga => riga.Pid == 22);

        // Stessi PID, valori nuovi: e' cio' che succede a ogni giro. Si passa per un altro
        // quadrante e non per lo stesso, perche' lo stesso quadrante una seconda volta CHIUDE
        // il pannello; un altro lo aggiorna sul posto, ed e' l'aggiornamento che qui conta.
        cliente.Cpu = ["9.0 %", "3.0 %"];
        await viewModel.ApriProcessiCommand.ExecuteAsync(Riga("memory|memory.used.percent|"));

        Assert.NotNull(viewModel.ProcessoSelezionato);
        Assert.Equal(22, viewModel.ProcessoSelezionato!.Pid);
    }

    [Fact]
    public async Task CambiareRigaDisarmaLaConferma()
    {
        // La regola che evita l'incidente peggiore: conferma armata su un processo, l'utente
        // cambia idea e ne seleziona un altro, e il clic successivo terminerebbe quello nuovo
        // senza averlo mai confermato.
        ClienteConProcessi cliente = new();
        MainViewModel viewModel = new(cliente, problemaDiConfigurazione: null);

        await viewModel.ApriProcessiCommand.ExecuteAsync(Riga("cpu|cpu.usage.total|"));

        viewModel.ProcessoSelezionato = viewModel.Processi.First();
        await viewModel.TerminaSelezionatoCommand.ExecuteAsync(parameter: null);

        Assert.True(viewModel.ConfermaTerminazione);
        Assert.Empty(cliente.Terminati);

        viewModel.ProcessoSelezionato = viewModel.Processi.Last();

        Assert.False(viewModel.ConfermaTerminazione);
        Assert.Empty(cliente.Terminati);
    }

    [Fact]
    public async Task ServonoDueClicPerTerminareDavvero()
    {
        ClienteConProcessi cliente = new();
        MainViewModel viewModel = new(cliente, problemaDiConfigurazione: null);

        await viewModel.ApriProcessiCommand.ExecuteAsync(Riga("cpu|cpu.usage.total|"));
        viewModel.ProcessoSelezionato = viewModel.Processi.Single(riga => riga.Pid == 11);

        await viewModel.TerminaSelezionatoCommand.ExecuteAsync(parameter: null);
        Assert.Empty(cliente.Terminati);

        await viewModel.TerminaSelezionatoCommand.ExecuteAsync(parameter: null);

        Assert.Equal([11], cliente.Terminati);
        Assert.False(viewModel.ConfermaTerminazione);
    }

    [Fact]
    public async Task CliccareDiNuovoLoStessoQuadranteChiudeIlPannello()
    {
        // Il gesto che chiunque prova per primo per far sparire cio' che ha appena fatto
        // comparire. Prima riapriva lo stesso elenco, e l'unico modo di chiuderlo era il
        // pulsante Close in fondo a destra.
        ClienteConProcessi cliente = new();
        MainViewModel viewModel = new(cliente, problemaDiConfigurazione: null);

        await viewModel.ApriProcessiCommand.ExecuteAsync(Riga("cpu|cpu.usage.total|"));
        Assert.True(viewModel.ProcessiVisibili);

        await viewModel.ApriProcessiCommand.ExecuteAsync(Riga("cpu|cpu.usage.total|"));

        Assert.False(viewModel.ProcessiVisibili);
        Assert.Empty(viewModel.Processi);
        Assert.Equal(["cpu"], cliente.Chiesti);
    }

    [Fact]
    public async Task CliccareUnAltroQuadranteCambiaElencoSenzaChiudere()
    {
        ClienteConProcessi cliente = new();
        MainViewModel viewModel = new(cliente, problemaDiConfigurazione: null);

        await viewModel.ApriProcessiCommand.ExecuteAsync(Riga("cpu|cpu.usage.total|"));
        await viewModel.ApriProcessiCommand.ExecuteAsync(Riga("memory|memory.used.percent|"));

        Assert.True(viewModel.ProcessiVisibili);
        Assert.Contains("memory", viewModel.ProcessiTitolo, StringComparison.Ordinal);
        Assert.Equal(["cpu", "memory"], cliente.Chiesti);
    }

    [Fact]
    public async Task IlPulsanteDiceQuandoServeIlSecondoClic()
    {
        // Un pulsante solo, che cambia scritta: cosi' il fuoco della tastiera resta dov'e'.
        // Con due pulsanti alternati, al primo clic quello premuto spariva.
        ClienteConProcessi cliente = new();
        MainViewModel viewModel = new(cliente, problemaDiConfigurazione: null);

        await viewModel.ApriProcessiCommand.ExecuteAsync(Riga("cpu|cpu.usage.total|"));
        viewModel.ProcessoSelezionato = viewModel.Processi.First();

        Assert.Equal("End process", viewModel.TestoTermina);

        await viewModel.TerminaSelezionatoCommand.ExecuteAsync(parameter: null);

        Assert.Equal("Click again to end it", viewModel.TestoTermina);

        viewModel.ProcessoSelezionato = viewModel.Processi.Last();

        Assert.Equal("End process", viewModel.TestoTermina);
    }

    [Fact]
    public void UnaRigaSiLeggePerInteroConLeIntestazioni() =>
        Assert.Equal(
            "claude, CPU 15.1 %, memory 228.5 MiB, I/O 1.1 MiB/s",
            new ProcessoMostrato(1, "claude", "15.1 %", "228.5 MiB", "1.1 MiB/s").Descrizione);

    [Fact]
    public async Task ChiudereIlPannelloDimenticaTutto()
    {
        ClienteConProcessi cliente = new();
        MainViewModel viewModel = new(cliente, problemaDiConfigurazione: null);

        await viewModel.ApriProcessiCommand.ExecuteAsync(Riga("cpu|cpu.usage.total|"));
        viewModel.ProcessoSelezionato = viewModel.Processi.First();

        viewModel.ChiudiProcessiCommand.Execute(parameter: null);

        Assert.False(viewModel.ProcessiVisibili);
        Assert.Empty(viewModel.Processi);
        Assert.Null(viewModel.ProcessoSelezionato);
        Assert.False(viewModel.ConfermaTerminazione);
    }

    [Fact]
    public async Task UnClicMentreLaPrimaLetturaEInVoloNonVieneScartato()
    {
        // Macchina remota lenta: la prima lettura dell'elenco non torna subito. Nel frattempo
        // chi ha cliccato clicca ancora — per chiudere, o per passare a un altro quadrante —
        // e quel clic deve contare. Prima veniva scartato: il comando e' UNO per tutti i
        // quadranti, e un comando asincrono in esecuzione rifiuta le esecuzioni concorrenti.
        ClienteConProcessi cliente = new() { Attesa = new TaskCompletionSource<ProcessFetch>() };
        MainViewModel viewModel = new(cliente, problemaDiConfigurazione: null);

        Task prima = viewModel.ApriProcessiCommand.ExecuteAsync(Riga("cpu|cpu.usage.total|"));

        Assert.True(viewModel.ProcessiVisibili);
        Assert.True(viewModel.ApriProcessiCommand.CanExecute(Riga("memory|memory.used.percent|")));

        await viewModel.ApriProcessiCommand.ExecuteAsync(Riga("cpu|cpu.usage.total|"));

        Assert.False(viewModel.ProcessiVisibili);

        // E la risposta arrivata in ritardo per un pannello ormai chiuso non lo riempie.
        cliente.Attesa.SetResult(new ProcessFetch(
            ServiceOutcome.Ok, string.Empty, [new ProcessoMostrato(99, "in ritardo", "99 %", "1 MiB")]));
        await prima;

        Assert.False(viewModel.ProcessiVisibili);
        Assert.Empty(viewModel.Processi);
    }

    [Fact]
    public async Task UnaRispostaInRitardoNonFinisceSottoIlTitoloDiUnAltroQuadrante()
    {
        ClienteConProcessi cliente = new();
        TaskCompletionSource<ProcessFetch> inVolo = new();
        cliente.Attesa = inVolo;
        MainViewModel viewModel = new(cliente, problemaDiConfigurazione: null);

        Task cpu = viewModel.ApriProcessiCommand.ExecuteAsync(Riga("cpu|cpu.usage.total|"));

        // Il secondo quadrante risponde subito; il primo, dopo.
        cliente.Attesa = null;
        await viewModel.ApriProcessiCommand.ExecuteAsync(Riga("memory|memory.used.percent|"));

        Assert.Contains("memory", viewModel.ProcessiTitolo, StringComparison.Ordinal);
        Assert.Equal(["affamato", "tranquillo"], viewModel.Processi.Select(riga => riga.Nome));

        inVolo.SetResult(new ProcessFetch(
            ServiceOutcome.Ok, string.Empty, [new ProcessoMostrato(99, "in ritardo", "99 %", "1 MiB")]));
        await cpu;

        Assert.Equal(["affamato", "tranquillo"], viewModel.Processi.Select(riga => riga.Nome));
    }

    private sealed class ClienteConProcessi : IMetricsClient
    {
        public IReadOnlyList<string> Cpu { get; set; } = ["5.0 %", "1.0 %"];

        /// <summary>Se impostata, la prossima lettura dell'elenco risponde solo quando lo dice il test.</summary>
        public TaskCompletionSource<ProcessFetch>? Attesa { get; set; }

        public List<int> Terminati { get; } = [];

        public List<string> Chiesti { get; } = [];

        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.CanaleLocale();

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.NonRaggiungibile, "spenta", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Ok, string.Empty, MetricCatalog.Empty));

        public Task<HistoryFetch> GetHistoryAsync(
            HistoryQuery richiesta, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));

        public Task<ProcessFetch> GetProcessesAsync(
            string per, int quanti, CancellationToken cancellationToken)
        {
            Chiesti.Add(per);

            if (Attesa is { } attesa)
            {
                return attesa.Task;
            }

            return Task.FromResult(new ProcessFetch(
                ServiceOutcome.Ok,
                string.Empty,
                [
                    new ProcessoMostrato(11, "affamato", Cpu[0], "100 MiB"),
                    new ProcessoMostrato(22, "tranquillo", Cpu[1], "10 MiB"),
                ]));
        }

        public Task<KillFetch> KillProcessAsync(int pid, CancellationToken cancellationToken)
        {
            Terminati.Add(pid);

            return Task.FromResult(new KillFetch(ServiceOutcome.Ok, string.Empty));
        }
    }
}
