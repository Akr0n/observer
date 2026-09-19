using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// The process panel: when it opens, what it remembers, and what it takes to end a process.
/// </summary>
/// <remarks>
/// It is the only place in the application that destroys anything, and the rules that matter
/// are not the ones you can see. The selection has to survive the refresh — the list is
/// rewritten every second while the user is aiming at the row — and the confirmation has to
/// disarm when the row changes, or the second click would kill the wrong process.
/// </remarks>
public class ProcessPanelTests
{
    private static MetricRow RowFor(string key) =>
        new(new MetricRowState(key, "label", "value", 0.5d, MetricSeverity.Ok));

    [Theory]
    [InlineData("cpu|cpu.usage.total|", "cpu")]
    [InlineData("memory|memory.used.percent|", "memory")]
    [InlineData("disk.activity|disk.busy.percent|Disk 0", "io")]
    public void TheCpuMemoryAndDiskActivityGaugesOpenTheList(string key, string expected) =>
        Assert.Equal(expected, ProcessResource.From(key));

    [Theory]
    [InlineData("disk|disk.used.percent|C:")]
    [InlineData("")]
    [InlineData(null)]
    public void TheDiskSpaceGaugesOpenNothing(string? key)
    {
        // This is not an oversight. The space used on a volume is not attributable to a
        // RUNNING process — whoever wrote those files may have been gone for months. A panel
        // that opened with the CPU list under the title of a volume would be saying something
        // false.
        Assert.Null(ProcessResource.From(key));
    }

    [Fact]
    public void OnlyDiskSpaceIsNotClickable()
    {
        Assert.False(RowFor("disk|disk.used.percent|C:").CanShowProcesses);
        Assert.True(RowFor("disk.activity|disk.busy.percent|Disk 0").CanShowProcesses);
        Assert.True(RowFor("cpu|cpu.usage.total|").CanShowProcesses);
    }

    [Fact]
    public async Task TheDiskActivityGaugeAsksForWholeMachineIo()
    {
        // The gauge is for ONE disk, the list is not: the counters are per process, not per
        // device. The title has to say so, and the service is asked for "io", not for the CPU.
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("disk.activity|disk.busy.percent|Disk 0"));

        Assert.Equal(["io"], client.Requested);
        Assert.Contains("I/O", viewModel.ProcessesTitle, StringComparison.Ordinal);
        Assert.Contains("whole machine", viewModel.ProcessesTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIoRateIsFormattedAsBytesPerSecondAndAnUnknownOneAsADash()
    {
        // A dash and not "0 B/s": they are two different claims, and the second one, on a list
        // sorted by I/O, would move attention to the wrong program.
        Assert.Equal("1.5 MiB/s", ProcessRowState.From(new ProcessWire(1, "copy", 0d, 10, 1_572_864d)).Io);
        Assert.Equal("—", ProcessRowState.From(new ProcessWire(1, "unknown", 0d, 10, null)).Io);
    }

    [Fact]
    public async Task TheSelectionSurvivesTheRefresh()
    {
        // The list is rewritten once a second. Without holding the selection on the PID, the
        // row being aimed at would deselect itself while you get ready to end it.
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 22);

        // Same PIDs, new values: that is what happens on every round. This goes through another
        // gauge and not the same one, because the same gauge a second time CLOSES the panel;
        // another one refreshes it in place, and the refresh is what matters here.
        client.Cpu = ["9.0 %", "3.0 %"];
        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("memory|memory.used.percent|"));

