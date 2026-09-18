using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// Less work when nobody is looking, and the history read all at once.
/// </summary>
/// <remarks>
/// This is a window that measures the CPU: what it spends on refreshing itself goes into the
/// number it shows. The two rules here are the only ones that cut that cost without taking
/// anything away from whoever is watching.
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

        // At least five times sparser, or telling it apart would not be worth it; and not
        // infinite, because on reopening the window the status bar has to say at once how
        // things stand.
        Assert.True(MainViewModel.BackgroundInterval >= MainViewModel.Interval * 5);
        Assert.True(MainViewModel.BackgroundInterval <= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TheHistoryOfTwoGaugesIsReadInParallel()
    {
        // Two gauges, four history requests. One after another the round lasts the SUM of the
        // latencies; in parallel, the largest of them. The bench measures how many requests are
        // in flight at the same time: one after another it never goes above one.
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
            $"at most {client.PeakInFlight} history requests in flight at once: they went out one after another");

        // And every response has to go back to ITS OWN row: reading in parallel and then
        // matching by position is exactly the point where a history would end up under the
        // wrong gauge. Here memory answers with a fault and the CPU does not.
        MetricRow cpu = viewModel.Gauges.Single(row => row.Key.StartsWith("cpu|", StringComparison.Ordinal));
        MetricRow memory = viewModel.Gauges.Single(row => row.Key.StartsWith("memory|", StringComparison.Ordinal));

        while (!stop.IsCancellationRequested && !memory.HistoryNote.Contains("broken", StringComparison.Ordinal))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Contains("memory history is broken", memory.HistoryNote, StringComparison.Ordinal);
        Assert.DoesNotContain("broken", cpu.HistoryNote, StringComparison.Ordinal);

        await stop.CancelAsync();

        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
            // End of the test.
        }
    }

    /// <summary>A client with two percentages and a history that takes its time to answer.</summary>
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
                ? new HistoryFetch(ServiceOutcome.Unreachable, "memory history is broken", null)
                : new HistoryFetch(ServiceOutcome.Ok, string.Empty, []);
        }
    }
}