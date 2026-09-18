using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>
/// What happens when the configuration is missing at startup and appears later.
/// </summary>
/// <remarks>
/// It is the NORMAL case, not an edge case: the "Configuration missing" message tells the user
/// to create a file. If creating that file has no effect until they restart — and the message
/// does not tell them so — the user follows the instructions to the letter and concludes that
/// the application is broken. That is what really happened.
/// </remarks>
public class MainViewModelReconnectTests
{
    [Fact]
    public async Task ConfigurationAppearsAfterStartup_TheApplicationConnectsWithoutARestart()
    {
        // At startup there is no configuration; at the first re-read a valid one appears.
        FakeMetricsClient client = new();
        int rereads = 0;

        MainViewModel viewModel = new(
            client: null,
            configurationProblem: "the token is missing",
            rereadConfiguration: () =>
            {
                rereads++;
                return client;
            });

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
        Task loop = viewModel.RunAsync(stop.Token);

        // Waits until the client that appeared is actually QUERIED, without depending on a
        // fixed delay: that is the proof the view model has adopted it.
        while (!stop.IsCancellationRequested && client.Queries == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(client.Queries >= 1, "the client that appeared was never queried");
        Assert.True(rereads >= 1);

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task TokenRejectedOnAnAlreadyConnectedWindow_TheConfigurationIsReread()
    {
        // The twin of the case above, and until now left uncovered: the re-read happened ONLY at
        // startup. A window already connected that gets a 401 — because the token was rotated —
        // stayed stuck on "Token rejected" until a restart, and no message said so. It is the
        // same incident as "Configuration missing", on a different path.
        FakeMetricsClient oldClient = new(
            ObserverEndpoint.Remote(new Uri("http://old-machine:5057/"), "t", "from the test"),
            ServiceOutcome.TokenRejected);
        FakeMetricsClient newClient = new(
            ObserverEndpoint.Remote(new Uri("http://new-machine:9999/"), "t", "from the test"),
            ServiceOutcome.Unreachable);

        MainViewModel viewModel = new(
            oldClient,
            configurationProblem: null,
            rereadConfiguration: () => newClient);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(stop.Token);

        while (!stop.IsCancellationRequested && newClient.Queries == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(newClient.Queries >= 1, "after a 401 the re-read client was never queried");

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task WithNoConfigurationAndNoReread_TheLoopExitsAtOnce()
    {
        // The previous behaviour still holds when there is no way to re-read: hammering the
        // service with requests bound for a 401 would help nobody.
        MainViewModel viewModel = new(client: null, configurationProblem: "the token is missing");

        await viewModel.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("Observer", viewModel.Heading);
    }

    private sealed class FakeMetricsClient(ObserverEndpoint endpoint, ServiceOutcome outcome) : IMetricsClient
    {
        public FakeMetricsClient()
            : this(ObserverEndpoint.LocalChannel(), ServiceOutcome.Unreachable)
        {
        }

        public ObserverEndpoint Endpoint { get; } = endpoint;

        private int queryCount;

        /// <summary>How many times this client has been queried.</summary>
        /// <remarks>
        /// It is the signal the tests use to recognise that the view model has ADOPTED this
        /// client. They used to look at the heading, which carried the machine's name; the
        /// heading is now always "Observer", and it was an indirect clue anyway: it said that a
        /// string had changed, not that the new client was really being used.
        /// </remarks>
        public int Queries => Volatile.Read(ref queryCount);

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref queryCount);

            return Task.FromResult(new SnapshotFetch(outcome, "no service in this test", null));
        }

        // The history does NOT increment the counter: that counter says whether the view model
        // has adopted this client for SAMPLING, and mixing a second call into it would make the
        // signal ambiguous in exactly the reconnection tests.
        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(outcome, "no service in this test", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(outcome, "no service in this test", null));

        /// <summary>True after Dispose: keeps the method non-static and documents the outcome.</summary>
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}