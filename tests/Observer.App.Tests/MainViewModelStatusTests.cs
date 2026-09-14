using FluentAvalonia.UI.Controls;
using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>
/// Che l'attesa provata in <see cref="StatusEscalationTests"/> sia davvero cablata nel ciclo.
/// </summary>
/// <remarks>
/// Senza questa classe la tabella dell'escalation potrebbe essere perfetta e la finestra
/// continuare ad aprirsi rossa: basterebbe che il view model passasse sempre zero come durata,
/// e nessun test puro se ne accorgerebbe. Qui si guarda cio' che si vede a schermo.
/// </remarks>
public class MainViewModelStatusTests
{
    [Fact]
    public async Task LaFinestraCheSiApreMentreIlServizioParte_NonMostraUnaBarraRossa()
    {
        // Il difetto misurato, riprodotto: il servizio non risponde ancora, e la finestra
        // e' appena stata aperta.
        OrologioFinto clock = new();
        MainViewModel viewModel = Costruisci(ServiceOutcome.Unreachable, clock);

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.RunAsync(arresto.Token);

        await Attendi(viewModel, "Waiting for", arresto.Token);

        Assert.Equal(FAInfoBarSeverity.Informational, viewModel.StatusSeverity);
        Assert.Equal("Connecting", viewModel.StatusTitle);

        await Chiudi(arresto, ciclo);
    }

    [Fact]
    public async Task SeIlServizioNonRispondeAncoraDopoLaTolleranza_LaBarraDiventaRossa()
    {
        // Il gemello obbligatorio del test sopra: rimandare l'allarme non deve significare
        // sopprimerlo. Un servizio che non c'e' va detto, e va detto in rosso.
        OrologioFinto clock = new();
        MainViewModel viewModel = Costruisci(ServiceOutcome.Unreachable, clock);

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.RunAsync(arresto.Token);

        await Attendi(viewModel, "Waiting for", arresto.Token);

        clock.Avanza(StatusEscalation.GracePeriod + TimeSpan.FromSeconds(1));

        await Attendi(viewModel, "isn't answering", arresto.Token);

        Assert.Equal(FAInfoBarSeverity.Error, viewModel.StatusSeverity);
        Assert.Equal("Service unreachable", viewModel.StatusTitle);

        await Chiudi(arresto, ciclo);
    }

    [Fact]
    public async Task UnTokenRifiutatoNonAspetta_ERossoDalPrimoTentativo()
    {
        // L'attesa vale solo dove aspettare puo' cambiare l'esito. Un token sbagliato sara'
        // sbagliato anche fra dieci secondi: rimandare l'allarme rimanderebbe solo il momento
        // in cui l'utente puo' correggerlo.
        OrologioFinto clock = new();
        MainViewModel viewModel = Costruisci(ServiceOutcome.TokenRejected, clock);

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.RunAsync(arresto.Token);

        await Attendi(viewModel, "rejected the token", arresto.Token);

        Assert.Equal(FAInfoBarSeverity.Error, viewModel.StatusSeverity);

        await Chiudi(arresto, ciclo);
    }

    private static MainViewModel Costruisci(ServiceOutcome esito, OrologioFinto clock) =>
        new(
            new FakeMetricsClient(esito),
            configurationProblem: null,
            rereadConfiguration: null,
            clock: clock.Adesso);

    private static async Task Attendi(MainViewModel viewModel, string atteso, CancellationToken arresto)
    {
        while (!arresto.IsCancellationRequested
            && !viewModel.StatusText.Contains(atteso, StringComparison.Ordinal))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Contains(atteso, viewModel.StatusText, StringComparison.Ordinal);
    }

    private static async Task Chiudi(CancellationTokenSource arresto, Task ciclo)
    {
        await arresto.CancelAsync();
        await ciclo.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    /// <summary>Un client che fallisce sempre allo stesso modo.</summary>
    private sealed class FakeMetricsClient(ServiceOutcome esito) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.LocalChannel();

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(esito, Testo(esito), null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(esito, Testo(esito), null));
        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(esito, Testo(esito), null));

        // Ricalca le frasi vere di MetricsClient: i test aspettano cio' che si vede a schermo,
        // e una frase inventata qui renderebbe l'attesa una tautologia.
        private static string Testo(ServiceOutcome esito) => esito switch
        {
            ServiceOutcome.TokenRejected =>
                "The service on this machine rejected the token (401).",
            _ =>
                "The Observer service isn't answering on this machine. Check that it is running.",
        };
    }
}