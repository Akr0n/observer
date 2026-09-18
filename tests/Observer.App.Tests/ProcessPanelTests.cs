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
        new(new MetricRowState(key, "etichetta", "valore", 0.5d, MetricSeverity.Ok));

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
        Assert.Equal("1.5 MiB/s", ProcessRowState.From(new ProcessWire(1, "copia", 0d, 10, 1_572_864d)).Io);
        Assert.Equal("—", ProcessRowState.From(new ProcessWire(1, "ignoto", 0d, 10, null)).Io);
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

        Assert.Equal("Click again to end it", viewModel.EndButtonText);

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
        // the meantime whoever clicked clicks again — to close, or to move to another gauge —
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
            ServiceOutcome.Ok, string.Empty, [new ProcessRowState(99, "in ritardo", "99 %", "1 MiB")]));
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
        Assert.Equal(["affamato", "tranquillo"], viewModel.Processes.Select(row => row.Name));

        inFlight.SetResult(new ProcessFetch(
            ServiceOutcome.Ok, string.Empty, [new ProcessRowState(99, "in ritardo", "99 %", "1 MiB")]));
        await cpu;

        Assert.Equal(["affamato", "tranquillo"], viewModel.Processes.Select(row => row.Name));
    }

    private sealed class FakeProcessClient : IMetricsClient
    {
        public IReadOnlyList<string> Cpu { get; set; } = ["5.0 %", "1.0 %"];

        /// <summary>When set, the next read of the list answers only when the test says so.</summary>
        public TaskCompletionSource<ProcessFetch>? PendingRead { get; set; }

        public List<int> Killed { get; } = [];

        public List<string> Requested { get; } = [];

        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.LocalChannel();

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.Unreachable, "spenta", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Ok, string.Empty, MetricCatalog.Empty));

        public Task<HistoryFetch> GetHistoryAsync(
            HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));

        public Task<ProcessFetch> GetProcessesAsync(
            string by, int top, CancellationToken cancellationToken)
        {
            Requested.Add(by);

            if (PendingRead is { } expected)
            {
                return expected.Task;
            }

            return Task.FromResult(new ProcessFetch(
                ServiceOutcome.Ok,
                string.Empty,
                [
                    new ProcessRowState(11, "affamato", Cpu[0], "100 MiB"),
                    new ProcessRowState(22, "tranquillo", Cpu[1], "10 MiB"),
                ]));
        }

        public Task<KillFetch> KillProcessAsync(int pid, CancellationToken cancellationToken)
        {
            Killed.Add(pid);

            return Task.FromResult(new KillFetch(ServiceOutcome.Ok, string.Empty));
        }
    }
}
