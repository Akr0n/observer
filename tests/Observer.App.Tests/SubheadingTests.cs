using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// What the row under the title says.
/// </summary>
/// <remarks>
/// It says <b>where</b> and <b>when</b>: which machine is being watched and at what time the
/// last reading arrived. It does not say how the connection was made. Where the token comes
/// from is a configuration note — it is needed when something goes wrong and you have to know
/// which file to correct, and that is where it lives, in the fingerprint mismatch message —
/// but in normal running it is a sentence that gets re-read at every glance and never changes.
/// </remarks>
public class SubheadingTests
{
    [Fact]
    public async Task WatchingARemoteMachineTheSubheadingShowsOnlyTheTimeAndNeverTheToken()
    {
        ObserverEndpoint remote = ObserverEndpoint.Remote(
            new Uri("https://other:5058/"), "the-token", "from the machines file", new string('a', 64));

        MainViewModel viewModel = new(new AnsweringClient(remote), configurationProblem: null);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(stop.Token);

        while (!stop.IsCancellationRequested
            && !viewModel.Subheading.StartsWith("Last Reading:", StringComparison.Ordinal))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        // The time, and only that. The check is on a shape and not on a fixed string because
        // the time changes at every sample while the SHAPE does not — and a wrong shape, the
        // time zone or the milliseconds or the twelve-hour clock, is how this row breaks
        // without anyone noticing.
        Assert.Matches(@"^Last Reading: \d{2}:\d{2}:\d{2}$", viewModel.Subheading);
        Assert.DoesNotContain("token", viewModel.Subheading, StringComparison.OrdinalIgnoreCase);

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

    private sealed class AnsweringClient(ObserverEndpoint endpoint) : IMetricsClient
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

        public Task<HistoryFetch> GetHistoryAsync(
            HistoryQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));
    }
}