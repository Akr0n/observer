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
public class RefreshCostTests
{
    [Fact]
    public void WhenMinimizedTheWindowPollsLessOften()
    {
        MainViewModel viewModel = new(client: null, configurationProblem: null);

        Assert.Equal(MainViewModel.Interval, viewModel.PollInterval);

        viewModel.IsMinimized = true;

        Assert.Equal(MainViewModel.BackgroundInterval, viewModel.PollInterval);

        // Almeno cinque volte piu' rada, altrimenti non varrebbe la pena distinguerla; e non
        // infinita, perche' riaprendo la finestra la barra di stato deve dire subito com'e'.
        Assert.True(MainViewModel.BackgroundInterval >= MainViewModel.Interval * 5);
        Assert.True(MainViewModel.BackgroundInterval <= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TheHistoryOfTwoGaugesIsReadInParallel()
    {
        // Due quadranti, quattro richieste di storico. In fila il giro dura la SOMMA delle
        // latenze; in parallelo il massimo. Il banco misura quante richieste sono in volo
        // insieme: in fila non supera mai una.
        SlowClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(stop.Token);

        while (!stop.IsCancellationRequested && client.Completed < 4)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(
            client.PeakInFlight >= 2,
            $"al massimo {client.PeakInFlight} richieste di storico in volo insieme: sono partite in fila");

        // E ogni risposta deve tornare alla SUA riga: leggere in parallelo e poi abbinare per
        // posizione e' esattamente il punto in cui uno storico finirebbe sotto il quadrante
        // sbagliato. Qui la memoria risponde con un guasto e la CPU no.
        MetricRow cpu = viewModel.Gauges.Single(row => row.Key.StartsWith("cpu|", StringComparison.Ordinal));
        MetricRow memory = viewModel.Gauges.Single(row => row.Key.StartsWith("memory|", StringComparison.Ordinal));

        while (!stop.IsCancellationRequested && !memory.HistoryNote.Contains("guasto", StringComparison.Ordinal))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Contains("storico della memoria guasto", memory.HistoryNote, StringComparison.Ordinal);
        Assert.DoesNotContain("guasto", cpu.HistoryNote, StringComparison.Ordinal);

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

    /// <summary>Un client con due percentuali e uno storico che risponde con calma.</summary>
    private sealed class SlowClient : IMetricsClient
    {
        private int inFlight;
        private int peak;
        private int completedCount;

        public int PeakInFlight => peak;

        public int Completed => completedCount;

        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.LocalChannel();

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

        public async Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref inFlight);

            int seen;

            do
            {
                seen = peak;

                if (current <= seen)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref peak, current, seen) != seen);

            await Task.Delay(150, cancellationToken);

            Interlocked.Decrement(ref inFlight);
            Interlocked.Increment(ref completedCount);

            return string.Equals(query.Collector, "memory", StringComparison.Ordinal)
                ? new HistoryFetch(ServiceOutcome.Unreachable, "storico della memoria guasto", null)
                : new HistoryFetch(ServiceOutcome.Ok, string.Empty, []);
        }
    }
}