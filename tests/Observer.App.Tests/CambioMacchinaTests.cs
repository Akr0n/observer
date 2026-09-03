using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// Che cambiando macchina non resti a schermo niente della precedente.
/// </summary>
/// <remarks>
/// E' il difetto peggiore che questa finestra possa avere, ed e' successo davvero: i quadranti
/// e le strisce continuavano a mostrare le misure della macchina di prima sotto il nome di
/// quella nuova. Non numeri sbagliati — numeri <b>veri</b>, di un'altra macchina. Si vedeva
/// solo perche' meta' della finestra si svuotava e meta' no: i riquadri scritti sparivano, i
/// quadranti restavano.
/// <para>
/// La causa e' strutturale e va ricordata: i quadranti sono una SECONDA collezione costruita
/// sulle stesse righe dei riquadri. Chi ne aggiunge una terza deve azzerarla nello stesso
/// posto, e questo test e' cio' che glielo dira'.
/// </para>
/// </remarks>
public class CambioMacchinaTests
{
    [Fact]
    public async Task CambiandoMacchinaINumeriDellaPrecedenteSpariscono()
    {
        ObserverEndpoint locale = ObserverEndpoint.CanaleLocale();
        ObserverEndpoint altra = ObserverEndpoint.Remoto(
            new Uri("https://altra:5058/"), "token", "altra", new string('a', 64));

        MainViewModel viewModel = new(
            client: new ClientConDati(locale),
            problemaDiConfigurazione: null,
            elenco: new MachineListResult([locale, altra], []),

            // La seconda macchina non risponde: e' proprio il caso in cui i numeri vecchi
            // resterebbero a schermo, perche' non arriva niente che li sostituisca.
            apriMacchina: punto => new ClientMuto(punto));

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && viewModel.Quadranti.Count == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.NotEmpty(viewModel.Quadranti);
        Assert.True(viewModel.MostraQuadranti);

        viewModel.MacchinaSelezionata = viewModel.Macchine.Single(voce => voce.Punto == altra);

        // Subito, senza aspettare un giro: fra la scelta e la prima risposta della macchina
        // nuova passa almeno un secondo, e in quel secondo non deve esserci niente da leggere.
        Assert.Empty(viewModel.Quadranti);
        Assert.False(viewModel.MostraQuadranti);
        Assert.Empty(viewModel.Gruppi);

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

    /// <summary>Un client che risponde con una misura sola, buona.</summary>
    private sealed class ClientConDati(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(
                ServiceOutcome.Ok,
                string.Empty,
                new MachineSnapshot(
                    MachineSnapshot.CurrentSchemaVersion,
                    DateTimeOffset.UnixEpoch,
                    [
                        new MetricSnapshot("cpu", CollectorStatus.Ok, null,
                        [
                            MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(42d)),
                        ]),
                    ])));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(
                ServiceOutcome.Ok,
                string.Empty,
                new MetricCatalog(
                [
                    new CollectorCatalogEntry("cpu",
                    [
                        new MetricDescriptor(
                            "cpu.usage.total",
                            "CPU usage",
                            MetricUnit.Percent,
                            IsPerInstance: false),
                    ]),
                ])));

        public Task<HistoryFetch> GetHistoryAsync(
            HistoryQuery richiesta,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));
    }

    /// <summary>Un client che non risponde mai, come una macchina spenta.</summary>
    private sealed class ClientMuto(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.NonRaggiungibile, "spenta", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.NonRaggiungibile, "spenta", null));

        public Task<HistoryFetch> GetHistoryAsync(
            HistoryQuery richiesta,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.NonRaggiungibile, "spenta", null));
    }
}