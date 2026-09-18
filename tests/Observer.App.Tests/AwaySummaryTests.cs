using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;

namespace Observer.App.Tests;

/// <summary>
/// The line that says what happened while the window was closed.
/// </summary>
/// <remarks>
/// It is the honest answer this stack can give to "tell me if a machine goes down while I am not
/// looking": the real alert cannot be delivered without being able to fail silently, and this one
/// cannot fail silently because it promises nothing while nobody is looking.
/// </remarks>
public class AwaySummaryTests
{
    /// <remarks>
    /// LOCAL noon, not UTC: the line shows the clock of the machine you are watching from (like
    /// <c>HistoryStrip.Describe</c>), so a UTC instant would make the test depend on the time
    /// zone of whoever runs it - green here and red on the runner, or the other way round.
    /// </remarks>
    private static readonly DateTimeOffset Noon = new(new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Local));

    private static HistoryGap Gap(int startMinute, int endMinute, bool atEdge = false) =>
        new(Noon.AddMinutes(startMinute), Noon.AddMinutes(endMinute), atEdge);

    [Fact]
    public void NoGapsMeansNoLine()
    {
        // Silence is a result: "I asked and there was nothing". An "all good" line for every
        // healthy machine would fill the panel in exactly the case where it is not needed, and
        // people would learn to close it without reading it.
        Assert.Equal(string.Empty, AwaySummary.LineFor("work", [], withDay: false));
    }

    [Fact]
    public void ASingleOutageSaysHowLongAndWhen()
    {
        string line = AwaySummary.LineFor("work", [Gap(20, 200)], withDay: false);

        Assert.Equal("work: not measured for 3 h (12:20 – 15:20)", line);

        // With only one, "in 1 period" would be noise: the total IS that one.
        Assert.DoesNotContain("period", line, StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralOutagesSayHowManyAndWhichWasLongest()
    {
        // The total on its own would lie by omission: three hours in one go and three hours in
        // ten hiccups are two different machines, and the longest one is what decides whether
        // you get out of your chair.
        string line = AwaySummary.LineFor("work", [Gap(10, 20), Gap(60, 240), Gap(300, 310)], withDay: false);

        Assert.Contains("in 3 periods", line, StringComparison.Ordinal);
        Assert.Contains("longest 13:00 – 16:00", line, StringComparison.Ordinal);
        Assert.Contains("3 h 20 min", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGapAtTheEdgeIsNotCountedAsAnOutage()
    {
        // Retention deletes a PREFIX, indistinguishable from a machine switched on halfway
        // through the window: calling it an outage would be inventing, and one invented line
        // teaches you not to trust the rest.
        string edgeOnly = AwaySummary.LineFor("home", [Gap(0, 45, atEdge: true)], withDay: false);

        Assert.Equal("home: nothing known before 12:45", edgeOnly);
        Assert.DoesNotContain("not measured", edgeOnly, StringComparison.Ordinal);

        // And when there is a real outage too, the edge stays a note at the end and does NOT go
        // into the total: twenty minutes, not sixty-five.
        string edgeAndOutage = AwaySummary.LineFor("home", [Gap(0, 45, atEdge: true), Gap(60, 80)], withDay: false);

        Assert.Contains("not measured for 20 min", edgeAndOutage, StringComparison.Ordinal);
        Assert.Contains("nothing known before 12:45", edgeAndOutage, StringComparison.Ordinal);
        Assert.DoesNotContain("periods", edgeAndOutage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDayFlagAddsTheWeekdayToBothTimes()
    {
        // Same threshold and same reason as HistoryStrip.Describe: at seven days "14:20" can be
        // any one of seven afternoons.
        string plain = AwaySummary.LineFor("work", [Gap(20, 200)], withDay: false);
        string dated = AwaySummary.LineFor("work", [Gap(20, 200)], withDay: true);

        Assert.Matches(@"\(\d{2}:\d{2} – \d{2}:\d{2}\)", plain);
        Assert.Matches(@"\([A-Za-z]{3} \d{2}:\d{2} – [A-Za-z]{3} \d{2}:\d{2}\)", dated);
    }

    [Fact]
    public void AnOutageAcrossMidnightDoesNotReadBackwards()
    {
        // At twenty-four hours a gap can last almost the whole window, and its two ends then
        // fall at the same time of day on two different days: without the day the line would
        // say "not measured for 23 h 45 min (09:25 – 09:10)", a duration of almost a day next
        // to an interval that reads as a quarter of an hour backwards. It happens at one hour
        // too, on a machine that is down across midnight: that is why the rule looks at the
        // PAIR and not at the window's threshold.
        string line = AwaySummary.LineFor("work", [Gap(-755, -710)], withDay: false);

        Assert.Matches(@"\([A-Za-z]{3} \d{2}:\d{2} – [A-Za-z]{3} \d{2}:\d{2}\)", line);

        // And when the two ends are in the same day, the day does NOT appear: always adding it
        // would make the line longer where it is not needed.
        Assert.DoesNotMatch(@"[A-Za-z]{3} \d{2}:\d{2}", AwaySummary.LineFor("work", [Gap(10, 20)], withDay: false));
    }

    [Fact]
    public async Task AHistoryThatCannotBeReadSaysSoInsteadOfStayingSilent()
    {
        // This is the point where this route differs from an alert that never appears: when the
        // answer cannot be known, the line says so. Silence stays reserved for "I asked and all
        // is well", and that way silence means something.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        FailingHistoryClient client = new();

        MainViewModel viewModel = new(
            client,
            configurationProblem: null,
            machineList: new MachineListResult([local], []));

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(25));
        Task loop = viewModel.RunAsync(stop.Token);

        while (!stop.IsCancellationRequested && viewModel.AwaySummaryText.Length == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Contains("history could not be read", viewModel.AwaySummaryText, StringComparison.Ordinal);
        Assert.Contains("persistence is off", viewModel.AwaySummaryText, StringComparison.Ordinal);
        Assert.True(viewModel.ShowAwaySummary);

        // The summary asks FURTHER BACK than the window it examines, and that is the fix
        // everything else rests on: without that margin the grid, anchored to the last point,
        // overruns on the left and every healthy machine opens with "nothing known before".
        Assert.True(
            client.SummaryQueryCount(TimeSpan.FromHours(1), DateTimeOffset.UtcNow) > 0,
            "the summary did not ask further back than the window: the grid would overrun on the left");

        // Changing the period changes the question, so it starts again from scratch.
        int queriesAtOneHour = client.SummaryQueryCount(TimeSpan.FromHours(1), DateTimeOffset.UtcNow);

        viewModel.HistoryPeriod = "24h";

        Assert.Equal(string.Empty, viewModel.AwaySummaryText);
        Assert.False(viewModel.ShowAwaySummary);

        while (!stop.IsCancellationRequested
            && client.SummaryQueryCount(TimeSpan.FromHours(24), DateTimeOffset.UtcNow) == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(
            client.SummaryQueryCount(TimeSpan.FromHours(24), DateTimeOffset.UtcNow) > 0,
            "changing the period did not redo the summary over the new window");
        Assert.True(queriesAtOneHour > 0, "the first query was not the summary's");

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

    /// <summary>Samples perfectly well, and the history is not there.</summary>
    private sealed class FailingHistoryClient : IMetricsClient
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<HistoryQuery> queries = [];

        /// <summary>The SUMMARY's requests alone, told apart by the margin only it asks for.</summary>
        /// <remarks>
        /// Counting them all would not tell the summary's requests from the strip's: the strip
        /// queries the same series at every step, so a single counter goes up anyway and the
        /// test would stay green even if the summary never ran. The summary is the only one
        /// that looks FURTHER BACK than the window, and that is precisely the fix this test has
        /// to pin down.
        /// </remarks>
        public int SummaryQueryCount(TimeSpan window, DateTimeOffset now) =>
            queries.Count(q => q.From < now - window - TimeSpan.FromMinutes(1));

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
                            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(42d)),
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
                        new MetricDescriptor(CpuCollector.TotalUsageMetricId, "CPU usage", MetricUnit.Percent, IsPerInstance: false),
                    ]),
                ])));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken)
        {
            queries.Add(query);

            return Task.FromResult(new HistoryFetch(ServiceOutcome.Unreachable, "persistence is off", null));
        }
    }
}