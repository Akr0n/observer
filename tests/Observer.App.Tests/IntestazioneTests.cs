using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// Che cosa dice la riga sotto il titolo.
/// </summary>
/// <remarks>
/// Dice <b>dove</b> e <b>quando</b>: quale macchina si sta guardando e a che ora e' arrivata
/// l'ultima lettura. Non dice come ci si e' entrati. Da dove viene il token e' una nota di
/// configurazione — serve quando qualcosa non va e bisogna sapere quale file correggere, ed e'
/// li' che sta, nel messaggio dell'impronta che non corrisponde — ma a regime e' una frase che
/// si rilegge a ogni sguardo senza mai cambiare.
/// </remarks>
public class IntestazioneTests
{
    [Fact]
    public async Task GuardandoUnaMacchinaRemotaLIntestazioneNonNominaIlToken()
    {
        ObserverEndpoint remota = ObserverEndpoint.Remoto(
            new Uri("https://altra:5058/"), "il-token", "dal file delle macchine", new string('a', 64));

        MainViewModel viewModel = new(new ClientCheRisponde(remota), problemaDiConfigurazione: null);

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        while (!arresto.IsCancellationRequested
            && !viewModel.SottoIntestazione.StartsWith("Connected", StringComparison.Ordinal))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Contains("last reading at", viewModel.SottoIntestazione, StringComparison.Ordinal);
        Assert.DoesNotContain("token", viewModel.SottoIntestazione, StringComparison.OrdinalIgnoreCase);

        await arresto.CancelAsync();

        try
        {
            await ciclo;
        }
        catch (OperationCanceledException)
        {
            // Fine del test.
        }
    }

    private sealed class ClientCheRisponde(ObserverEndpoint endpoint) : IMetricsClient
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

        public Task<HistoryFetch> GetHistoryAsync(
            HistoryQuery richiesta,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));
    }
}