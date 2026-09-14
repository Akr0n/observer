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
public class StatoMacchineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static ObserverEndpoint Remota(string nome) =>
        ObserverEndpoint.Remote(new Uri($"https://{nome}:5058/"), "token", nome, new string('a', 64));

    [Fact]
    public void AllInizioNessunaMacchinaHaUnoStato()
    {
        MachineRow voce = new(Remota("altra"));

        Assert.True(voce.IsUnknown);
        Assert.False(voce.IsReachable);
        Assert.Equal("Not checked yet", voce.Detail);
    }

    [Fact]
    public void UnaRispostaBuonaSegnaRaggiungibile()
    {
        MachineRow voce = new(Remota("altra"));

        voce.Record(ServiceOutcome.Ok, string.Empty, T0);

        Assert.True(voce.IsReachable);
        Assert.Equal("Reachable", voce.Detail);
        Assert.Contains("Reachable", voce.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void UnRifiutoRecenteEAttenzioneEDopoLaGraziaEGuasto()
    {
        // La stessa regola della barra di stato: un servizio che sta ancora partendo rifiuta,
        // e per dieci secondi e' normale. Dopo, no.
        MachineRow voce = new(Remota("altra"));

        voce.Record(ServiceOutcome.ConnectionRefused, "refused", T0);
        Assert.True(voce.IsWarning);

        voce.Record(ServiceOutcome.ConnectionRefused, "refused", T0 + StatusEscalation.GracePeriod + TimeSpan.FromSeconds(1));
        Assert.True(voce.IsFaulted);

        // E una risposta buona azzera la serie: il guasto successivo ricomincia da capo.
        voce.Record(ServiceOutcome.Ok, string.Empty, T0 + TimeSpan.FromMinutes(1));
        voce.Record(ServiceOutcome.ConnectionRefused, "refused", T0 + TimeSpan.FromMinutes(1));
        Assert.True(voce.IsWarning);
    }

    [Fact]
    public void DentroLaTolleranzaLaRigaNonDiceAncoraDaQuanto()
    {
        // Un contatore che parte su ogni singhiozzo insegna a ignorarlo, ed e' esattamente
        // cio' che i dieci secondi di tolleranza esistono per impedire.
        MachineRow voce = new(Remota("altra"));

        voce.Record(ServiceOutcome.ConnectionRefused, "refused", T0);

        Assert.True(voce.IsWarning);
        Assert.Equal(string.Empty, voce.DowntimeText);
        Assert.Equal(string.Empty, voce.Subtitle);
    }

    [Fact]
    public void PassataLaTolleranzaLaRigaDiceDaQuantoDuraIlGuasto()
    {
        MachineRow voce = new(Remota("altra"));

        voce.Record(ServiceOutcome.ConnectionRefused, "refused", T0);
        voce.Record(ServiceOutcome.ConnectionRefused, "refused", T0 + TimeSpan.FromMinutes(3));

        Assert.True(voce.IsFaulted);
        Assert.Equal("for 3 min", voce.DowntimeText);
        Assert.Equal("for 3 min", voce.Subtitle);

        // La durata si sente anche senza vedere la riga: il suggerimento e il nome accessibile
        // passano dallo stesso testo, cosi' non possono divergere.
        Assert.Contains("for 3 min", voce.ToolTipText, StringComparison.Ordinal);
        Assert.Contains("for 3 min", voce.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void UnServizioCheNonCampionaDiceDaQuantoAncheSeNonERosso()
    {
        // Il ramo giallo, che e' la ragione per cui il cancello e' il TONO e non lo stato
        // IsFaulted: un servizio raggiungibile che non ha ancora campionato resta un avviso, mai
        // un rosso, e puo' durare giorni. Un cancello scritto sul rosso lo lascerebbe senza
        // durata proprio mentre e' la cosa che dura di piu'.
        MachineRow voce = new(Remota("altra"));

        voce.Record(ServiceOutcome.NotReadyYet, "warming up", T0);
        voce.Record(ServiceOutcome.NotReadyYet, "warming up", T0 + TimeSpan.FromMinutes(7));

        Assert.True(voce.IsWarning);
        Assert.False(voce.IsFaulted);
        Assert.Equal("for 7 min", voce.DowntimeText);
    }

    [Fact]
    public void ScrivereLaDurataNotificaAncheLaRigaCheLaMostra()
    {
        // Subtitle e' il Text della seconda riga: senza la sua notifica la durata
        // cambierebbe e la riga continuerebbe a dire la cosa di prima, con la suite verde.
        MachineRow voce = new(Remota("altra"));
        List<string> notificate = [];
        voce.PropertyChanged += (_, e) => notificate.Add(e.PropertyName ?? string.Empty);

        voce.Record(ServiceOutcome.TokenRejected, "rejected", T0);

        Assert.Contains(nameof(MachineRow.DowntimeText), notificate);
        Assert.Contains(nameof(MachineRow.Subtitle), notificate);
        Assert.Contains(nameof(MachineRow.ToolTipText), notificate);
    }

    [Fact]
    public void UnaMacchinaCheTornaSuNonMostraPiuLaDurata()
    {
        MachineRow voce = new(Remota("altra"));

        voce.Record(ServiceOutcome.ConnectionRefused, "refused", T0);
        voce.Record(ServiceOutcome.ConnectionRefused, "refused", T0 + TimeSpan.FromMinutes(3));
        voce.Record(ServiceOutcome.Ok, string.Empty, T0 + TimeSpan.FromMinutes(4));

        Assert.Equal(string.Empty, voce.DowntimeText);

        // Niente coda: la descrizione torna a essere nome e stato, senza durata appiccicata.
        Assert.EndsWith(": Reachable", voce.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void UnTokenRifiutatoDiceDaQuantoDalPrimoIstante()
    {
        // Non ha tolleranza: fra un minuto sara' identico, quindi la durata parte subito.
        MachineRow voce = new(Remota("altra"));

        voce.Record(ServiceOutcome.TokenRejected, "rejected", T0);

        Assert.True(voce.IsFaulted);
        Assert.Equal("for under 1 min", voce.DowntimeText);
    }

    [Fact]
    public void UnTokenRifiutatoEGuastoDaSubito()
    {
        // Fra un minuto sara' identico: non c'e' grazia che tenga.
        MachineRow voce = new(Remota("altra"));

        voce.Record(ServiceOutcome.TokenRejected, "rejected", T0);

        Assert.True(voce.IsFaulted);
    }

    [Fact]
    public async Task LeMacchineNonGuardateVengonoSondateDaSole()
    {
        ObserverEndpoint locale = ObserverEndpoint.LocalChannel();
        ObserverEndpoint viva = Remota("viva");
        ObserverEndpoint spenta = Remota("spenta");

        List<ObserverEndpoint> aperte = [];

        MainViewModel viewModel = new(
            client: new ClientCheRisponde(locale),
            configurationProblem: null,
            machineList: new MachineListResult([locale, viva, spenta], []),
            openMachine: punto =>
            {
                aperte.Add(punto);

                return punto == viva ? new ClientCheRisponde(punto) : new ClientMuto(punto);
            });

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.RunAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && viewModel.Machines.Any(voce => voce.IsUnknown))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        // La macchina guardata segue il giro principale; le altre due la sonda.
        Assert.True(viewModel.Machines[0].IsReachable);
        Assert.True(viewModel.Machines[1].IsReachable);
        Assert.True(viewModel.Machines[2].IsWarning, viewModel.Machines[2].Detail);

        // La sonda NON apre un client verso la macchina che si sta gia' guardando.
        Assert.DoesNotContain(locale, aperte);
        Assert.Contains(viva, aperte);
        Assert.Contains(spenta, aperte);

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

    [Fact]
    public void CambiareStatoNotificaAncheIColoriELaDescrizione()
    {
        // Sono i nomi a cui sono legate le Classes dell'Ellipse: toglierne uno dall'attributo
        // lascerebbe il pallino grigio per sempre, e nessun test lo direbbe.
        MachineRow voce = new(Remota("altra"));
        List<string> notificate = [];
        voce.PropertyChanged += (_, e) => notificate.Add(e.PropertyName ?? string.Empty);

        voce.Record(ServiceOutcome.Ok, string.Empty, T0);

        Assert.Contains(nameof(MachineRow.IsUnknown), notificate);
        Assert.Contains(nameof(MachineRow.IsReachable), notificate);
        Assert.Contains(nameof(MachineRow.IsWarning), notificate);
        Assert.Contains(nameof(MachineRow.IsFaulted), notificate);
        Assert.Contains(nameof(MachineRow.AccessibleName), notificate);
    }

    [Fact]
    public async Task LaMacchinaGuardataNonVieneSondataAncheSeRemota()
    {
        // "Salta la selezionata" e "salta la locale" sono indistinguibili quando la guardata
        // e' la prima dell'elenco. Qui la guardata e' la seconda, e remota.
        ObserverEndpoint locale = ObserverEndpoint.LocalChannel();
        ObserverEndpoint viva = Remota("viva");
        ObserverEndpoint spenta = Remota("spenta");
        List<ObserverEndpoint> aperte = [];

        MainViewModel viewModel = new(
            client: new ClientCheRisponde(viva),
            configurationProblem: null,
            machineList: new MachineListResult([locale, viva, spenta], []),
            openMachine: punto =>
            {
                aperte.Add(punto);

                return punto == spenta ? new ClientMuto(punto) : new ClientCheRisponde(punto);
            });

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.RunAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && viewModel.Machines.Any(voce => voce.IsUnknown))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Same(viewModel.Machines[1], viewModel.SelectedMachine);
        Assert.DoesNotContain(viva, aperte);
        Assert.Contains(locale, aperte);
        Assert.Contains(spenta, aperte);

        await Ferma(arresto, ciclo);
    }

    [Fact]
    public async Task SenzaSelezioneLaMacchinaGuardataRestaFuoriDalleSonde()
    {
        // La lista non dovrebbe mai azzerare la selezione (AlwaysSelected), ma se succede il
        // giro principale continua a leggere la stessa macchina, e la sonda NON deve leggerla
        // una seconda volta: e' la voce guardata a contare, non la selezione.
        ObserverEndpoint locale = ObserverEndpoint.LocalChannel();
        ObserverEndpoint altra = Remota("altra");
        OrologioFinto clock = new();
        List<ObserverEndpoint> aperte = [];

        MainViewModel viewModel = new(
            client: new ClientCheRisponde(locale),
            configurationProblem: null,
            clock: clock.Adesso,
            machineList: new MachineListResult([locale, altra], []),
            openMachine: punto =>
            {
                aperte.Add(punto);

                return new ClientCheRisponde(punto);
            });

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.RunAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && viewModel.Machines[1].IsUnknown)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        viewModel.SelectedMachine = null;
        clock.Avanza(MainViewModel.StatusRefreshInterval + TimeSpan.FromSeconds(1));

        while (!arresto.IsCancellationRequested && aperte.Count(punto => punto == altra) < 2)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(aperte.Count(punto => punto == altra) >= 2, "la seconda sonda non e' partita");
        Assert.DoesNotContain(locale, aperte);
        Assert.True(viewModel.Machines[0].IsReachable);

        await Ferma(arresto, ciclo);
    }

    [Fact]
    public async Task UnaSondaSospesaNonFermaIlGiroENonNeFaPartireUnAltra()
    {
        // Le due promesse delle sonde, provate con un client che NON risponde finche' il test
        // non lo dice: un client che risponde subito le lascerebbe entrambe mutabili.
        ObserverEndpoint locale = ObserverEndpoint.LocalChannel();
        ObserverEndpoint lenta = Remota("lenta");
        OrologioFinto clock = new();
        ClientContatore guardato = new(locale);
        ClientSospeso sospeso = new(lenta);
        int aperture = 0;

        MainViewModel viewModel = new(
            client: guardato,
            configurationProblem: null,
            clock: clock.Adesso,
            machineList: new MachineListResult([locale, lenta], []),
            openMachine: _ =>
            {
                aperture++;

                return sospeso;
            });

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(20));
        Task ciclo = viewModel.RunAsync(arresto.Token);

        // La sonda e' partita e resta appesa; il giro principale intanto legge ancora.
        while (!arresto.IsCancellationRequested && guardato.Letture < 3)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(guardato.Letture >= 3, "il giro principale ha aspettato la sonda");
        Assert.Equal(1, aperture);
        Assert.True(viewModel.Machines[1].IsProbing);
        Assert.True(viewModel.Machines[1].IsUnknown);

        // Passano due cadenze: con la sonda ancora in volo non ne parte una seconda.
        clock.Avanza(MainViewModel.StatusRefreshInterval * 2);
        int prima = guardato.Letture;

        while (!arresto.IsCancellationRequested && guardato.Letture < prima + 2)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Equal(1, aperture);

        // Quando torna, il pallino cambia e la voce e' di nuovo sondabile.
        sospeso.Rispondi(new SnapshotFetch(ServiceOutcome.Unreachable, "spenta", null));

        while (!arresto.IsCancellationRequested && viewModel.Machines[1].IsUnknown)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[1].IsWarning, viewModel.Machines[1].Detail);
        Assert.False(viewModel.Machines[1].IsProbing);

        await Ferma(arresto, ciclo);
    }

    [Fact]
    public async Task UnaSondaCheLanciaDiventaUnPallinoRossoEIlGiroContinua()
    {
        ObserverEndpoint locale = ObserverEndpoint.LocalChannel();
        ObserverEndpoint rotta = Remota("rotta");
        ClientContatore guardato = new(locale);

        MainViewModel viewModel = new(
            client: guardato,
            configurationProblem: null,
            machineList: new MachineListResult([locale, rotta], []),
            openMachine: punto => new ClientCheLancia(punto));

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.RunAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && viewModel.Machines[1].IsUnknown)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[1].IsFaulted, viewModel.Machines[1].Detail);
        Assert.Equal("Reading failed", viewModel.Machines[1].Detail);
        Assert.False(viewModel.Machines[1].IsProbing);

        // E il giro principale e' vivo.
        int prima = guardato.Letture;

        while (!arresto.IsCancellationRequested && guardato.Letture <= prima)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(guardato.Letture > prima, "il giro principale si e' fermato");

        await Ferma(arresto, ciclo);
    }

    [Fact]
    public async Task RileggendoLaMacchinaLaDurataRiparteDaCapo()
    {
        // Stesso posto nell'elenco, macchina cambiata sotto: "giu' da mezz'ora" riferito alla
        // precedente sarebbe una bugia, ed e' una bugia che nessuno andrebbe a cercare.
        // Il percorso passa da rereadEndpoint -> ProbeAsync -> Update, che e' interno: si
        // prova da qui, dove e' raggiungibile, invece di allargare la superficie della classe.
        // Il client rifiuta ANCHE la credenziale nuova, altrimenti una lettura buona azzererebbe
        // tutto per un'altra strada e il test passerebbe anche senza l'azzeramento.
        ObserverEndpoint locale = ObserverEndpoint.LocalChannel();
        ObserverEndpoint vecchia = Remota("ruotata");
        ObserverEndpoint nuova = vecchia with { ApiToken = "nuovo" };
        OrologioFinto clock = new();
        bool ruota = false;

        MainViewModel viewModel = new(
            client: new ClientCheRisponde(locale),
            configurationProblem: null,
            clock: clock.Adesso,
            machineList: new MachineListResult([locale, vecchia], []),
            openMachine: punto => new ClientRifiutato(punto),
            rereadEndpoint: _ => ruota ? nuova : null);

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(20));
        Task ciclo = viewModel.RunAsync(arresto.Token);

        // Un token rifiutato e' rosso dal primo istante: niente tolleranza da aspettare.
        while (!arresto.IsCancellationRequested && !viewModel.Machines[1].IsFaulted)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        // Il guasto invecchia. L'orologio si sposta una volta sola: sono le sonde successive
        // a leggerlo, e la riga arriva a dire mezz'ora.
        clock.Avanza(TimeSpan.FromMinutes(30));

        while (!arresto.IsCancellationRequested
            && !viewModel.Machines[1].DowntimeText.Contains("30 min", StringComparison.Ordinal))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Equal("for 30 min", viewModel.Machines[1].DowntimeText);

        // Adesso la macchina cambia sotto: la sonda successiva la rilegge. L'orologio deve
        // avanzare, altrimenti la sonda non scatta piu' e non c'e' nessuna lettura successiva.
        ruota = true;

        while (!arresto.IsCancellationRequested && viewModel.Machines[1].Endpoint != nuova)
        {
            clock.Avanza(MainViewModel.StatusRefreshInterval + TimeSpan.FromSeconds(1));
            await Task.Delay(50, CancellationToken.None);
        }

        // La macchina e' ancora giu', ma e' un'ALTRA macchina: la misura ricomincia da zero e
        // il rifiuto successivo riparte da "under 1 min", invece di continuare la mezz'ora
        // della precedente. Senza l'azzeramento dentro Update la durata proseguirebbe.
        Assert.Equal(nuova, viewModel.Machines[1].Endpoint);
        Assert.True(viewModel.Machines[1].IsFaulted || viewModel.Machines[1].IsWarning);
        // Vuota se si guarda fra l'azzeramento e la lettura successiva, "under 1 min" se si
        // guarda dopo. Senza l'azzeramento sarebbe la mezz'ora di prima, che continua a
        // crescere: un'asserzione su un valore preciso non basterebbe a distinguerlo.
        string dopo = viewModel.Machines[1].DowntimeText;
        Assert.True(dopo.Length == 0 || dopo == "for under 1 min", $"durata dopo la rilettura: '{dopo}'");

        await Ferma(arresto, ciclo);
    }

    [Fact]
    public async Task DopoUnTokenRifiutatoLaSondaRileggeLaMacchina()
    {
        // "observer token set" a finestra aperta, su una macchina NON guardata: la sonda
        // successiva deve partire con la credenziale nuova, non con quella letta all'avvio.
        ObserverEndpoint locale = ObserverEndpoint.LocalChannel();
        ObserverEndpoint vecchia = Remota("ruotata");
        ObserverEndpoint nuova = vecchia with { ApiToken = "nuovo" };
        OrologioFinto clock = new();
        List<ObserverEndpoint> aperte = [];

        MainViewModel viewModel = new(
            client: new ClientCheRisponde(locale),
            configurationProblem: null,
            clock: clock.Adesso,
            machineList: new MachineListResult([locale, vecchia], []),
            openMachine: punto =>
            {
                aperte.Add(punto);

                return punto == nuova ? new ClientCheRisponde(punto) : new ClientRifiutato(punto);
            },
            rereadEndpoint: _ => nuova);

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.RunAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && !viewModel.Machines[1].IsFaulted)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Equal("Token rejected", viewModel.Machines[1].Detail);

        clock.Avanza(MainViewModel.StatusRefreshInterval + TimeSpan.FromSeconds(1));

        while (!arresto.IsCancellationRequested && !viewModel.Machines[1].IsReachable)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[1].IsReachable, viewModel.Machines[1].Detail);
        Assert.Contains(nuova, aperte);
        Assert.Equal(nuova, viewModel.Machines[1].Endpoint);

        await Ferma(arresto, ciclo);
    }

    [Fact]
    public async Task ScegliendoUnaMacchinaCheLaSondaSaGiaSpentaLaBarraNonRecitaConnecting()
    {
        // Barra e pallino hanno un orologio solo: la sonda sa da sedici secondi che la macchina
        // e' spenta, e cliccandoci sopra la barra deve aprire rossa, non "Connecting" per altri
        // dieci secondi mentre il pallino accanto e' gia' rosso.
        ObserverEndpoint locale = ObserverEndpoint.LocalChannel();
        ObserverEndpoint spenta = Remota("spenta");
        OrologioFinto clock = new();

        MainViewModel viewModel = new(
            client: new ClientCheRisponde(locale),
            configurationProblem: null,
            clock: clock.Adesso,
            machineList: new MachineListResult([locale, spenta], []),
            openMachine: punto => new ClientMuto(punto));

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(20));
        Task ciclo = viewModel.RunAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && !viewModel.Machines[1].IsWarning)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        clock.Avanza(MainViewModel.StatusRefreshInterval + TimeSpan.FromSeconds(1));

        while (!arresto.IsCancellationRequested && !viewModel.Machines[1].IsFaulted)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[1].IsFaulted, viewModel.Machines[1].Detail);

        viewModel.SelectedMachine = viewModel.Machines[1];

        while (!arresto.IsCancellationRequested && viewModel.StatusTitle == "Connecting")
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Equal(FAInfoBarSeverity.Error, viewModel.StatusSeverity);
        Assert.True(viewModel.Machines[1].IsFaulted);

        await Ferma(arresto, ciclo);
    }

    [Fact]
    public async Task IlPallinoDellaMacchinaGuardataSegueLaBarraAncheQuandoFallisce()
    {
        ObserverEndpoint locale = ObserverEndpoint.LocalChannel();
        OrologioFinto clock = new();

        MainViewModel viewModel = new(
            client: new ClientMuto(locale),
            configurationProblem: null,
            clock: clock.Adesso,
            machineList: new MachineListResult([locale], []));

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.RunAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && viewModel.Machines[0].IsUnknown)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[0].IsWarning, viewModel.Machines[0].Detail);
        Assert.Equal(FAInfoBarSeverity.Informational, viewModel.StatusSeverity);

        clock.Avanza(StatusEscalation.GracePeriod + TimeSpan.FromSeconds(1));

        while (!arresto.IsCancellationRequested && !viewModel.Machines[0].IsFaulted)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[0].IsFaulted, viewModel.Machines[0].Detail);
        Assert.Equal(FAInfoBarSeverity.Error, viewModel.StatusSeverity);

        await Ferma(arresto, ciclo);
    }

    private static async Task Ferma(CancellationTokenSource arresto, Task ciclo)
    {
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

    private sealed class ClientCheRisponde(ObserverEndpoint endpoint) : IMetricsClient
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
    private sealed class ClientContatore(ObserverEndpoint endpoint) : IMetricsClient
    {
        private int letture;

        public int Letture => Volatile.Read(ref letture);

        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref letture);

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
    private sealed class ClientSospeso(ObserverEndpoint endpoint) : IMetricsClient
    {
        private readonly TaskCompletionSource<SnapshotFetch> attesa = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ObserverEndpoint Endpoint { get; } = endpoint;

        public void Rispondi(SnapshotFetch esito) => attesa.TrySetResult(esito);

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) => attesa.Task;

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Unreachable, "lenta", null));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Unreachable, "lenta", null));
    }

    /// <summary>Lancia invece di rispondere: un client che non si costruisce, un DNS che esplode.</summary>
    private sealed class ClientCheLancia(ObserverEndpoint endpoint) : IMetricsClient
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
    private sealed class ClientRifiutato(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.TokenRejected, "rejected", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.TokenRejected, "rejected", null));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.TokenRejected, "rejected", null));
    }

    private sealed class ClientMuto(ObserverEndpoint endpoint) : IMetricsClient
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