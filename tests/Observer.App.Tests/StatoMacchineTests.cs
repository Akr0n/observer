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
        ObserverEndpoint.Remoto(new Uri($"https://{nome}:5058/"), "token", nome, new string('a', 64));

    [Fact]
    public void AllInizioNessunaMacchinaHaUnoStato()
    {
        MacchinaInElenco voce = new(Remota("altra"));

        Assert.True(voce.Ignoto);
        Assert.False(voce.Raggiungibile);
        Assert.Equal("Not checked yet", voce.Dettaglio);
    }

    [Fact]
    public void UnaRispostaBuonaSegnaRaggiungibile()
    {
        MacchinaInElenco voce = new(Remota("altra"));

        voce.Registra(ServiceOutcome.Ok, string.Empty, T0);

        Assert.True(voce.Raggiungibile);
        Assert.Equal("Reachable", voce.Dettaglio);
        Assert.Contains("Reachable", voce.Descrizione, StringComparison.Ordinal);
    }

    [Fact]
    public void UnRifiutoRecenteEAttenzioneEDopoLaGraziaEGuasto()
    {
        // La stessa regola della barra di stato: un servizio che sta ancora partendo rifiuta,
        // e per dieci secondi e' normale. Dopo, no.
        MacchinaInElenco voce = new(Remota("altra"));

        voce.Registra(ServiceOutcome.ConnessioneRifiutata, "refused", T0);
        Assert.True(voce.Attenzione);

        voce.Registra(ServiceOutcome.ConnessioneRifiutata, "refused", T0 + StatusEscalation.Tolleranza + TimeSpan.FromSeconds(1));
        Assert.True(voce.Guasto);

        // E una risposta buona azzera la serie: il guasto successivo ricomincia da capo.
        voce.Registra(ServiceOutcome.Ok, string.Empty, T0 + TimeSpan.FromMinutes(1));
        voce.Registra(ServiceOutcome.ConnessioneRifiutata, "refused", T0 + TimeSpan.FromMinutes(1));
        Assert.True(voce.Attenzione);
    }

    [Fact]
    public void UnTokenRifiutatoEGuastoDaSubito()
    {
        // Fra un minuto sara' identico: non c'e' grazia che tenga.
        MacchinaInElenco voce = new(Remota("altra"));

        voce.Registra(ServiceOutcome.TokenRifiutato, "rejected", T0);

        Assert.True(voce.Guasto);
    }

    [Fact]
    public async Task LeMacchineNonGuardateVengonoSondateDaSole()
    {
        ObserverEndpoint locale = ObserverEndpoint.CanaleLocale();
        ObserverEndpoint viva = Remota("viva");
        ObserverEndpoint spenta = Remota("spenta");

        List<ObserverEndpoint> aperte = [];

        MainViewModel viewModel = new(
            client: new ClientCheRisponde(locale),
            problemaDiConfigurazione: null,
            elenco: new MachineListResult([locale, viva, spenta], []),
            apriMacchina: punto =>
            {
                aperte.Add(punto);

                return punto == viva ? new ClientCheRisponde(punto) : new ClientMuto(punto);
            });

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && viewModel.Macchine.Any(voce => voce.Ignoto))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        // La macchina guardata segue il giro principale; le altre due la sonda.
        Assert.True(viewModel.Macchine[0].Raggiungibile);
        Assert.True(viewModel.Macchine[1].Raggiungibile);
        Assert.True(viewModel.Macchine[2].Attenzione, viewModel.Macchine[2].Dettaglio);

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
        MacchinaInElenco voce = new(Remota("altra"));
        List<string> notificate = [];
        voce.PropertyChanged += (_, e) => notificate.Add(e.PropertyName ?? string.Empty);

        voce.Registra(ServiceOutcome.Ok, string.Empty, T0);

        Assert.Contains(nameof(MacchinaInElenco.Ignoto), notificate);
        Assert.Contains(nameof(MacchinaInElenco.Raggiungibile), notificate);
        Assert.Contains(nameof(MacchinaInElenco.Attenzione), notificate);
        Assert.Contains(nameof(MacchinaInElenco.Guasto), notificate);
        Assert.Contains(nameof(MacchinaInElenco.Descrizione), notificate);
    }

    [Fact]
    public async Task LaMacchinaGuardataNonVieneSondataAncheSeRemota()
    {
        // "Salta la selezionata" e "salta la locale" sono indistinguibili quando la guardata
        // e' la prima dell'elenco. Qui la guardata e' la seconda, e remota.
        ObserverEndpoint locale = ObserverEndpoint.CanaleLocale();
        ObserverEndpoint viva = Remota("viva");
        ObserverEndpoint spenta = Remota("spenta");
        List<ObserverEndpoint> aperte = [];

        MainViewModel viewModel = new(
            client: new ClientCheRisponde(viva),
            problemaDiConfigurazione: null,
            elenco: new MachineListResult([locale, viva, spenta], []),
            apriMacchina: punto =>
            {
                aperte.Add(punto);

                return punto == spenta ? new ClientMuto(punto) : new ClientCheRisponde(punto);
            });

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && viewModel.Macchine.Any(voce => voce.Ignoto))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Same(viewModel.Macchine[1], viewModel.MacchinaSelezionata);
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
        ObserverEndpoint locale = ObserverEndpoint.CanaleLocale();
        ObserverEndpoint altra = Remota("altra");
        OrologioFinto orologio = new();
        List<ObserverEndpoint> aperte = [];

        MainViewModel viewModel = new(
            client: new ClientCheRisponde(locale),
            problemaDiConfigurazione: null,
            orologio: orologio.Adesso,
            elenco: new MachineListResult([locale, altra], []),
            apriMacchina: punto =>
            {
                aperte.Add(punto);

                return new ClientCheRisponde(punto);
            });

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && viewModel.Macchine[1].Ignoto)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        viewModel.MacchinaSelezionata = null;
        orologio.Avanza(MainViewModel.RicaricaStati + TimeSpan.FromSeconds(1));

        while (!arresto.IsCancellationRequested && aperte.Count(punto => punto == altra) < 2)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(aperte.Count(punto => punto == altra) >= 2, "la seconda sonda non e' partita");
        Assert.DoesNotContain(locale, aperte);
        Assert.True(viewModel.Macchine[0].Raggiungibile);

        await Ferma(arresto, ciclo);
    }

    [Fact]
    public async Task UnaSondaSospesaNonFermaIlGiroENonNeFaPartireUnAltra()
    {
        // Le due promesse delle sonde, provate con un client che NON risponde finche' il test
        // non lo dice: un client che risponde subito le lascerebbe entrambe mutabili.
        ObserverEndpoint locale = ObserverEndpoint.CanaleLocale();
        ObserverEndpoint lenta = Remota("lenta");
        OrologioFinto orologio = new();
        ClientContatore guardato = new(locale);
        ClientSospeso sospeso = new(lenta);
        int aperture = 0;

        MainViewModel viewModel = new(
            client: guardato,
            problemaDiConfigurazione: null,
            orologio: orologio.Adesso,
            elenco: new MachineListResult([locale, lenta], []),
            apriMacchina: _ =>
            {
                aperture++;

                return sospeso;
            });

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(20));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        // La sonda e' partita e resta appesa; il giro principale intanto legge ancora.
        while (!arresto.IsCancellationRequested && guardato.Letture < 3)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(guardato.Letture >= 3, "il giro principale ha aspettato la sonda");
        Assert.Equal(1, aperture);
        Assert.True(viewModel.Macchine[1].InSonda);
        Assert.True(viewModel.Macchine[1].Ignoto);

        // Passano due cadenze: con la sonda ancora in volo non ne parte una seconda.
        orologio.Avanza(MainViewModel.RicaricaStati * 2);
        int prima = guardato.Letture;

        while (!arresto.IsCancellationRequested && guardato.Letture < prima + 2)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Equal(1, aperture);

        // Quando torna, il pallino cambia e la voce e' di nuovo sondabile.
        sospeso.Rispondi(new SnapshotFetch(ServiceOutcome.NonRaggiungibile, "spenta", null));

        while (!arresto.IsCancellationRequested && viewModel.Macchine[1].Ignoto)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Macchine[1].Attenzione, viewModel.Macchine[1].Dettaglio);
        Assert.False(viewModel.Macchine[1].InSonda);

        await Ferma(arresto, ciclo);
    }

    [Fact]
    public async Task UnaSondaCheLanciaDiventaUnPallinoRossoEIlGiroContinua()
    {
        ObserverEndpoint locale = ObserverEndpoint.CanaleLocale();
        ObserverEndpoint rotta = Remota("rotta");
        ClientContatore guardato = new(locale);

        MainViewModel viewModel = new(
            client: guardato,
            problemaDiConfigurazione: null,
            elenco: new MachineListResult([locale, rotta], []),
            apriMacchina: punto => new ClientCheLancia(punto));

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && viewModel.Macchine[1].Ignoto)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Macchine[1].Guasto, viewModel.Macchine[1].Dettaglio);
        Assert.Equal("Reading failed", viewModel.Macchine[1].Dettaglio);
        Assert.False(viewModel.Macchine[1].InSonda);

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
    public async Task DopoUnTokenRifiutatoLaSondaRileggeLaMacchina()
    {
        // "observer token set" a finestra aperta, su una macchina NON guardata: la sonda
        // successiva deve partire con la credenziale nuova, non con quella letta all'avvio.
        ObserverEndpoint locale = ObserverEndpoint.CanaleLocale();
        ObserverEndpoint vecchia = Remota("ruotata");
        ObserverEndpoint nuova = vecchia with { ApiToken = "nuovo" };
        OrologioFinto orologio = new();
        List<ObserverEndpoint> aperte = [];

        MainViewModel viewModel = new(
            client: new ClientCheRisponde(locale),
            problemaDiConfigurazione: null,
            orologio: orologio.Adesso,
            elenco: new MachineListResult([locale, vecchia], []),
            apriMacchina: punto =>
            {
                aperte.Add(punto);

                return punto == nuova ? new ClientCheRisponde(punto) : new ClientRifiutato(punto);
            },
            rileggiPunto: _ => nuova);

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && !viewModel.Macchine[1].Guasto)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Equal("Token rejected", viewModel.Macchine[1].Dettaglio);

        orologio.Avanza(MainViewModel.RicaricaStati + TimeSpan.FromSeconds(1));

        while (!arresto.IsCancellationRequested && !viewModel.Macchine[1].Raggiungibile)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Macchine[1].Raggiungibile, viewModel.Macchine[1].Dettaglio);
        Assert.Contains(nuova, aperte);
        Assert.Equal(nuova, viewModel.Macchine[1].Punto);

        await Ferma(arresto, ciclo);
    }

    [Fact]
    public async Task ScegliendoUnaMacchinaCheLaSondaSaGiaSpentaLaBarraNonRecitaConnecting()
    {
        // Barra e pallino hanno un orologio solo: la sonda sa da sedici secondi che la macchina
        // e' spenta, e cliccandoci sopra la barra deve aprire rossa, non "Connecting" per altri
        // dieci secondi mentre il pallino accanto e' gia' rosso.
        ObserverEndpoint locale = ObserverEndpoint.CanaleLocale();
        ObserverEndpoint spenta = Remota("spenta");
        OrologioFinto orologio = new();

        MainViewModel viewModel = new(
            client: new ClientCheRisponde(locale),
            problemaDiConfigurazione: null,
            orologio: orologio.Adesso,
            elenco: new MachineListResult([locale, spenta], []),
            apriMacchina: punto => new ClientMuto(punto));

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(20));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && !viewModel.Macchine[1].Attenzione)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        orologio.Avanza(MainViewModel.RicaricaStati + TimeSpan.FromSeconds(1));

        while (!arresto.IsCancellationRequested && !viewModel.Macchine[1].Guasto)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Macchine[1].Guasto, viewModel.Macchine[1].Dettaglio);

        viewModel.MacchinaSelezionata = viewModel.Macchine[1];

        while (!arresto.IsCancellationRequested && viewModel.StatoTitolo == "Connecting")
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Equal(FAInfoBarSeverity.Error, viewModel.StatoGravita);
        Assert.True(viewModel.Macchine[1].Guasto);

        await Ferma(arresto, ciclo);
    }

    [Fact]
    public async Task IlPallinoDellaMacchinaGuardataSegueLaBarraAncheQuandoFallisce()
    {
        ObserverEndpoint locale = ObserverEndpoint.CanaleLocale();
        OrologioFinto orologio = new();

        MainViewModel viewModel = new(
            client: new ClientMuto(locale),
            problemaDiConfigurazione: null,
            orologio: orologio.Adesso,
            elenco: new MachineListResult([locale], []));

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && viewModel.Macchine[0].Ignoto)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Macchine[0].Attenzione, viewModel.Macchine[0].Dettaglio);
        Assert.Equal(FAInfoBarSeverity.Informational, viewModel.StatoGravita);

        orologio.Avanza(StatusEscalation.Tolleranza + TimeSpan.FromSeconds(1));

        while (!arresto.IsCancellationRequested && !viewModel.Macchine[0].Guasto)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Macchine[0].Guasto, viewModel.Macchine[0].Dettaglio);
        Assert.Equal(FAInfoBarSeverity.Error, viewModel.StatoGravita);

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

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery richiesta, CancellationToken cancellationToken) =>
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

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery richiesta, CancellationToken cancellationToken) =>
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
            Task.FromResult(new CatalogFetch(ServiceOutcome.NonRaggiungibile, "lenta", null));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery richiesta, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.NonRaggiungibile, "lenta", null));
    }

    /// <summary>Lancia invece di rispondere: un client che non si costruisce, un DNS che esplode.</summary>
    private sealed class ClientCheLancia(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery richiesta, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    /// <summary>Un servizio che rifiuta il token.</summary>
    private sealed class ClientRifiutato(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.TokenRifiutato, "rejected", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.TokenRifiutato, "rejected", null));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery richiesta, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.TokenRifiutato, "rejected", null));
    }

    private sealed class ClientMuto(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.NonRaggiungibile, "spenta", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.NonRaggiungibile, "spenta", null));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery richiesta, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.NonRaggiungibile, "spenta", null));
    }
}