using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>
/// Quale macchina la finestra riapre, e quale nome finisce nel file.
/// </summary>
/// <remarks>
/// Che la barra laterale evidenzi la macchina con cui la finestra e' stata costruita lo pinna
/// gia' <c>StatoMacchineTests.LaMacchinaGuardataNonVieneSondataAncheSeRemota</c>. Qui si prova
/// l'altra meta': cosa si scrive nel file alla chiusura, che e' l'unica parte che questo giro
/// aggiunge al view model.
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
        // Null nel file vuol dire "questo computer", ed e' anche cio' che si legge in un file
        // scritto da una versione precedente: nessuna migrazione da fare.
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
        // Si ricorda la macchina che il giro sta davvero LEGGENDO, non quella evidenziata: la
        // selezione puo' diventare nulla mentre la lettura continua, ed e' la stessa
        // distinzione per cui il view model tiene watchedEntry separata dalla selezione.
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

        // E tornando su questo computer si torna a non ricordare niente, che e' cio' che il
        // null nel file vuol dire. Senza, chi passa dalla remota alla locale si ritroverebbe
        // la remota riaperta per sempre.
        viewModel.SelectedMachine = viewModel.Machines[0];
        Assert.Null(viewModel.MachineToRemember);
    }

    [Fact]
    public void AMachineWithoutANameDoesNotReachTheFile()
    {
        // La vecchia configurazione a macchina singola (client.json, Observer__BaseAddress)
        // produce un punto remoto SENZA nome. Il nome visibile in quel caso ripiega
        // sull'INDIRIZZO, e un indirizzo in preferences.json sarebbe un dato di rete scritto
        // dove non deve stare, per giunta inutile: non e' una chiave di machines.json.
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

    /// <summary>Un client che non risponde mai: qui interessa solo da dove si e' partiti.</summary>
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
