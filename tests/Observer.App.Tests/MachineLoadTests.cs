using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Memory;

namespace Observer.App.Tests;

/// <summary>
/// The two numbers next to the name of a machine you are not watching.
/// </summary>
/// <remarks>
/// The sidebar answers one question only - "should I switch machine?" - and the two numbers
/// exist for that. The rules here are mostly about what happens when the answer is not known,
/// which is the case where an invented zero does more damage than a blank.
/// </remarks>
public class MachineLoadTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 12, 9, 0, 0, TimeSpan.Zero);

    private static MachineSnapshot Snapshot(params MetricSnapshot[] collectors) =>
        new(MachineSnapshot.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, collectors);

    private static MetricSnapshot Group(string id, params MetricPoint[] points) =>
        new(id, CollectorStatus.Ok, null, points);

    private static MachineRow RemoteRow() => new(ObserverEndpoint.Remote(
        new Uri("https://altra:5058/"), "token", "machines.json", new string('a', 64), "altra"));

    [Fact]
    public void BothNumbersComeFromTheSnapshotTheProbeAlreadyHasAndTheCaptionRoundsThem()
    {
        MachineLoad load = MachineLoad.From(Snapshot(
            Group("cpu", MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(42.7d))),
            Group("memory", MetricPoint.Measured(MemoryCollector.UsedPercentMetricId, null, MetricValue.FromNumber(61.2d)))));

        Assert.Equal(42.7d, load.Cpu);
        Assert.Equal(61.2d, load.Memory);

        // Whole numbers: the question is "which machine is struggling", and a tenth of a point
        // adds nothing to it while catching the eye at every reading.
        Assert.Equal("CPU 43% · RAM 61%", load.Caption);
    }

    [Fact]
    public void APerCorePointIsNeverTakenForTheWholeMachine()
    {
        // The per-core points come through the same interface, with the SAME identifier and an
        // instance set. Taking whichever point comes first would pass the load of a single core
        // off as the machine's - and it would look plausible, so it would go unnoticed.
        // In BOTH orders, because the order of the points is not declared anywhere: with the
        // cores only at the end, code that takes the first point would pass.
        MachineLoad machinePointLast = MachineLoad.From(Snapshot(Group(
            "cpu",
            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, "0", MetricValue.FromNumber(99d)),
            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, "1", MetricValue.FromNumber(97d)),
            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(12d)))));

        Assert.Equal(12d, machinePointLast.Cpu);
        Assert.Equal("CPU 12%", machinePointLast.Caption);

        MachineLoad machinePointFirst = MachineLoad.From(Snapshot(Group(
            "cpu",
            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(12d)),
            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, "0", MetricValue.FromNumber(99d)))));

        Assert.Equal(12d, machinePointFirst.Cpu);
    }

    [Fact]
    public void AMissingMetricDoesNotHideTheOther()
    {
        // On a platform where the CPU cannot be read the memory still can be, and half an
        // answer is better than none.
        MachineLoad memoryOnly = MachineLoad.From(Snapshot(
            Group("cpu", MetricPoint.Unsupported(CpuCollector.TotalUsageMetricId, null, "non misurabile qui")),
            Group("memory", MetricPoint.Measured(MemoryCollector.UsedPercentMetricId, null, MetricValue.FromNumber(61d)))));

        Assert.Null(memoryOnly.Cpu);
        Assert.Equal("RAM 61%", memoryOnly.Caption);

        MachineLoad cpuOnly = MachineLoad.From(Snapshot(
            Group("cpu", MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(8d)))));

        Assert.Null(cpuOnly.Memory);
        Assert.Equal("CPU 8%", cpuOnly.Caption);
    }

    [Fact]
    public void WithNoSnapshotItDoesNotInventAZero()
    {
        // A zero next to a machine's name reads as "idle", which is the opposite of "not
        // known". The difference matters precisely on the machines that do not answer.
        Assert.Equal(MachineLoad.None, MachineLoad.From(null));
        Assert.Equal(string.Empty, MachineLoad.None.Caption);
        Assert.Null(MachineLoad.None.Cpu);
        Assert.Equal(string.Empty, MachineLoad.From(Snapshot()).Caption);
    }

    [Fact]
    public void AValueThatIsNotANumberDoesNotBecomeZero()
    {
        MachineLoad load = MachineLoad.From(Snapshot(Group(
            "cpu",
            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromText("parecchio")))));

        Assert.Null(load.Cpu);
        Assert.Equal(string.Empty, load.Caption);
    }

    [Fact]
    public void AMachineThatGoesDownLosesTheLoadItHad()
    {
        // The load is cleared inside Record and not in the callers: there are three of them and
        // one would forget, leaving under the name of a machine that is down the numbers from
        // when it was answering - real numbers, for a moment that is gone.
        MachineRow row = RemoteRow();

        row.Record(
            ServiceOutcome.Ok,
            string.Empty,
            T0,
            Snapshot(Group("cpu", MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(50d)))));

        Assert.Equal("CPU 50%", row.Subtitle);

        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0 + TimeSpan.FromMinutes(1));

        // The load goes AT ONCE, while the duration is not there yet: within the ten seconds of
        // grace StatusEscalation says nothing, on purpose. So the row stays empty for a moment,
        // and that is the reason the space under the name is always reserved instead of
        // appearing and disappearing - the blank must not make the row jump.
        Assert.Equal(MachineLoad.None, row.MachineLoad);
        Assert.Equal(string.Empty, row.Subtitle);

        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0 + TimeSpan.FromMinutes(4));

        Assert.Equal(MachineLoad.None, row.MachineLoad);
        Assert.Equal("for 3 min", row.Subtitle);
    }

    [Fact]
    public void TheFaultDurationTakesPrecedenceOverTheLoad()
    {
        // The precedence can only be tested if the two COEXIST, and by construction they never
        // do: Record clears the load on every outcome that is not Ok. So they are made to
        // coexist from the outside, which is the only way to test the rule itself instead of
        // the branch that today makes it unreachable - and to notice if one day it stopped
        // being unreachable.
        MachineRow row = RemoteRow();

        row.Record(ServiceOutcome.TokenRejected, "rejected", T0);
        row.Record(ServiceOutcome.TokenRejected, "rejected", T0 + TimeSpan.FromMinutes(2));

        Assert.Equal(MachineLoad.None, row.MachineLoad);
        Assert.Equal("for 2 min", row.Subtitle);

        row.MachineLoad = new MachineLoad(80d, 90d);

        Assert.Equal("for 2 min", row.Subtitle);
        Assert.Contains("for 2 min", row.ToolTipText, StringComparison.Ordinal);
        Assert.DoesNotContain("CPU", row.ToolTipText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWatchedMachineShowsNoNumbersInTheSidebar()
    {
        // This is the decision that holds all the rest together: the numbers for the watched
        // machine are in the gauges, and repeating them next to the name would mean two
        // readings of the same machine at different cadences - fifteen seconds against one -
        // able to contradict each other in plain sight. The watched row is also the only one
        // that is always selected, that is, the only one a screen reader re-announces: with
        // the numbers its accessible name would change every second, for ever.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint other = ObserverEndpoint.Remote(
            new Uri("https://altra:5058/"), "token", "machines.json", new string('a', 64), "altra");

        MainViewModel viewModel = new(
            client: null,
            configurationProblem: null,
            machineList: new MachineListResult([local, other], []));

        MachineRow row = viewModel.Machines.Single(v => v.Endpoint == other);

        // The probe wrote the load into it while it was NOT being watched: the normal case.
        row.Record(
            ServiceOutcome.Ok,
            string.Empty,
            T0,
            Snapshot(
                Group("cpu", MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(42d))),
                Group("memory", MetricPoint.Measured(MemoryCollector.UsedPercentMetricId, null, MetricValue.FromNumber(61d)))));

        Assert.Equal("CPU 42% · RAM 61%", row.Subtitle);

        // The click. AT ONCE, without waiting for a round: up to eight seconds of timeout pass
        // between the selection and the first answer, and in that time the highlighted row
        // would say the machine is working while the status bar says "Connecting".
        viewModel.SelectedMachine = row;

        Assert.Equal(MachineLoad.None, row.MachineLoad);
        Assert.Equal(string.Empty, row.Subtitle);
        Assert.DoesNotContain("CPU", row.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLoadIsAnnouncedEvenWithoutSeeingTheRow()
    {
        // The tooltip and the accessible name both come from the row's one text, so they
        // cannot diverge. The middle dot separates two facts placed side by side.
        MachineRow row = RemoteRow();

        // A first successful reading WITHOUT a load, so that Status and Detail are already at
        // their final value: from here on the only thing that changes is the load, and the
        // notifications observed can only come from it. Without this step the three assertions
        // would be satisfied by Detail, which notifies ToolTipText and AccessibleName by itself.
        row.Record(ServiceOutcome.Ok, string.Empty, T0);

        List<string> notified = [];
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? string.Empty);

        row.Record(
            ServiceOutcome.Ok,
            string.Empty,
            T0 + TimeSpan.FromSeconds(15),
            Snapshot(
                Group("cpu", MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(42d))),
                Group("memory", MetricPoint.Measured(MemoryCollector.UsedPercentMetricId, null, MetricValue.FromNumber(61d)))));

        Assert.Equal("Reachable · CPU 42% · RAM 61%", row.ToolTipText);
        Assert.Contains("CPU 42%", row.AccessibleName, StringComparison.Ordinal);

        // Without these notifications the row would still say what it said before, and the
        // suite would still be green.
        Assert.Contains(nameof(MachineRow.Subtitle), notified);
        Assert.Contains(nameof(MachineRow.ToolTipText), notified);
        Assert.Contains(nameof(MachineRow.AccessibleName), notified);
    }

    [Fact]
    public async Task AProbeThatReturnsAfterTheMachineBecameWatchedDoesNotWrite()
    {
        // The probe STARTS by filtering out the watched machine, but it COMES BACK up to eight
        // seconds later, and one click in that time is enough. From then on two writers would
        // be writing into the same row - the probe every fifteen seconds, the main loop every
        // second - and the row would jerk between two different readings of the SAME machine.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint other = ObserverEndpoint.Remote(
            new Uri("https://altra:5058/"), "token", "machines.json", new string('a', 64), "altra");

        HeldClient held = new();

        MainViewModel viewModel = new(
            client: new HeldClient(alreadyReleased: true),
            configurationProblem: null,
            machineList: new MachineListResult([local, other], []),
            openMachine: _ => held);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
        Task loop = viewModel.RunAsync(stop.Token);

        MachineRow row = viewModel.Machines.Single(v => v.Endpoint == other);

        // Wait until the probe is REALLY in flight, not until some time has gone by.
        while (!stop.IsCancellationRequested && !held.HasEntered)
        {
            await Task.Delay(20, CancellationToken.None);
        }

        Assert.True(held.HasEntered, "la sonda non e' mai partita");

        // The click, while the response is still in the air.
        viewModel.SelectedMachine = row;

        held.Release();

        while (!stop.IsCancellationRequested && !held.HasExited)
        {
            await Task.Delay(20, CancellationToken.None);
        }

        // The probe was holding a CPU at 99 %: had it written, the row would say so.
        Assert.Equal(MachineLoad.None, row.MachineLoad);
        Assert.Equal(string.Empty, row.Subtitle);

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

    /// <summary>Answers only when the test releases it, and only the first time.</summary>
    private sealed class HeldClient(bool alreadyReleased = false) : IMetricsClient
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int entries;

        public bool HasEntered => Volatile.Read(ref entries) > 0;

        public bool HasExited { get; private set; }

        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.LocalChannel();

        public void Release() => gate.TrySetResult();

        public async Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken)
        {
            if (!alreadyReleased && Interlocked.Increment(ref entries) == 1)
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                HasExited = true;
            }

            return new SnapshotFetch(
                ServiceOutcome.Ok,
                string.Empty,
                Snapshot(Group(
                    "cpu",
                    MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(99d)))));
        }

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Ok, string.Empty, new MetricCatalog([])));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));
    }
}