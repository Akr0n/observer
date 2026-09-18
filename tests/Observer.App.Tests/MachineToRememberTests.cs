using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>
/// Which machine the window reopens on, and which name ends up in the file.
/// </summary>
/// <remarks>
/// That the sidebar highlights the machine the window was built with is already pinned by
/// <c>MachineStatusTests.TheWatchedMachineIsNotProbedEvenWhenItIsRemote</c>. Here the other
/// half is tested: what gets written to the file on close, which is the only part this round
/// adds to the view model.
/// </remarks>
public class MachineToRememberTests
{
    [Fact]
    public void ItRemembersTheMachineItWasWatching()
    {
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint remote = RemoteEndpoint("laptop");

        MainViewModel viewModel = new(
            client: new SilentClient(remote),
            configurationProblem: null,
            machineList: new MachineListResult([local, remote], []));

        Assert.Equal("laptop", viewModel.MachineToRemember);
    }

    [Fact]
    public void OnTheLocalChannelNothingIsRemembered()
    {
        // Null in the file means "this computer", and it is also what is read from a file
        // written by an earlier version: no migration to do.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();

        MainViewModel viewModel = new(
            client: new SilentClient(local),
            configurationProblem: null,
            machineList: new MachineListResult([local], []));

        Assert.Null(viewModel.MachineToRemember);
    }

    [Fact]
    public void ADeselectionDoesNotForgetTheMachine()
    {
        // It remembers the machine the loop is really READING, not the highlighted one: the
        // selection can become null while the reading goes on, and it is the same distinction
        // for which the view model keeps watchedEntry separate from the selection.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint remote = RemoteEndpoint("laptop");

        MainViewModel viewModel = new(
            client: new SilentClient(remote),
            configurationProblem: null,
            machineList: new MachineListResult([local, remote], []));

        viewModel.SelectedMachine = null;

        Assert.Equal("laptop", viewModel.MachineToRemember);
    }

    [Fact]
    public void SwitchingMachineMidSessionRemembersTheLastOne()
    {
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint remote = RemoteEndpoint("laptop");

        MainViewModel viewModel = new(
            client: new SilentClient(local),
            configurationProblem: null,
            machineList: new MachineListResult([local, remote], []),
            openMachine: endpoint => new SilentClient(endpoint));

        Assert.Null(viewModel.MachineToRemember);

        viewModel.SelectedMachine = viewModel.Machines.Single(row => row.Endpoint == remote);
        Assert.Equal("laptop", viewModel.MachineToRemember);

        // And going back to this computer goes back to remembering nothing, which is what the
        // null in the file means. Without it, whoever moves from the remote machine to the
        // local one would find the remote one reopened for ever.
        viewModel.SelectedMachine = viewModel.Machines[0];
        Assert.Null(viewModel.MachineToRemember);
    }

    [Fact]
    public void AMachineWithoutANameDoesNotReachTheFile()
    {
        // The old single-machine configuration (client.json, Observer__BaseAddress) produces a
        // remote endpoint with NO name. The display name in that case falls back to the
        // ADDRESS, and an address in preferences.json would be network data written where it
        // must not be, and useless on top of that: it is not a key of machines.json.
        ObserverEndpoint unnamed = ObserverEndpoint.Remote(
            new Uri("https://10.0.0.9:5058/"), "token", "client.json");

        MainViewModel viewModel = new(
            client: new SilentClient(unnamed),
            configurationProblem: null,
            machineList: new MachineListResult([unnamed], []));

        Assert.Null(viewModel.MachineToRemember);
    }

    private static ObserverEndpoint RemoteEndpoint(string name) =>
        ObserverEndpoint.Remote(
            new Uri($"https://{name}:5058/"),
            "token",
            "machines.json",
            new string('a', 64),
            name);

    /// <summary>A client that never answers: only where it started from matters here.</summary>
    private sealed class SilentClient(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.Unreachable, "muto", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Unreachable, "muto", null));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Unreachable, "muto", null));
    }
}
