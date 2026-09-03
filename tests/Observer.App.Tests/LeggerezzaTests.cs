using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// Meno lavoro quando nessuno guarda, e lo storico letto tutto insieme.
/// </summary>
/// <remarks>
/// Questa e' una finestra che misura la CPU: cio' che spende per aggiornarsi rientra nel
/// numero che mostra. Le due regole qui sono le uniche che riducono quel costo senza togliere
/// niente a chi guarda.
/// </remarks>
public class LeggerezzaTests
{
    [Fact]
    public void RidottaAIconaLaFinestraLeggeMenoSpesso()
    {
        MainViewModel viewModel = new(client: null, problemaDiConfigurazione: null);

        Assert.Equal(MainViewModel.Intervallo, viewModel.Cadenza);

        viewModel.InSecondoPiano = true;

        Assert.Equal(MainViewModel.IntervalloRidotto, viewModel.Cadenza);

        // Almeno cinque volte piu' rada, altrimenti non varrebbe la pena distinguerla; e non
        // infinita, perche' riaprendo la finestra la barra di stato deve dire subito com'e'.
        Assert.True(MainViewModel.IntervalloRidotto >= MainViewModel.Intervallo * 5);
        Assert.True(MainViewModel.IntervalloRidotto <= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task LoStoricoDiDueQuadrantiSiLeggeInParallelo()
    {
        // Due quadranti, quattro richieste di storico. In fila il giro dura la SOMMA delle
        // latenze; in parallelo il massimo. Il banco misura quante richieste sono in volo
        // insieme: in fila non supera mai una.
        ClientLento cliente = new();
        MainViewModel viewModel = new(cliente, problemaDiConfigurazione: null);

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && cliente.Completate < 4)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(
            cliente.MassimoInVolo >= 2,
            $"al massimo {cliente.MassimoInVolo} richieste di storico in volo insieme: sono partite in fila");

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

    /// <summary>Un client con due percentuali e uno storico che risponde con calma.</summary>
    private sealed class ClientLento : IMetricsClient
    {
        private int inVolo;
        private int massimo;
        private int completate;

        public int MassimoInVolo => massimo;

        public int Completate => completate;

        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.CanaleLocale();

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
                        new MetricSnapshot("memory", CollectorStatus.Ok, null,
                        [
                            MetricPoint.Measured("memory.used.percent", null, MetricValue.FromNumber(30d)),
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
                        new MetricDescriptor("cpu.usage.total", "CPU usage", MetricUnit.Percent, IsPerInstance: false),
                    ]),
                    new CollectorCatalogEntry("memory",
                    [
                        new MetricDescriptor("memory.used.percent", "Memory usage", MetricUnit.Percent, IsPerInstance: false),
                    ]),
                ])));

        public async Task<HistoryFetch> GetHistoryAsync(HistoryQuery richiesta, CancellationToken cancellationToken)
        {
            int adesso = Interlocked.Increment(ref inVolo);

            int letto;

            do
            {
                letto = massimo;

                if (adesso <= letto)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref massimo, adesso, letto) != letto);

            await Task.Delay(150, cancellationToken);

            Interlocked.Decrement(ref inVolo);
            Interlocked.Increment(ref completate);

            return new HistoryFetch(ServiceOutcome.Ok, string.Empty, []);
        }
    }
}