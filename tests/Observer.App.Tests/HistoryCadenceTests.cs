using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// How often the history is re-read, and why it is not "every step".
/// </summary>
/// <remarks>
/// While the strip showed one hour only, the step was a minute and any cadence error lasted
/// sixty seconds: invisible. With the long periods the same error lasts two hours, and becomes
/// the window's settled state. The rules here came out of real defects, every one of them
/// found with the suite green.
/// </remarks>
public class HistoryCadenceTests
{
    /// <summary>The selector's three periods, as keys.</summary>
    public static TheoryData<string> Periods() => [.. Preferences.AllowedPeriods];

    [Theory]
    [MemberData(nameof(Periods))]
    public void AFailedReadIsRetriedSoonAndNotAWholeStepLater(string key)
    {
        HistoryPeriodOption period = new(key);

        TimeSpan retryDelay = MainViewModel.HistoryReadDelay(period, succeeded: false);

        // The defect was exactly this: the read declared "went well" even when EVERY strip had
        // failed, so a timeout put the next one off by a whole step. At seven days that is two
        // hours of "No history" next to gauges refreshing once a second, over data the service
        // went back to serving a second later.
        Assert.True(
            retryDelay < period.Step,
            $"{key}: dopo un guasto si aspetta {retryDelay}, cioe' quanto un passo ({period.Step})");

        // And soon means soon, not "a bit less": half a minute is the ceiling.
        Assert.True(retryDelay <= TimeSpan.FromSeconds(30), $"{key}: si riprova dopo {retryDelay}");
    }

    /// <summary>Each period with the exact cadence it gets, in seconds.</summary>
    public static TheoryData<string, double> Cadences() => new()
    {
        { "1h", 60d },
        { "24h", 225d },
        { "7d", 1800d },
    };

    [Theory]
    [MemberData(nameof(Cadences))]
    public void TheBarInProgressHasTimeToGrow(string key, double seconds)
    {
        HistoryPeriodOption period = new(key);

        TimeSpan cadence = MainViewModel.HistoryReadDelay(period, succeeded: true);

        // The exact values and not only the rule, as with the zoom levels: a quarter of the
        // step with the floor at one minute gives {1 min, 3 min 45 s, 30 min}, and whoever
        // changes them has to see them.
        Assert.Equal(TimeSpan.FromSeconds(seconds), cadence);

        // Re-reading EXACTLY every step looks like the right cadence - reading more often adds
        // no bar - and it is not: the last bar is the interval in progress and is drawn as wide
        // as it has covered, so re-reading at the step would mean looking at a newborn bar
        // every time, always at the same fraction. At seven days the right-hand end of the
        // strip - the point the eye reads as "now" - would stay frozen at that width for the
        // whole session, and with a choice made at the start of an interval that is one pixel.
        // At one hour the step is already a minute and the floor wins: there the bar does not
        // grow, and that is a declared give-up (twelve requests every fifteen seconds to
        // animate thirteen pixels do not pay).
        Assert.True(
            cadence <= period.Step,
            $"{key}: si rilegge ogni {cadence}, cioe' MENO spesso del passo ({period.Step})");

        Assert.True(
            key == "1h" || cadence <= period.Step / 2,
            $"{key}: si rilegge ogni {cadence} su barre da {period.Step}: la barra in corso non cresce");

        // And not continuously either: this is a window that measures the machine it is
        // querying, and what it spends on refreshing itself lands in the number it shows.
        Assert.True(cadence >= TimeSpan.FromMinutes(1), $"{key}: si rilegge ogni {cadence}");
    }

