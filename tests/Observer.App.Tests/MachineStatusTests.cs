using FluentAvalonia.UI.Controls;
using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// The dot next to each machine: what it says, and who updates it.
/// </summary>
/// <remarks>
/// Two rules. The first: the colour follows the same rule as the status bar, with its own
/// ten-second grace, so a red dot and a red bar mean the same thing. The second: the machines
/// that are not being watched are probed on their own, with nobody clicking on them and without
/// the gauge loop waiting for them.
/// </remarks>
public class MachineStatusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static ObserverEndpoint RemoteAt(string host) =>
        ObserverEndpoint.Remote(new Uri($"https://{host}:5058/"), "token", host, new string('a', 64));

    [Fact]
    public void ARowThatWasMeasuringSaysItStopped_NotThatItNeverStarted()
    {
        // The sidebar has the same two cases as the status bar, and the same one wording had to
        // cover both: a machine that has never answered, and one that answered with readings
        // until a moment ago and whose service now refuses to serve a sample that stopped
        // advancing. The row knows which of the two it is - it recorded the readings itself.
        MachineRow row = new(RemoteAt("other"));

        row.Record(
            ServiceOutcome.Ok,
            string.Empty,
            T0,
            new MachineSnapshot(MachineSnapshot.CurrentSchemaVersion, T0, []));

        Assert.Equal("Reachable", row.Detail);

        // The first failed reading starts the clock, so it is INSIDE the ten-second tolerance -
        // the row's own rule, unchanged: a counter that starts on every blip teaches you to
        // ignore it. But even there the wording must not claim the machine never started.
        row.Record(ServiceOutcome.NotReadyYet, "stopped sampling", T0 + TimeSpan.FromMinutes(5));

        Assert.Equal("Readings paused", row.Detail);

        row.Record(ServiceOutcome.NotReadyYet, "stopped sampling", T0 + TimeSpan.FromMinutes(6));

        Assert.Equal("Not measuring", row.Detail);

        // And it does not flip back to the other wording on the next reading of the same run,
        // which is what a check on "was it reachable a moment ago" would have done: the row
        // would have alternated between the two sentences once a probe.
        row.Record(ServiceOutcome.NotReadyYet, "stopped sampling", T0 + TimeSpan.FromMinutes(7));

        Assert.Equal("Not measuring", row.Detail);
    }

    [Fact]
    public void AFreshRowHasNoStatusYet()
    {
        MachineRow row = new(RemoteAt("other"));

        Assert.True(row.IsUnknown);
        Assert.False(row.IsReachable);
        Assert.Equal("Not checked yet", row.Detail);
    }

    [Fact]
    public void AGoodAnswerMarksTheMachineReachable()
    {
        MachineRow row = new(RemoteAt("other"));

        row.Record(ServiceOutcome.Ok, string.Empty, T0);

        Assert.True(row.IsReachable);
        Assert.Equal("Reachable", row.Detail);
        Assert.Contains("Reachable", row.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalIsAWarningThenAFaultAndAGoodAnswerResetsIt()
    {
        // The same rule as the status bar: a service that is still starting refuses, and for ten
        // seconds that is normal. After that, it is not.
        MachineRow row = new(RemoteAt("other"));

        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0);
        Assert.True(row.IsWarning);

        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0 + StatusEscalation.GracePeriod + TimeSpan.FromSeconds(1));
        Assert.True(row.IsFaulted);

        // And a good answer clears the streak: the next fault starts over from scratch.
        row.Record(ServiceOutcome.Ok, string.Empty, T0 + TimeSpan.FromMinutes(1));
        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0 + TimeSpan.FromMinutes(1));
        Assert.True(row.IsWarning);
    }

    [Fact]
    public void WithinTheGraceTheRowDoesNotYetSayForHowLong()
    {
        // A counter that starts on every hiccup teaches you to ignore it, and that is exactly
        // what the ten seconds of grace exist to prevent.
        MachineRow row = new(RemoteAt("other"));

        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0);

        Assert.True(row.IsWarning);
        Assert.Equal(string.Empty, row.DowntimeText);
        Assert.Equal(string.Empty, row.Subtitle);
    }

    [Fact]
    public void PastTheGraceTheRowSaysHowLongTheFaultHasLasted()
    {
        MachineRow row = new(RemoteAt("other"));

        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0);
        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0 + TimeSpan.FromMinutes(3));

        Assert.True(row.IsFaulted);
        Assert.Equal("for 3 min", row.DowntimeText);
        Assert.Equal("for 3 min", row.Subtitle);

        // The duration is announced even without seeing the row: the tooltip and the accessible
        // name come from the same text, so they cannot diverge.
        Assert.Contains("for 3 min", row.ToolTipText, StringComparison.Ordinal);
        Assert.Contains("for 3 min", row.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void AServiceThatIsNotSamplingSaysForHowLongEvenWhenItIsNotRed()
    {
        // The yellow branch, which is the reason the gate is the TONE and not the IsFaulted
        // state: a reachable service that has not sampled yet stays a warning, never a red, and
        // it can last for days. A gate written on red would leave it with no duration precisely
        // when it is the thing that lasts longest.
        MachineRow row = new(RemoteAt("other"));

        row.Record(ServiceOutcome.NotReadyYet, "warming up", T0);
        row.Record(ServiceOutcome.NotReadyYet, "warming up", T0 + TimeSpan.FromMinutes(7));

        Assert.True(row.IsWarning);
        Assert.False(row.IsFaulted);
        Assert.Equal("for 7 min", row.DowntimeText);
    }

    [Fact]
    public void RecordingAFaultNotifiesTheDurationTheSubtitleAndTheTooltip()
    {
        // Subtitle is the Text of the second line: without its notification the duration would
        // change and the row would go on saying what it said before, with the suite green.
        MachineRow row = new(RemoteAt("other"));
        List<string> notified = [];
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? string.Empty);

        row.Record(ServiceOutcome.TokenRejected, "rejected", T0);

        Assert.Contains(nameof(MachineRow.DowntimeText), notified);
        Assert.Contains(nameof(MachineRow.Subtitle), notified);
        Assert.Contains(nameof(MachineRow.ToolTipText), notified);
    }

    [Fact]
    public void AMachineThatComesBackNoLongerShowsTheDuration()
    {
        MachineRow row = new(RemoteAt("other"));

        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0);
        row.Record(ServiceOutcome.ConnectionRefused, "refused", T0 + TimeSpan.FromMinutes(3));
        row.Record(ServiceOutcome.Ok, string.Empty, T0 + TimeSpan.FromMinutes(4));

        Assert.Equal(string.Empty, row.DowntimeText);

        // No tail: the description goes back to name and status, with no duration stuck on it.
        Assert.EndsWith(": Reachable", row.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void ARejectedTokenSaysForHowLongFromTheFirstReading()
    {
        // It gets no grace: in a minute it will be identical, so the duration starts at once.
        MachineRow row = new(RemoteAt("other"));

        row.Record(ServiceOutcome.TokenRejected, "rejected", T0);

        Assert.True(row.IsFaulted);
        Assert.Equal("for under 1 min", row.DowntimeText);
    }

    [Fact]
    public void ARejectedTokenIsAFaultWithNoGrace()
    {
        // In a minute it will be identical: no grace period can change that.
        MachineRow row = new(RemoteAt("other"));

        row.Record(ServiceOutcome.TokenRejected, "rejected", T0);

        Assert.True(row.IsFaulted);
    }

    [Fact]
    public async Task TheMachinesNotBeingWatchedAreProbedOnTheirOwn()
    {
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint alive = RemoteAt("alive");
        ObserverEndpoint down = RemoteAt("down");

        List<ObserverEndpoint> opened = [];

        MainViewModel viewModel = new(
            client: new RespondingClient(local),
            configurationProblem: null,
            machineList: new MachineListResult([local, alive, down], []),
            openMachine: endpoint =>
            {
                opened.Add(endpoint);

                return endpoint == alive ? new RespondingClient(endpoint) : new UnreachableClient(endpoint);
            });

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(cancellation.Token);

        while (!cancellation.IsCancellationRequested && viewModel.Machines.Any(row => row.IsUnknown))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        // The watched machine follows the main loop; the probe handles the other two.
        Assert.True(viewModel.Machines[0].IsReachable);
        Assert.True(viewModel.Machines[1].IsReachable);
        Assert.True(viewModel.Machines[2].IsWarning, viewModel.Machines[2].Detail);

        // The probe does NOT open a client to the machine that is already being watched.
        Assert.DoesNotContain(local, opened);
        Assert.Contains(alive, opened);
        Assert.Contains(down, opened);

        await cancellation.CancelAsync();

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
    public void ChangingStatusNotifiesEveryStatusFlagAndTheAccessibleName()
    {
        // These are the names the Ellipse's Classes are bound to: dropping one from the
        // attribute would leave the dot grey for ever, and no test would say so.
        MachineRow row = new(RemoteAt("other"));
        List<string> notified = [];
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? string.Empty);

        row.Record(ServiceOutcome.Ok, string.Empty, T0);

        Assert.Contains(nameof(MachineRow.IsUnknown), notified);
        Assert.Contains(nameof(MachineRow.IsReachable), notified);
        Assert.Contains(nameof(MachineRow.IsWarning), notified);
        Assert.Contains(nameof(MachineRow.IsFaulted), notified);
        Assert.Contains(nameof(MachineRow.AccessibleName), notified);
    }

    [Fact]
    public async Task TheWatchedMachineIsNotProbedEvenWhenItIsRemote()
    {
        // "Skip the selected one" and "skip the local one" are indistinguishable when the
        // watched machine is the first in the list. Here the watched machine is the second,
        // and remote.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint alive = RemoteAt("alive");
        ObserverEndpoint down = RemoteAt("down");
        List<ObserverEndpoint> opened = [];

        MainViewModel viewModel = new(
            client: new RespondingClient(alive),
            configurationProblem: null,
            machineList: new MachineListResult([local, alive, down], []),
            openMachine: endpoint =>
            {
                opened.Add(endpoint);

                return endpoint == down ? new UnreachableClient(endpoint) : new RespondingClient(endpoint);
            });

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(cancellation.Token);

        while (!cancellation.IsCancellationRequested && viewModel.Machines.Any(row => row.IsUnknown))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Same(viewModel.Machines[1], viewModel.SelectedMachine);
        Assert.DoesNotContain(alive, opened);
        Assert.Contains(local, opened);
        Assert.Contains(down, opened);

        await Stop(cancellation, loop);
    }

    [Fact]
    public async Task WithNoSelectionTheWatchedMachineIsStillNotProbed()
    {
        // The list should never clear the selection (AlwaysSelected), but if it does the main
        // loop goes on reading the same machine, and the probe must NOT read it a second time:
        // what counts is the watched entry, not the selection.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint other = RemoteAt("other");
        FakeClock clock = new();
        List<ObserverEndpoint> opened = [];

        MainViewModel viewModel = new(
            client: new RespondingClient(local),
            configurationProblem: null,
            clock: clock.Now,
            machineList: new MachineListResult([local, other], []),
            openMachine: endpoint =>
            {
                opened.Add(endpoint);

                return new RespondingClient(endpoint);
            });

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(cancellation.Token);

        while (!cancellation.IsCancellationRequested && viewModel.Machines[1].IsUnknown)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        viewModel.SelectedMachine = null;
        clock.Advance(MainViewModel.StatusRefreshInterval + TimeSpan.FromSeconds(1));

        while (!cancellation.IsCancellationRequested && opened.Count(endpoint => endpoint == other) < 2)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(opened.Count(endpoint => endpoint == other) >= 2, "the second probe never started");
        Assert.DoesNotContain(local, opened);
        Assert.True(viewModel.Machines[0].IsReachable);

        await Stop(cancellation, loop);
    }

    [Fact]
    public async Task AHangingProbeDoesNotStopTheLoopAndDoesNotStartASecondOne()
    {
        // The two promises the probes make, tested with a client that does NOT answer until the
        // test says so: a client that answered straight away would leave both of them mutable.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint slow = RemoteAt("slow");
        FakeClock clock = new();
        CountingClient watched = new(local);
        HangingClient hanging = new(slow);
        int openCount = 0;

        MainViewModel viewModel = new(
            client: watched,
            configurationProblem: null,
            clock: clock.Now,
            machineList: new MachineListResult([local, slow], []),
            openMachine: _ =>
            {
                openCount++;

                return hanging;
            });

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
        Task loop = viewModel.RunAsync(cancellation.Token);

        // The probe has started and stays hanging; the main loop meanwhile keeps reading.
        while (!cancellation.IsCancellationRequested && watched.Reads < 3)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(watched.Reads >= 3, "the main loop waited for the probe");
        Assert.Equal(1, openCount);
        Assert.True(viewModel.Machines[1].IsProbing);
        Assert.True(viewModel.Machines[1].IsUnknown);

        // Two cadences go by: with the probe still in flight a second one does not start.
        clock.Advance(MainViewModel.StatusRefreshInterval * 2);
        int before = watched.Reads;

        while (!cancellation.IsCancellationRequested && watched.Reads < before + 2)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Equal(1, openCount);

        // When it comes back, the dot changes and the row can be probed again.
        hanging.Respond(new SnapshotFetch(ServiceOutcome.Unreachable, "down", null));

        while (!cancellation.IsCancellationRequested && viewModel.Machines[1].IsUnknown)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[1].IsWarning, viewModel.Machines[1].Detail);
        Assert.False(viewModel.Machines[1].IsProbing);

        await Stop(cancellation, loop);
    }

    [Fact]
    public async Task AProbeThatThrowsBecomesARedDotAndTheLoopGoesOn()
    {
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint broken = RemoteAt("broken");
        CountingClient watched = new(local);

        MainViewModel viewModel = new(
            client: watched,
            configurationProblem: null,
            machineList: new MachineListResult([local, broken], []),
            openMachine: endpoint => new ThrowingClient(endpoint));

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(cancellation.Token);

        while (!cancellation.IsCancellationRequested && viewModel.Machines[1].IsUnknown)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[1].IsFaulted, viewModel.Machines[1].Detail);
        Assert.Equal("Reading failed", viewModel.Machines[1].Detail);
        Assert.False(viewModel.Machines[1].IsProbing);

        // And the main loop is alive.
        int before = watched.Reads;

        while (!cancellation.IsCancellationRequested && watched.Reads <= before)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(watched.Reads > before, "the main loop stopped");

        await Stop(cancellation, loop);
    }

    [Fact]
    public async Task RereadingTheMachineRestartsTheDurationFromZero()
    {
        // Same place in the list, machine swapped underneath: "down for half an hour" said of
        // the previous one would be a lie, and it is a lie nobody would go looking for.
        // The path runs rereadEndpoint -> ProbeAsync -> Update, which is internal: it is tested
        // from here, where it is reachable, instead of widening the class's surface.
        // The client rejects the new credential TOO, otherwise a good reading would clear
        // everything by another route and the test would pass even without the reset.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint oldEndpoint = RemoteAt("rotated");
        ObserverEndpoint newEndpoint = oldEndpoint with { ApiToken = "new-token" };
        FakeClock clock = new();
        bool rotate = false;

        MainViewModel viewModel = new(
            client: new RespondingClient(local),
            configurationProblem: null,
            clock: clock.Now,
            machineList: new MachineListResult([local, oldEndpoint], []),
            openMachine: endpoint => new TokenRejectedClient(endpoint),
            rereadEndpoint: _ => rotate ? newEndpoint : null);

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
        Task loop = viewModel.RunAsync(cancellation.Token);

        // A rejected token is red from the very first moment: no grace to wait out.
        while (!cancellation.IsCancellationRequested && !viewModel.Machines[1].IsFaulted)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        // The fault ages. The clock moves only once: it is the later probes that read it, and
        // the row ends up saying half an hour.
        clock.Advance(TimeSpan.FromMinutes(30));

        while (!cancellation.IsCancellationRequested
            && !viewModel.Machines[1].DowntimeText.Contains("30 min", StringComparison.Ordinal))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Equal("for 30 min", viewModel.Machines[1].DowntimeText);

        // Now the machine changes underneath: the next probe rereads it. The clock has to
        // advance, or the probe never fires again and there is no later reading.
        rotate = true;

        while (!cancellation.IsCancellationRequested && viewModel.Machines[1].Endpoint != newEndpoint)
        {
            clock.Advance(MainViewModel.StatusRefreshInterval + TimeSpan.FromSeconds(1));
            await Task.Delay(50, CancellationToken.None);
        }

        // The machine is still down, but it is ANOTHER machine: the measurement restarts from
        // zero and the next refusal starts again at "under 1 min", instead of carrying on the
        // previous one's half hour. Without the reset inside Update the duration would go on.
        Assert.Equal(newEndpoint, viewModel.Machines[1].Endpoint);
        Assert.True(viewModel.Machines[1].IsFaulted || viewModel.Machines[1].IsWarning);
        // Empty if you look between the reset and the next reading, "under 1 min" if you look
        // after it. Without the reset it would be the earlier half hour, which keeps growing:
        // an assertion on one exact value would not be enough to tell them apart.
        string after = viewModel.Machines[1].DowntimeText;
        Assert.True(after.Length == 0 || after == "for under 1 min", $"duration after the re-read: '{after}'");

        await Stop(cancellation, loop);
    }

    [Fact]
    public async Task AfterARejectedTokenTheProbeRereadsTheMachine()
    {
        // "observer token set" with the window open, on a machine that is NOT being watched: the
        // next probe has to start with the new credential, not with the one read at start-up.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint oldEndpoint = RemoteAt("rotated");
        ObserverEndpoint newEndpoint = oldEndpoint with { ApiToken = "new-token" };
        FakeClock clock = new();
        List<ObserverEndpoint> opened = [];

        MainViewModel viewModel = new(
            client: new RespondingClient(local),
            configurationProblem: null,
            clock: clock.Now,
            machineList: new MachineListResult([local, oldEndpoint], []),
            openMachine: endpoint =>
            {
                opened.Add(endpoint);

                return endpoint == newEndpoint ? new RespondingClient(endpoint) : new TokenRejectedClient(endpoint);
            },
            rereadEndpoint: _ => newEndpoint);

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(cancellation.Token);

        while (!cancellation.IsCancellationRequested && !viewModel.Machines[1].IsFaulted)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Equal("Token rejected", viewModel.Machines[1].Detail);

        clock.Advance(MainViewModel.StatusRefreshInterval + TimeSpan.FromSeconds(1));

        while (!cancellation.IsCancellationRequested && !viewModel.Machines[1].IsReachable)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[1].IsReachable, viewModel.Machines[1].Detail);
        Assert.Contains(newEndpoint, opened);
        Assert.Equal(newEndpoint, viewModel.Machines[1].Endpoint);

        await Stop(cancellation, loop);
    }

    [Fact]
    public async Task ChoosingAMachineTheProbeAlreadyKnowsIsDownTheBarDoesNotSayConnecting()
    {
        // Bar and dot share one clock: the probe has known for sixteen seconds that the machine
        // is down, and when you click on it the bar must open red, not "Connecting" for another
        // ten seconds while the dot beside it is already red.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint down = RemoteAt("down");
        FakeClock clock = new();

        MainViewModel viewModel = new(
            client: new RespondingClient(local),
            configurationProblem: null,
            clock: clock.Now,
            machineList: new MachineListResult([local, down], []),
            openMachine: endpoint => new UnreachableClient(endpoint));

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
        Task loop = viewModel.RunAsync(cancellation.Token);

        while (!cancellation.IsCancellationRequested && !viewModel.Machines[1].IsWarning)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        clock.Advance(MainViewModel.StatusRefreshInterval + TimeSpan.FromSeconds(1));

        while (!cancellation.IsCancellationRequested && !viewModel.Machines[1].IsFaulted)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[1].IsFaulted, viewModel.Machines[1].Detail);

        viewModel.SelectedMachine = viewModel.Machines[1];

        while (!cancellation.IsCancellationRequested && viewModel.StatusTitle == "Connecting")
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Equal(FAInfoBarSeverity.Error, viewModel.StatusSeverity);
        Assert.True(viewModel.Machines[1].IsFaulted);

        await Stop(cancellation, loop);
    }

    [Fact]
    public async Task TheDotOfTheWatchedMachineFollowsTheStatusBarEvenWhenItFails()
    {
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        FakeClock clock = new();

        MainViewModel viewModel = new(
            client: new UnreachableClient(local),
            configurationProblem: null,
            clock: clock.Now,
            machineList: new MachineListResult([local], []));

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(cancellation.Token);

        while (!cancellation.IsCancellationRequested && viewModel.Machines[0].IsUnknown)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[0].IsWarning, viewModel.Machines[0].Detail);
        Assert.Equal(FAInfoBarSeverity.Informational, viewModel.StatusSeverity);

        clock.Advance(StatusEscalation.GracePeriod + TimeSpan.FromSeconds(1));

        while (!cancellation.IsCancellationRequested && !viewModel.Machines[0].IsFaulted)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(viewModel.Machines[0].IsFaulted, viewModel.Machines[0].Detail);
        Assert.Equal(FAInfoBarSeverity.Error, viewModel.StatusSeverity);

        await Stop(cancellation, loop);
    }

    private static async Task Stop(CancellationTokenSource cancellation, Task loop)
    {
        await cancellation.CancelAsync();

        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
            // End of the test.
        }
    }

    private sealed class RespondingClient(ObserverEndpoint endpoint) : IMetricsClient
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

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));
    }

    /// <summary>Answers well and counts how many times it has been read.</summary>
    private sealed class CountingClient(ObserverEndpoint endpoint) : IMetricsClient
    {
        private int reads;

        public int Reads => Volatile.Read(ref reads);

        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref reads);

            return Task.FromResult(new SnapshotFetch(
                ServiceOutcome.Ok,
                string.Empty,
                new MachineSnapshot(
                    MachineSnapshot.CurrentSchemaVersion,
                    DateTimeOffset.UnixEpoch,
                    [new MetricSnapshot("cpu", CollectorStatus.Ok, null, [])])));
        }

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Ok, string.Empty, MetricCatalog.Empty));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));
    }

    /// <summary>Does not answer until the test calls <see cref="Respond"/>.</summary>
    private sealed class HangingClient(ObserverEndpoint endpoint) : IMetricsClient
    {
        private readonly TaskCompletionSource<SnapshotFetch> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ObserverEndpoint Endpoint { get; } = endpoint;

        public void Respond(SnapshotFetch fetch) => pending.TrySetResult(fetch);

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) => pending.Task;

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Unreachable, "slow", null));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Unreachable, "slow", null));
    }

    /// <summary>Throws instead of answering: a client that fails to build, a DNS that blows up.</summary>
    private sealed class ThrowingClient(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    /// <summary>A service that rejects the token.</summary>
    private sealed class TokenRejectedClient(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.TokenRejected, "rejected", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.TokenRejected, "rejected", null));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.TokenRejected, "rejected", null));
    }

    private sealed class UnreachableClient(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.Unreachable, "down", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Unreachable, "down", null));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Unreachable, "down", null));
    }
}