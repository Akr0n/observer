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
public class MacchinaRicordataTests
{
    [Fact]
    public void SiRicordaLaMacchinaCheSiStavaGuardando()
    {
        ObserverEndpoint locale = ObserverEndpoint.CanaleLocale();
        ObserverEndpoint remota = Remota("laptop");

        MainViewModel viewModel = new(
            client: new ClientMuto(remota),
            problemaDiConfigurazione: null,
            elenco: new MachineListResult([locale, remota], []));

        Assert.Equal("laptop", viewModel.MacchinaDaRicordare);
    }

    [Fact]
    public void SulCanaleLocaleNonSiRicordaNiente()
    {
        // Null nel file vuol dire "questo computer", ed e' anche cio' che si legge in un file
        // scritto da una versione precedente: nessuna migrazione da fare.
        ObserverEndpoint locale = ObserverEndpoint.CanaleLocale();

        MainViewModel viewModel = new(
            client: new ClientMuto(locale),
            problemaDiConfigurazione: null,
            elenco: new MachineListResult([locale], []));

        Assert.Null(viewModel.MacchinaDaRicordare);
    }

    [Fact]
    public void UnaMacchinaSenzaNomeNonFinisceNelFile()
    {
        // La vecchia configurazione a macchina singola (client.json, Observer__BaseAddress)
        // produce un punto remoto SENZA nome. Il nome visibile in quel caso ripiega
        // sull'INDIRIZZO, e un indirizzo in preferences.json sarebbe un dato di rete scritto
        // dove non deve stare, per giunta inutile: non e' una chiave di machines.json.
        ObserverEndpoint senzaNome = ObserverEndpoint.Remoto(
            new Uri("https://10.0.0.9:5058/"), "token", "client.json");

        MainViewModel viewModel = new(
            client: new ClientMuto(senzaNome),
            problemaDiConfigurazione: null,
            elenco: new MachineListResult([senzaNome], []));

        Assert.Null(viewModel.MacchinaDaRicordare);
    }

    private static ObserverEndpoint Remota(string nome) =>
        ObserverEndpoint.Remoto(
            new Uri($"https://{nome}:5058/"),
            "token",
            "machines.json",
            new string('a', 64),
            nome);

    /// <summary>Un client che non risponde mai: qui interessa solo da dove si e' partiti.</summary>
    private sealed class ClientMuto(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.NonRaggiungibile, "muto", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.NonRaggiungibile, "muto", null));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery richiesta, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.NonRaggiungibile, "muto", null));
    }
}
