using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// That switching machine leaves nothing of the previous one on screen.
/// </summary>
/// <remarks>
/// It is the worst defect this window could have, and it really happened: the gauges and the
/// history strips went on showing the previous machine's readings under the new machine's name.
/// Not wrong numbers — <b>real</b> numbers, belonging to another machine. It was noticeable only
/// because half the window emptied and half did not: the text panels vanished, the gauges stayed.
/// <para>
/// The cause is structural and worth remembering: the gauges are a SECOND collection built on
/// the same rows as the panels. Whoever adds a third one has to clear it in the same place, and
/// this test is what will tell them so.
/// </para>
/// </remarks>
public class MachineSwitchTests
{
    [Fact]
    public async Task ChangingMachineClearsThePreviousReadings()
    {
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint other = ObserverEndpoint.Remote(
            new Uri("https://other:5058/"), "token", "other", new string('a', 64));

        MainViewModel viewModel = new(
            client: new ClientWithData(local),
            configurationProblem: null,
            machineList: new MachineListResult([local, other], []),

            // The second machine does not answer: this is exactly the case where the old numbers
            // would stay on screen, because nothing arrives to replace them.
            openMachine: endpoint => new SilentClient(endpoint));

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(stop.Token);

        while (!stop.IsCancellationRequested && viewModel.Gauges.Count == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.NotEmpty(viewModel.Gauges);
        Assert.True(viewModel.HasGauges);

        viewModel.SelectedMachine = viewModel.Machines.Single(entry => entry.Endpoint == other);

        // Straight away, without waiting for a round: at least a second goes by between the
        // choice and the new machine's first answer, and in that second there must be nothing
        // left to read.
        Assert.Empty(viewModel.Gauges);
        Assert.False(viewModel.HasGauges);
        Assert.Empty(viewModel.Groups);

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

    /// <summary>A client that answers with a single, good reading.</summary>
    private sealed class ClientWithData(ObserverEndpoint endpoint) : IMetricsClient
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
            HistoryQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));
    }

    /// <summary>A client that never answers, like a machine that is switched off.</summary>
    private sealed class SilentClient(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.Unreachable, "down", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Unreachable, "down", null));

        public Task<HistoryFetch> GetHistoryAsync(
            HistoryQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Unreachable, "down", null));
    }
}