using FluentAvalonia.UI.Controls;
using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// Il pallino accanto a ogni macchina: cosa dice, e chi lo aggiorna.
/// </summary>
/// <remarks>
/// Due regole. La prima: il colore segue la stessa regola della barra di stato, con la sua
/// grazia di dieci secondi, cosi' un pallino rosso e una barra rossa vogliono dire la stessa
/// cosa. La seconda: le macchine che non si stanno guardando vengono sondate da sole, senza
/// che nessuno ci clicchi sopra e senza che il giro dei quadranti le aspetti.
/// </remarks>
public class MachineStatusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static ObserverEndpoint RemoteAt(string host) =>
        ObserverEndpoint.Remote(new Uri($"https://{host}:5058/"), "token", host, new string('a', 64));

    [Fact]
    public void AFreshRowHasNoStatusYet()
    {
        MachineRow row = new(RemoteAt("altra"));

        Assert.True(row.IsUnknown);
        Assert.False(row.IsReachable);
        Assert.Equal("Not checked yet", row.Detail);
    }

    [Fact]
    public void AGoodAnswerMarksTheMachineReachable()
    {
        MachineRow row = new(RemoteAt("altra"));

        row.Record(ServiceOutcome.Ok, string.Empty, T0);

        Assert.True(row.IsReachable);
        Assert.Equal("Reachable", row.Detail);
        Assert.Contains("Reachable", row.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalIsAWarningThenAFaultAndAGoodAnswerResetsIt()
    {
        // La stessa regola della barra di stato: un servizio che sta ancora partendo rifiuta,
        // e per dieci secondi e' normale. Dopo, no.
        MachineRow row = new(RemoteAt("altra"));

        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0);
        Assert.True(row.IsWarning);

        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0 + StatusEscalation.GracePeriod + TimeSpan.FromSeconds(1));
        Assert.True(row.IsFaulted);

        // E una risposta buona azzera la serie: il guasto successivo ricomincia da capo.
        row.Record(ServiceOutcome.Ok, string.Empty, T0 + TimeSpan.FromMinutes(1));
        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0 + TimeSpan.FromMinutes(1));
        Assert.True(row.IsWarning);
    }

    [Fact]
    public void WithinTheGraceTheRowDoesNotYetSayForHowLong()
    {
        // Un contatore che parte su ogni singhiozzo insegna a ignorarlo, ed e' esattamente
        // cio' che i dieci secondi di tolleranza esistono per impedire.
        MachineRow row = new(RemoteAt("altra"));

        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0);

        Assert.True(row.IsWarning);
        Assert.Equal(string.Empty, row.DowntimeText);
        Assert.Equal(string.Empty, row.Subtitle);
    }

    [Fact]
    public void PastTheGraceTheRowSaysHowLongTheFaultHasLasted()
    {
        MachineRow row = new(RemoteAt("altra"));

        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0);
        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0 + TimeSpan.FromMinutes(3));

        Assert.True(row.IsFaulted);
        Assert.Equal("for 3 min", row.DowntimeText);
        Assert.Equal("for 3 min", row.Subtitle);

        // La durata si sente anche senza vedere la riga: il suggerimento e il nome accessibile
        // passano dallo stesso testo, cosi' non possono divergere.
        Assert.Contains("for 3 min", row.ToolTipText, StringComparison.Ordinal);
        Assert.Contains("for 3 min", row.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void AServiceThatIsNotSamplingSaysForHowLongEvenWhenItIsNotRed()
    {
        // Il ramo giallo, che e' la ragione per cui il cancello e' il TONO e non lo stato
        // IsFaulted: un servizio raggiungibile che non ha ancora campionato resta un avviso, mai
        // un rosso, e puo' durare giorni. Un cancello scritto sul rosso lo lascerebbe senza
        // durata proprio mentre e' la cosa che dura di piu'.
        MachineRow row = new(RemoteAt("altra"));

        row.Record(ServiceOutcome.NotReadyYet, "warming up", T0);
        row.Record(ServiceOutcome.NotReadyYet, "warming up", T0 + TimeSpan.FromMinutes(7));

        Assert.True(row.IsWarning);
        Assert.False(row.IsFaulted);
        Assert.Equal("for 7 min", row.DowntimeText);
    }

    [Fact]
    public void RecordingAFaultNotifiesTheDurationTheSubtitleAndTheTooltip()
    {
        // Subtitle e' il Text della seconda riga: senza la sua notifica la durata
        // cambierebbe e la riga continuerebbe a dire la cosa di prima, con la suite verde.
        MachineRow row = new(RemoteAt("altra"));
        List<string> notified = [];
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? string.Empty);

        row.Record(ServiceOutcome.TokenRejected, "rejected", T0);

        Assert.Contains(nameof(MachineRow.DowntimeText), notified);
        Assert.Contains(nameof(MachineRow.Subtitle), notified);
        Assert.Contains(nameof(MachineRow.ToolTipText), notified);
    }

    [Fact]
    public void AMachineThatComesBackNoLongerShowsTheDuration()
    {
        MachineRow row = new(RemoteAt("altra"));

        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0);
        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0 + TimeSpan.FromMinutes(3));
        row.Record(ServiceOutcome.Ok, string.Empty, T0 + TimeSpan.FromMinutes(4));

        Assert.Equal(string.Empty, row.DowntimeText);

        // Niente coda: la descrizione torna a essere nome e stato, senza durata appiccicata.
        Assert.EndsWith(": Reachable", row.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void ARejectedTokenSaysForHowLongFromTheFirstReading()
    {
        // Non ha tolleranza: fra un minuto sara' identico, quindi la durata parte subito.
        MachineRow row = new(RemoteAt("altra"));

        row.Record(ServiceOutcome.TokenRejected, "rejected", T0);

        Assert.True(row.IsFaulted);
        Assert.Equal("for under 1 min", row.DowntimeText);
    }

    [Fact]
    public void ARejectedTokenIsAFaultWithNoGrace()
    {
        // Fra un minuto sara' identico: non c'e' grazia che tenga.
        MachineRow row = new(RemoteAt("altra"));

        row.Record(ServiceOutcome.TokenRejected, "rejected", T0);

        Assert.True(row.IsFaulted);
    }

    [Fact]
    public async Task TheMachinesNotBeingWatchedAreProbedOnTheirOwn()
    {
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint alive = RemoteAt("viva");
        ObserverEndpoint down = RemoteAt("spenta");

        List<ObserverEndpoint> opened = [];

        MainViewModel viewModel = new(
            client: new RespondingClient(local),
            configurationProblem: null,
            machineList: new MachineListResult([local, alive, down], []),
            openMachine: endpoint =>
            {
                opened.Add(endpoint);

                return endpoint == alive ? new RespondingClient(endpoint) : new UnreachableClient(endpoint);
            });

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(cancellation.Token);

        while (!cancellation.IsCancellationRequested && viewModel.Machines.Any(row => row.IsUnknown))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        // La macchina guardata segue il giro principale; le altre due la sonda.
        Assert.True(viewModel.Machines[0].IsReachable);
        Assert.True(viewModel.Machines[1].IsReachable);
        Assert.True(viewModel.Machines[2].IsWarning, viewModel.Machines[2].Detail);

        // La sonda NON apre un client verso la macchina che si sta gia' guardando.
        Assert.DoesNotContain(local, opened);
        Assert.Contains(alive, opened);
        Assert.Contains(down, opened);

        await cancellation.CancelAsync();

        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
            // Fine del test.
        }
    }

    [Fact]
    public void ChangingStatusNotifiesEveryStatusFlagAndTheAccessibleName()
    {
        // Sono i nomi a cui sono legate le Classes dell'Ellipse: toglierne uno dall'attributo
        // lascerebbe il pallino grigio per sempre, e nessun test lo direbbe.
        MachineRow row = new(RemoteAt("altra"));
        List<string> notified = [];
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? string.Empty);

        row.Record(ServiceOutcome.Ok, string.Empty, T0);

        Assert.Contains(nameof(MachineRow.IsUnknown), notified);
        Assert.Contains(nameof(MachineRow.IsReachable), notified);
        Assert.Contains(nameof(MachineRow.IsWarning), notified);
        Assert.Contains(nameof(MachineRow.IsFaulted), notified);
        Assert.Contains(nameof(MachineRow.AccessibleName), notified);
    }

    [Fact]
    public async Task TheWatchedMachineIsNotProbedEvenWhenItIsRemote()
    {
        // "Salta la selezionata" e "salta la locale" sono indistinguibili quando la guardata
        // e' la prima dell'elenco. Qui la guardata e' la seconda, e remota.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint alive = RemoteAt("viva");
        ObserverEndpoint down = RemoteAt("spenta");
        List<ObserverEndpoint> opened = [];

        MainViewModel viewModel = new(
            client: new RespondingClient(alive),
            configurationProblem: null,
            machineList: new MachineListResult([local, alive, down], []),
            openMachine: endpoint =>
            {
                opened.Add(endpoint);

                return endpoint == down ? new UnreachableClient(endpoint) : new RespondingClient(endpoint);
            });

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(cancellation.Token);

        while (!cancellation.IsCancellationRequested && viewModel.Machines.Any(row => row.IsUnknown))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Same(viewModel.Machines[1], viewModel.SelectedMachine);
        Assert.DoesNotContain(alive, opened);
        Assert.Contains(local, opened);
        Assert.Contains(down, opened);

        await Stop(cancellation, loop);
    }

    [Fact]
    public async Task WithNoSelectionTheWatchedMachineIsStillNotProbed()
    {
        // La lista non dovrebbe mai azzerare la selezione (AlwaysSelected), ma se succede il
        // giro principale continua a leggere la stessa macchina, e la sonda NON deve leggerla
        // una seconda volta: e' la voce guardata a contare, non la selezione.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint other = RemoteAt("altra");
        FakeClock clock = new();
        List<ObserverEndpoint> opened = [];

        MainViewModel viewModel = new(
            client: new RespondingClient(local),
            configurationProblem: null,
            clock: clock.Now,
            machineList: new MachineListResult([local, other], []),
            openMachine: endpoint =>
            {
                opened.Add(endpoint);

                return new RespondingClient(endpoint);
            });

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(cancellation.Token);

        while (!cancellation.IsCancellationRequested && viewModel.Machines[1].IsUnknown)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        viewModel.SelectedMachine = null;
        clock.Advance(MainViewModel.StatusRefreshInterval + TimeSpan.FromSeconds(1));

        while (!cancellation.IsCancellationRequested && opened.Count(endpoint => endpoint == other) < 2)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(opened.Count(endpoint => endpoint == other) >= 2, "la seconda sonda non e' partita");
        Assert.DoesNotContain(local, opened);
        Assert.True(viewModel.Machines[0].IsReachable);

        await Stop(cancellation, loop);
    }

    [Fact]
    public async Task AHangingProbeDoesNotStopTheLoopAndDoesNotStartASecondOne()
    {
        // Le due promesse delle sonde, provate con un client che NON risponde finche' il test
        // non lo dice: un client che risponde subito le lascerebbe entrambe mutabili.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint slow = RemoteAt("lenta");
        FakeClock clock = new();
        CountingClient watched = new(local);
        HangingClient hanging = new(slow);
        int openCount = 0;

        MainViewModel viewModel = new(
            client: watched,
            configurationProblem: null,
            clock: clock.Now,
            machineList: new MachineListResult([local, slow], []),
            openMachine: _ =>
            {
                openCount++;

                return hanging;
            });

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
        Task loop = viewModel.RunAsync(cancellation.Token);

        // La sonda e' partita e resta appesa; il giro principale intanto legge ancora.
        while (!cancellation.IsCancellationRequested && watched.Reads < 3)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(watched.Reads >= 3, "il giro principale ha aspettato la sonda");
        Assert.Equal(1, openCount);
        Assert.True(viewModel.Machines[1].IsProbing);
        Assert.True(viewModel.Machines[1].IsUnknown);

        // Passano due cadenze: con la sonda ancora in volo non ne parte una seconda.
        clock.Advance(MainViewModel.StatusRefreshInterval * 2);
        int before = watched.Reads;

        while (!cancellation.IsCancellationRequested && watched.Reads < before + 2)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Equal(1, openCount);

        // Quando torna, il pallino cambia e la voce e' di nuovo sondabile.
        hanging.Respond(new SnapshotFetch(ServiceOutcome.Unreachable, "spenta", null));

        while (!cancellation.IsCancellationRequested && viewModel.Machines[1].IsUnknown)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[1].IsWarning, viewModel.Machines[1].Detail);
        Assert.False(viewModel.Machines[1].IsProbing);

        await Stop(cancellation, loop);
    }

    [Fact]
    public async Task AProbeThatThrowsBecomesARedDotAndTheLoopGoesOn()
    {
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint broken = RemoteAt("rotta");
        CountingClient watched = new(local);

        MainViewModel viewModel = new(
            client: watched,
            configurationProblem: null,
            machineList: new MachineListResult([local, broken], []),
            openMachine: endpoint => new ThrowingClient(endpoint));

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(cancellation.Token);

        while (!cancellation.IsCancellationRequested && viewModel.Machines[1].IsUnknown)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[1].IsFaulted, viewModel.Machines[1].Detail);
        Assert.Equal("Reading failed", viewModel.Machines[1].Detail);
        Assert.False(viewModel.Machines[1].IsProbing);

        // E il giro principale e' vivo.
        int before = watched.Reads;

        while (!cancellation.IsCancellationRequested && watched.Reads <= before)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(watched.Reads > before, "il giro principale si e' fermato");

        await Stop(cancellation, loop);
    }

    [Fact]
    public async Task RereadingTheMachineRestartsTheDurationFromZero()
    {
        // Stesso posto nell'elenco, macchina cambiata sotto: "giu' da mezz'ora" riferito alla
        // precedente sarebbe una bugia, ed e' una bugia che nessuno andrebbe a cercare.
        // Il percorso passa da rereadEndpoint -> ProbeAsync -> Update, che e' interno: si
        // prova da qui, dove e' raggiungibile, invece di allargare la superficie della classe.
        // Il client rifiuta ANCHE la credenziale nuova, altrimenti una lettura buona azzererebbe
        // tutto per un'altra strada e il test passerebbe anche senza l'azzeramento.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint oldEndpoint = RemoteAt("ruotata");
        ObserverEndpoint newEndpoint = oldEndpoint with { ApiToken = "nuovo" };
        FakeClock clock = new();
        bool rotate = false;

        MainViewModel viewModel = new(
            client: new RespondingClient(local),
            configurationProblem: null,
            clock: clock.Now,
            machineList: new MachineListResult([local, oldEndpoint], []),
            openMachine: endpoint => new TokenRejectedClient(endpoint),
            rereadEndpoint: _ => rotate ? newEndpoint : null);

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
        Task loop = viewModel.RunAsync(cancellation.Token);

        // Un token rifiutato e' rosso dal primo istante: niente tolleranza da aspettare.
        while (!cancellation.IsCancellationRequested && !viewModel.Machines[1].IsFaulted)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        // Il guasto invecchia. L'orologio si sposta una volta sola: sono le sonde successive
        // a leggerlo, e la riga arriva a dire mezz'ora.
        clock.Advance(TimeSpan.FromMinutes(30));

        while (!cancellation.IsCancellationRequested
            && !viewModel.Machines[1].DowntimeText.Contains("30 min", StringComparison.Ordinal))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Equal("for 30 min", viewModel.Machines[1].DowntimeText);

        // Adesso la macchina cambia sotto: la sonda successiva la rilegge. L'orologio deve
        // avanzare, altrimenti la sonda non scatta piu' e non c'e' nessuna lettura successiva.
        rotate = true;

        while (!cancellation.IsCancellationRequested && viewModel.Machines[1].Endpoint != newEndpoint)
        {
            clock.Advance(MainViewModel.StatusRefreshInterval + TimeSpan.FromSeconds(1));
            await Task.Delay(50, CancellationToken.None);
        }

        // La macchina e' ancora giu', ma e' un'ALTRA macchina: la misura ricomincia da zero e
        // il rifiuto successivo riparte da "under 1 min", invece di continuare la mezz'ora
        // della precedente. Senza l'azzeramento dentro Update la durata proseguirebbe.
        Assert.Equal(newEndpoint, viewModel.Machines[1].Endpoint);
        Assert.True(viewModel.Machines[1].IsFaulted || viewModel.Machines[1].IsWarning);
        // Vuota se si guarda fra l'azzeramento e la lettura successiva, "under 1 min" se si
        // guarda dopo. Senza l'azzeramento sarebbe la mezz'ora di prima, che continua a
        // crescere: un'asserzione su un valore preciso non basterebbe a distinguerlo.
        string after = viewModel.Machines[1].DowntimeText;
        Assert.True(after.Length == 0 || after == "for under 1 min", $"durata dopo la rilettura: '{after}'");

        await Stop(cancellation, loop);
    }

    [Fact]
    public async Task AfterARejectedTokenTheProbeRereadsTheMachine()
    {
        // "observer token set" a finestra aperta, su una macchina NON guardata: la sonda
        // successiva deve partire con la credenziale nuova, non con quella letta all'avvio.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint oldEndpoint = RemoteAt("ruotata");
        ObserverEndpoint newEndpoint = oldEndpoint with { ApiToken = "nuovo" };
        FakeClock clock = new();
        List<ObserverEndpoint> opened = [];

        MainViewModel viewModel = new(
            client: new RespondingClient(local),
            configurationProblem: null,
            clock: clock.Now,
            machineList: new MachineListResult([local, oldEndpoint], []),
            openMachine: endpoint =>
            {
                opened.Add(endpoint);

                return endpoint == newEndpoint ? new RespondingClient(endpoint) : new TokenRejectedClient(endpoint);
            },
            rereadEndpoint: _ => newEndpoint);

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(cancellation.Token);

        while (!cancellation.IsCancellationRequested && !viewModel.Machines[1].IsFaulted)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Equal("Token rejected", viewModel.Machines[1].Detail);

        clock.Advance(MainViewModel.StatusRefreshInterval + TimeSpan.FromSeconds(1));

        while (!cancellation.IsCancellationRequested && !viewModel.Machines[1].IsReachable)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[1].IsReachable, viewModel.Machines[1].Detail);
        Assert.Contains(newEndpoint, opened);
        Assert.Equal(newEndpoint, viewModel.Machines[1].Endpoint);

        await Stop(cancellation, loop);
    }

    [Fact]
    public async Task ChoosingAMachineTheProbeAlreadyKnowsIsDownTheBarDoesNotSayConnecting()
    {
        // Barra e pallino hanno un orologio solo: la sonda sa da sedici secondi che la macchina
        // e' spenta, e cliccandoci sopra la barra deve aprire rossa, non "Connecting" per altri
        // dieci secondi mentre il pallino accanto e' gia' rosso.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint down = RemoteAt("spenta");
        FakeClock clock = new();

        MainViewModel viewModel = new(
            client: new RespondingClient(local),
            configurationProblem: null,
            clock: clock.Now,
            machineList: new MachineListResult([local, down], []),
            openMachine: endpoint => new UnreachableClient(endpoint));

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
        Task loop = viewModel.RunAsync(cancellation.Token);

        while (!cancellation.IsCancellationRequested && !viewModel.Machines[1].IsWarning)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        clock.Advance(MainViewModel.StatusRefreshInterval + TimeSpan.FromSeconds(1));

        while (!cancellation.IsCancellationRequested && !viewModel.Machines[1].IsFaulted)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[1].IsFaulted, viewModel.Machines[1].Detail);

        viewModel.SelectedMachine = viewModel.Machines[1];

        while (!cancellation.IsCancellationRequested && viewModel.StatusTitle == "Connecting")
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Equal(FAInfoBarSeverity.Error, viewModel.StatusSeverity);
        Assert.True(viewModel.Machines[1].IsFaulted);

        await Stop(cancellation, loop);
    }

    [Fact]
    public async Task TheDotOfTheWatchedMachineFollowsTheStatusBarEvenWhenItFails()
    {
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        FakeClock clock = new();

        MainViewModel viewModel = new(
            client: new UnreachableClient(local),
            configurationProblem: null,
            clock: clock.Now,
            machineList: new MachineListResult([local], []));

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(cancellation.Token);

        while (!cancellation.IsCancellationRequested && viewModel.Machines[0].IsUnknown)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[0].IsWarning, viewModel.Machines[0].Detail);
        Assert.Equal(FAInfoBarSeverity.Informational, viewModel.StatusSeverity);

        clock.Advance(StatusEscalation.GracePeriod + TimeSpan.FromSeconds(1));

        while (!cancellation.IsCancellationRequested && !viewModel.Machines[0].IsFaulted)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[0].IsFaulted, viewModel.Machines[0].Detail);
        Assert.Equal(FAInfoBarSeverity.Error, viewModel.StatusSeverity);

        await Stop(cancellation, loop);
    }

    private static async Task Stop(CancellationTokenSource cancellation, Task loop)
    {
        await cancellation.CancelAsync();

        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
            // Fine del test.
        }
    }

    private sealed class RespondingClient(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(
                ServiceOutcome.Ok,
                string.Empty,
                new MachineSnapshot(
                    MachineSnapshot.CurrentSchemaVersion,
                    DateTimeOffset.UnixEpoch,
                    [new MetricSnapshot("cpu", CollectorStatus.Ok, null, [])])));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Ok, string.Empty, MetricCatalog.Empty));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));
    }

    /// <summary>Risponde bene e conta quante volte e' stato letto.</summary>
    private sealed class CountingClient(ObserverEndpoint endpoint) : IMetricsClient
    {
        private int reads;

        public int Reads => Volatile.Read(ref reads);

        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref reads);

            return Task.FromResult(new SnapshotFetch(
                ServiceOutcome.Ok,
                string.Empty,
                new MachineSnapshot(
                    MachineSnapshot.CurrentSchemaVersion,
                    DateTimeOffset.UnixEpoch,
                    [new MetricSnapshot("cpu", CollectorStatus.Ok, null, [])])));
        }

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Ok, string.Empty, MetricCatalog.Empty));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));
    }

    /// <summary>Non risponde finche' il test non chiama <see cref="Rispondi"/>.</summary>
    private sealed class HangingClient(ObserverEndpoint endpoint) : IMetricsClient
    {
        private readonly TaskCompletionSource<SnapshotFetch> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ObserverEndpoint Endpoint { get; } = endpoint;

        public void Respond(SnapshotFetch fetch) => pending.TrySetResult(fetch);

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) => pending.Task;

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Unreachable, "lenta", null));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Unreachable, "lenta", null));
    }

    /// <summary>Lancia invece di rispondere: un client che non si costruisce, un DNS che esplode.</summary>
    private sealed class ThrowingClient(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    /// <summary>Un servizio che rifiuta il token.</summary>
    private sealed class TokenRejectedClient(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.TokenRejected, "rejected", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.TokenRejected, "rejected", null));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.TokenRejected, "rejected", null));
    }

    private sealed class UnreachableClient(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.Unreachable, "spenta", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Unreachable, "spenta", null));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Unreachable, "spenta", null));
    }
}