        Assert.NotNull(viewModel.SelectedProcess);
        Assert.Equal(22, viewModel.SelectedProcess!.Pid);
    }

    [Fact]
    public async Task ChangingTheSelectedRowDisarmsTheConfirmation()
    {
        // The rule that prevents the worst accident: the confirmation is armed on one process,
        // the user changes their mind and selects another, and the next click would end the new
        // one without ever having confirmed it.
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        viewModel.SelectedProcess = viewModel.Processes.First();
        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        Assert.True(viewModel.IsAwaitingEndConfirmation);
        Assert.Empty(client.Killed);

        viewModel.SelectedProcess = viewModel.Processes.Last();

        Assert.False(viewModel.IsAwaitingEndConfirmation);
        Assert.Empty(client.Killed);
    }

    [Fact]
    public async Task EndingAProcessTakesTwoClicks()
    {
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));
        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 11);

        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);
        Assert.Empty(client.Killed);

        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        Assert.Equal([11], client.Killed);
        Assert.False(viewModel.IsAwaitingEndConfirmation);
    }

    [Fact]
    public async Task ClickingTheSameGaugeAgainClosesThePanel()
    {
        // The gesture anyone tries first to make what they have just brought up go away again.
        // It used to reopen the same list, and the only way to close it was the Close button
        // at the bottom right.
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));
        Assert.True(viewModel.IsProcessPanelOpen);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        Assert.False(viewModel.IsProcessPanelOpen);
        Assert.Empty(viewModel.Processes);
        Assert.Equal(["cpu"], client.Requested);
    }

    [Fact]
    public async Task ClickingAnotherGaugeSwitchesTheListWithoutClosing()
    {
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));
        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("memory|memory.used.percent|"));

        Assert.True(viewModel.IsProcessPanelOpen);
        Assert.Contains("memory", viewModel.ProcessesTitle, StringComparison.Ordinal);
        Assert.Equal(["cpu", "memory"], client.Requested);
    }

    [Fact]
    public async Task TheButtonSaysWhenTheSecondClickIsNeeded()
    {
        // A single button that changes its text: that way the keyboard focus stays where it is.
        // With two alternating buttons, the first click made the one just pressed disappear.
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));
        viewModel.SelectedProcess = viewModel.Processes.First();

        Assert.Equal("End process", viewModel.EndButtonText);

        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        // Armed, it names its target: the second click is aimed at a process that can be read
        // before pressing, not at whatever the list has selected by then.
        Assert.Equal("Click again to end pid 11 (greedy)", viewModel.EndButtonText);

        viewModel.SelectedProcess = viewModel.Processes.Last();

        Assert.Equal("End process", viewModel.EndButtonText);
    }

    [Fact]
    public void ARowReadsInFullWithItsHeadings() =>
        Assert.Equal(
            "claude, CPU 15.1 %, memory 228.5 MiB, I/O 1.1 MiB/s",
            new ProcessRowState(1, "claude", "15.1 %", "228.5 MiB", "1.1 MiB/s").AccessibleName);

    [Fact]
    public async Task ClosingThePanelForgetsEverything()
    {
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));
        viewModel.SelectedProcess = viewModel.Processes.First();

        viewModel.CloseProcessPanelCommand.Execute(parameter: null);

        Assert.False(viewModel.IsProcessPanelOpen);
        Assert.Empty(viewModel.Processes);
        Assert.Null(viewModel.SelectedProcess);
        Assert.False(viewModel.IsAwaitingEndConfirmation);
    }

    [Fact]
    public async Task AClickWhileTheFirstReadIsInFlightIsNotDropped()
    {
        // Slow remote machine: the first read of the list does not come back straight away. In
        // the meantime the user clicks again — to close, or to move to another gauge —
        // and that click has to count. It used to be dropped: the command is ONE for all the
        // gauges, and an async command that is running refuses concurrent executions.
        FakeProcessClient client = new() { PendingRead = new TaskCompletionSource<ProcessFetch>() };
        MainViewModel viewModel = new(client, configurationProblem: null);

        Task first = viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        Assert.True(viewModel.IsProcessPanelOpen);
        Assert.True(viewModel.OpenProcessesCommand.CanExecute(RowFor("memory|memory.used.percent|")));

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        Assert.False(viewModel.IsProcessPanelOpen);

        // And a response that arrives late for a panel that is closed by now does not fill it.
        client.PendingRead.SetResult(new ProcessFetch(
            ServiceOutcome.Ok, string.Empty, [new ProcessRowState(99, "late", "99 %", "1 MiB")]));
        await first;

        Assert.False(viewModel.IsProcessPanelOpen);
        Assert.Empty(viewModel.Processes);
    }

    [Fact]
    public async Task ALateResponseDoesNotLandUnderTheWrongTitle()
    {
        FakeProcessClient client = new();
        TaskCompletionSource<ProcessFetch> inFlight = new();
        client.PendingRead = inFlight;
        MainViewModel viewModel = new(client, configurationProblem: null);

        Task cpu = viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        // The second gauge answers straight away; the first one, later.
        client.PendingRead = null;
        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("memory|memory.used.percent|"));

        Assert.Contains("memory", viewModel.ProcessesTitle, StringComparison.Ordinal);
        Assert.Equal(["greedy", "quiet"], viewModel.Processes.Select(row => row.Name));

        inFlight.SetResult(new ProcessFetch(
            ServiceOutcome.Ok, string.Empty, [new ProcessRowState(99, "late", "99 %", "1 MiB")]));
        await cpu;

        Assert.Equal(["greedy", "quiet"], viewModel.Processes.Select(row => row.Name));
    }

    [Fact]
    public async Task TheConfirmationSurvivesTheRefreshThatRewritesTheRow()
    {
        // The list is rewritten once a second by the loop, and ProcessRowState is a record whose
        // CPU and memory are formatted strings: the re-selected row is a different object as soon
        // as a number changes. Armed on the row, the confirmation disarmed itself on the next
        // round - and a busy process, which is the one being ended, changes its numbers every
        // round.
        FakeProcessClient client = new() { AnswersPoll = true };
        MainViewModel viewModel = new(client, configurationProblem: null);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(stop.Token);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));
        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 11);
        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        Assert.True(viewModel.IsAwaitingEndConfirmation);

        // The rows the loop reads from here on carry new numbers, which is what makes the
        // re-selected row a different object.
        client.Cpu = ["9.0 %", "3.0 %"];

        while (!stop.IsCancellationRequested
            && !viewModel.Processes.Any(row => string.Equals(row.Cpu, "9.0 %", StringComparison.Ordinal)))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        // Without this the test would pass vacuously if the refresh never ran: the wait above
        // gives up quietly, and a confirmation nothing ever touched is of course still armed.
        Assert.True(
            viewModel.Processes.Any(row => string.Equals(row.Cpu, "9.0 %", StringComparison.Ordinal)),
            $"the panel was never refreshed: reads={client.Requested.Count}");

        Assert.True(viewModel.IsAwaitingEndConfirmation);
        Assert.Equal(11, viewModel.SelectedProcess!.Pid);
        Assert.Contains("greedy", viewModel.EndButtonText, StringComparison.Ordinal);
        Assert.Contains("11", viewModel.EndButtonText, StringComparison.Ordinal);
        Assert.Empty(client.Killed);

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

    [Fact]
    public async Task ARefusedKillSaysSoAndTheListIsReadAgain()
    {
        // The other half of the kill: the service can refuse - a process the operating system
        // protects - and then the panel has to say so and show the list as it is now.
        FakeProcessClient client = new() { Killing = new KillFetch(ServiceOutcome.UnexpectedResponse, "it is protected") };
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));
        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 11);

        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);
        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        Assert.Equal("it is protected", viewModel.ProcessesProblem);
        Assert.Equal(["cpu", "cpu"], client.Requested);
    }

    [Fact]
    public async Task AKillAnsweredAfterTheMachineChangedSaysNothingOnTheNewMachine()
    {
        // The kill is the only destructive request, and its answer can arrive after the user has
        // moved on. Reopening the panel on the new machine clears the problem line, so a late
        // refusal would land under the new machine's name - and pull an extra read with it.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint other = ObserverEndpoint.Remote(
            new Uri("https://other:5058/"), "token", "other", new string('a', 64));

        FakeProcessClient previous = new() { PendingKill = new TaskCompletionSource<KillFetch>() };
        FakeProcessClient current = new();

        MainViewModel viewModel = new(
            previous,
            configurationProblem: null,
            machineList: new MachineListResult([local, other], []),
            openMachine: endpoint => current);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));
        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 11);

        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);
        Task inFlight = viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        viewModel.SelectedMachine = viewModel.Machines.Single(entry => entry.Endpoint == other);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        previous.PendingKill!.SetResult(new KillFetch(ServiceOutcome.UnexpectedResponse, "the machine you left refused"));

        await inFlight;

        Assert.Equal(string.Empty, viewModel.ProcessesProblem);
        Assert.Equal(["cpu"], current.Requested);
    }

    [Fact]
    public async Task APidThatCameBackUnderAnotherNameIsNotTheProcessThatWasArmed()
    {
        // Pids are recycled, and the list re-selects by pid alone: the name is what stops an
        // already armed second click from ending whatever inherited the number.
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));
        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 11);

        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        viewModel.SelectedProcess = new ProcessRowState(11, "reused", "1.0 %", "1 MiB");

        Assert.False(viewModel.IsAwaitingEndConfirmation);

        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        Assert.Empty(client.Killed);
    }

    [Fact]
    public async Task AnArmedProcessThatLeavesTheListIsForgotten()
    {
        // It ended, or it fell out of the top rows. The target must go with it: otherwise it
        // waits, invisible, and the click that re-selects that row when it comes back - even the
        // right click that opens "Copy row" - would find the confirmation already armed.
        FakeProcessClient client = new() { AnswersPoll = true };
        MainViewModel viewModel = new(client, configurationProblem: null);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(stop.Token);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));
        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 11);
        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        Assert.True(viewModel.IsAwaitingEndConfirmation);

        // The armed process is gone from the rows the service returns from now on.
        client.WithoutTheGreedyOne = true;

        while (!stop.IsCancellationRequested && viewModel.Processes.Any(row => row.Pid == 11))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.DoesNotContain(viewModel.Processes, row => row.Pid == 11);

        // And now it is back, and the user clicks its row.
        client.WithoutTheGreedyOne = false;

        while (!stop.IsCancellationRequested && !viewModel.Processes.Any(row => row.Pid == 11))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 11);

        Assert.False(viewModel.IsAwaitingEndConfirmation);

        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        Assert.Empty(client.Killed);

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

    [Fact]
    public async Task ComingBackToTheRowThatWasArmedStillTakesTwoClicks()
    {
        // The other half of the disarm, and the one a bare "the flag went false" test cannot
        // see: after changing one's mind the target must be FORGOTTEN, not just unflagged, or
        // re-selecting that row would find it still armed and end it on the very first click.
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 11);
        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 22);
        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 11);

        Assert.False(viewModel.IsAwaitingEndConfirmation);

        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        Assert.Empty(client.Killed);
        Assert.True(viewModel.IsAwaitingEndConfirmation);
    }

    [Fact]
    public async Task SwitchingResourceForgetsTheArmedTarget()
    {
        // The panel stays open but the list becomes another resource's: a confirmation armed on
        // the CPU list must not come back to life when the same process appears in the memory
        // one.
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 11);
        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("memory|memory.used.percent|"));

        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 11);

        Assert.False(viewModel.IsAwaitingEndConfirmation);

        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        Assert.Empty(client.Killed);
    }

    [Fact]
    public async Task TheSelectionGoingNullDuringARefreshDoesNotDisarmTheConfirmation()
    {
        // What the bound list does when the collection is cleared: it writes null into the
        // selection before the re-selection arrives. It never happens in the view model on its
        // own, so this test is the only place that rule is written down - and it is the path
        // that disarmed the confirmation on EVERY refresh, numbers changed or not.
        FakeProcessClient client = new();
        MainViewModel viewModel = new(client, configurationProblem: null);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));
        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 11);
        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        viewModel.SelectedProcess = null;
        viewModel.SelectedProcess = viewModel.Processes.Single(row => row.Pid == 11);

        Assert.True(viewModel.IsAwaitingEndConfirmation);

        await viewModel.EndSelectedProcessCommand.ExecuteAsync(parameter: null);

        Assert.Equal([11], client.Killed);
    }

    [Fact]
    public async Task AReadStartedOnThePreviousMachineDoesNotFillTheNewMachinesPanel()
    {
        // Both panels ask for the same resource, so the guard on the resource cannot tell them
        // apart, and App.Open hands back the SAME client object for the same endpoint, so
        // comparing the client by reference cannot tell A -> B -> A apart either. What separates
        // them is that the machine changed while the response was in flight.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint other = ObserverEndpoint.Remote(
            new Uri("https://other:5058/"), "token", "other", new string('a', 64));

        FakeProcessClient previous = new() { PendingRead = new TaskCompletionSource<ProcessFetch>() };
        FakeProcessClient current = new();

        MainViewModel viewModel = new(
            previous,
            configurationProblem: null,
            machineList: new MachineListResult([local, other], []),
            openMachine: endpoint => current);

        Task inFlight = viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        viewModel.SelectedMachine = viewModel.Machines.Single(entry => entry.Endpoint == other);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        previous.PendingRead!.SetResult(new ProcessFetch(
            ServiceOutcome.Ok, string.Empty, [new ProcessRowState(99, "ghost", "99.0 %", "1 GiB")]));

        await inFlight;

        Assert.DoesNotContain(viewModel.Processes, row => row.Pid == 99);
        Assert.Equal([11, 22], viewModel.Processes.Select(row => row.Pid));
    }

    [Fact]
    public async Task AProblemFromThePreviousMachineDoesNotReachTheNewMachinesPanel()
    {
        // The half that is easy to forget: a superseded request must not report its failure
        // either, or the new machine's panel explains a fault belonging to the one just left.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint other = ObserverEndpoint.Remote(
            new Uri("https://other:5058/"), "token", "other", new string('a', 64));

        FakeProcessClient previous = new() { PendingRead = new TaskCompletionSource<ProcessFetch>() };
        FakeProcessClient current = new();

        MainViewModel viewModel = new(
            previous,
            configurationProblem: null,
            machineList: new MachineListResult([local, other], []),
            openMachine: endpoint => current);

        Task inFlight = viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        viewModel.SelectedMachine = viewModel.Machines.Single(entry => entry.Endpoint == other);

        await viewModel.OpenProcessesCommand.ExecuteAsync(RowFor("cpu|cpu.usage.total|"));

        previous.PendingRead!.SetResult(new ProcessFetch(
            ServiceOutcome.Unreachable, "the machine you just left is down", []));

        await inFlight;

        Assert.Equal(string.Empty, viewModel.ProcessesProblem);
    }

    private sealed class FakeProcessClient : IMetricsClient
    {
        public IReadOnlyList<string> Cpu { get; set; } = ["5.0 %", "1.0 %"];

        /// <summary>When set, the next read of the list answers only when the test says so.</summary>
        public TaskCompletionSource<ProcessFetch>? PendingRead { get; set; }

        /// <summary>What a kill answers, when the test wants something other than success.</summary>
        public KillFetch Killing { get; set; } = new(ServiceOutcome.Ok, string.Empty);

        /// <summary>When true the list comes back without the process the tests arm on.</summary>
        public bool WithoutTheGreedyOne { get; set; }

        public List<int> Killed { get; } = [];

        public List<string> Requested { get; } = [];

        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.LocalChannel();

        /// <summary>When true the poll is answered, so the loop refreshes the open panel.</summary>
        public bool AnswersPoll { get; set; }

        public int Polls;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken)
        {
            Polls++;

            return Task.FromResult(AnswersPoll
                ? new SnapshotFetch(
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
                        ]))
                : new SnapshotFetch(ServiceOutcome.Unreachable, "down", null));
        }

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Ok, string.Empty, MetricCatalog.Empty));

        public Task<HistoryFetch> GetHistoryAsync(
            HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));

        public Task<ProcessFetch> GetProcessesAsync(
            string by, int top, CancellationToken cancellationToken)
        {
            Requested.Add(by);

            if (PendingRead is { } pending)
            {
                return pending.Task;
            }

            return Task.FromResult(new ProcessFetch(
                ServiceOutcome.Ok,
                string.Empty,
                WithoutTheGreedyOne
                    ? [new ProcessRowState(22, "quiet", Cpu[1], "10 MiB")]
                    : [
                        new ProcessRowState(11, "greedy", Cpu[0], "100 MiB"),
                        new ProcessRowState(22, "quiet", Cpu[1], "10 MiB"),
                    ]));
        }

        /// <summary>When set, the kill answers only when the test says so.</summary>
        public TaskCompletionSource<KillFetch>? PendingKill { get; set; }

        public Task<KillFetch> KillProcessAsync(int pid, CancellationToken cancellationToken)
        {
            Killed.Add(pid);

            if (PendingKill is { } pending)
            {
                return pending.Task;
            }

            return Task.FromResult(Killing);
        }
    }
}