    [Fact]
    public async Task AFailingHistoryDoesNotFreezeTheStripForAWholeStep()
    {
        // At seven days the step is two hours: if the deadline moved anyway after a fault, the
        // second read would not start for half an hour of clock. Here the clock advances by
        // twenty seconds and the second read must already be there.
        FakeClock clock = new();
        ClientWithoutHistory client = new();

        MainViewModel viewModel = new(client, configurationProblem: null, clock: clock.Now)
        {
            HistoryPeriod = "7d",
        };

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
        Task loop = viewModel.RunAsync(stop.Token);

        while (!stop.IsCancellationRequested && client.Reads == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        int afterFirstRound = client.Reads;

        Assert.True(afterFirstRound > 0, "la prima lettura di storico non e' mai partita");

        clock.Advance(TimeSpan.FromSeconds(20));

        while (!stop.IsCancellationRequested && client.Reads <= afterFirstRound)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(
            client.Reads > afterFirstRound,
            "venti secondi dopo un guasto nessuno ha riprovato: la striscia resta ferma un passo intero");

        await End(stop, loop);
    }

    [Fact]
    public async Task AfterSwitchingMachineTheStripDoesNotWaitForThePreviousDeadline()
    {
        // The history deadline is derived from the watched machine, like the gauges and the
        // catalog. Without resetting it, the new machine's rows are born with no strip AND no
        // note - neither bars nor the reason there are none - and stay that way until the
        // inherited deadline: half an hour at seven days, with the gauges above already live.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint other = ObserverEndpoint.Remote(
            new Uri("https://altra:5058/"), "token", "altra", new string('a', 64));

        FakeClock clock = new();
        ClientWithHistory secondClient = new(other);

        MainViewModel viewModel = new(
            client: new ClientWithHistory(local),
            configurationProblem: null,
            clock: clock.Now,
            machineList: new MachineListResult([local, other], []),
            openMachine: _ => secondClient)
        {
            HistoryPeriod = "7d",
        };

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
        Task loop = viewModel.RunAsync(stop.Token);

        while (!stop.IsCancellationRequested && !viewModel.Gauges.Any(row => row.ShowHistory))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Contains(viewModel.Gauges, row => row.ShowHistory);

        viewModel.SelectedMachine = viewModel.Machines.Single(entry => entry.Endpoint == other);

        Assert.Empty(viewModel.Gauges);

        // Without advancing the clock: the new machine's strip must come back within the
        // loop's seconds, not half an hour from now.
        while (!stop.IsCancellationRequested && !viewModel.Gauges.Any(row => row.ShowHistory))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Contains(viewModel.Gauges, row => row.ShowHistory);

        await End(stop, loop);
    }

    [Fact]
    public async Task WithNothingDrawnTheTitleFollowsTheSelectedPeriod()
    {
        // With the machine not answering, the history is not re-read at all: if the title
        // followed the selector, it would stay "Last 7 days" for ever above the one-minute
        // bars read before the fault, and a machine idle for an hour would read as idle for a
        // week.
        MainViewModel viewModel = new(new SilentClient(), configurationProblem: null);

        string titleAtStart = viewModel.HistoryTitle;

        viewModel.HistoryPeriod = "7d";

        // Nothing is drawn, so the title does follow the selector: there is no strip to
        // contradict, and at startup with "7d" in the file saying "Last hour" would just be wrong.
        Assert.Equal(new HistoryPeriodOption("7d").Title, viewModel.HistoryTitle);
        Assert.NotEqual(titleAtStart, viewModel.HistoryTitle);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(stop.Token);

        await Task.Delay(1500, CancellationToken.None);

        // No read succeeded, so there is nothing to rename.
        Assert.Empty(viewModel.Gauges);
        Assert.Equal(new HistoryPeriodOption("7d").Title, viewModel.HistoryTitle);

        await End(stop, loop);
    }

    private static async Task End(CancellationTokenSource stop, Task loop)
    {
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

    /// <summary>Samples perfectly well and has no history: the fault the cadence must see.</summary>
    private sealed class ClientWithoutHistory : IMetricsClient
    {
        private int readCount;

        public int Reads => Volatile.Read(ref readCount);

        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.LocalChannel();

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Bench.Snapshot());

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Bench.Catalog());

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref readCount);

            return Task.FromResult(new HistoryFetch(ServiceOutcome.Unreachable, "persistenza spenta", null));
        }
    }

    /// <summary>Answers everything, history included.</summary>
    private sealed class ClientWithHistory(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Bench.Snapshot());

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Bench.Catalog());

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));
    }

    /// <summary>Never answers: the machine that is down.</summary>
    private sealed class SilentClient : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.LocalChannel();

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.Unreachable, "spenta", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Unreachable, "spenta", null));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Unreachable, "spenta", null));
    }

    /// <summary>One CPU, which is all it takes to get a gauge.</summary>
    private static class Bench
    {
        public static SnapshotFetch Snapshot() =>
            new(ServiceOutcome.Ok,
                string.Empty,
                new MachineSnapshot(
                    MachineSnapshot.CurrentSchemaVersion,
                    DateTimeOffset.UnixEpoch,
                    [
                        new MetricSnapshot("cpu", CollectorStatus.Ok, null,
                        [
                            MetricPoint.Measured("cpu.usage.total", null, MetricValue.FromNumber(42d)),
                        ]),
                    ]));

        public static CatalogFetch Catalog() =>
            new(ServiceOutcome.Ok,
                string.Empty,
                new MetricCatalog(
                [
                    new CollectorCatalogEntry("cpu",
                    [
                        new MetricDescriptor("cpu.usage.total", "CPU usage", MetricUnit.Percent, IsPerInstance: false),
                    ]),
                ]));
    }
}