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
    public async Task TheWindowOpeningWhileTheServiceStarts_ShowsNoRedBar()
    {
        // Il difetto misurato, riprodotto: il servizio non risponde ancora, e la finestra
        // e' appena stata aperta.
        FakeClock clock = new();
        MainViewModel viewModel = Build(ServiceOutcome.Unreachable, clock);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(stop.Token);

        await WaitForStatus(viewModel, "Waiting for", stop.Token);

        Assert.Equal(FAInfoBarSeverity.Informational, viewModel.StatusSeverity);
        Assert.Equal("Connecting", viewModel.StatusTitle);

        await Shutdown(stop, loop);
    }

    [Fact]
    public async Task IfTheServiceStillDoesNotAnswerAfterTheGracePeriod_TheBarTurnsRed()
    {
        // Il gemello obbligatorio del test sopra: rimandare l'allarme non deve significare
        // sopprimerlo. Un servizio che non c'e' va detto, e va detto in rosso.
        FakeClock clock = new();
        MainViewModel viewModel = Build(ServiceOutcome.Unreachable, clock);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(stop.Token);

        await WaitForStatus(viewModel, "Waiting for", stop.Token);

        clock.Advance(StatusEscalation.GracePeriod + TimeSpan.FromSeconds(1));

        await WaitForStatus(viewModel, "isn't answering", stop.Token);

        Assert.Equal(FAInfoBarSeverity.Error, viewModel.StatusSeverity);
        Assert.Equal("Service unreachable", viewModel.StatusTitle);

        await Shutdown(stop, loop);
    }

    [Fact]
    public async Task ARejectedTokenDoesNotWait_ItIsRedFromTheFirstAttempt()
    {
        // L'attesa vale solo dove aspettare puo' cambiare l'esito. Un token sbagliato sara'
        // sbagliato anche fra dieci secondi: rimandare l'allarme rimanderebbe solo il momento
        // in cui l'utente puo' correggerlo.
        FakeClock clock = new();
        MainViewModel viewModel = Build(ServiceOutcome.TokenRejected, clock);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        Task loop = viewModel.RunAsync(stop.Token);

        await WaitForStatus(viewModel, "rejected the token", stop.Token);

        Assert.Equal(FAInfoBarSeverity.Error, viewModel.StatusSeverity);

        await Shutdown(stop, loop);
    }

    private static MainViewModel Build(ServiceOutcome outcome, FakeClock clock) =>
        new(
            new FakeMetricsClient(outcome),
            configurationProblem: null,
            rereadConfiguration: null,
            clock: clock.Now);

    private static async Task WaitForStatus(MainViewModel viewModel, string expected, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested
            && !viewModel.StatusText.Contains(expected, StringComparison.Ordinal))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Contains(expected, viewModel.StatusText, StringComparison.Ordinal);
    }

    private static async Task Shutdown(CancellationTokenSource stop, Task loop)
    {
        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    /// <summary>Un client che fallisce sempre allo stesso modo.</summary>
    private sealed class FakeMetricsClient(ServiceOutcome outcome) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.LocalChannel();

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(outcome, MessageFor(outcome), null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(outcome, MessageFor(outcome), null));
        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(outcome, MessageFor(outcome), null));

        // Ricalca le frasi vere di MetricsClient: i test aspettano cio' che si vede a schermo,
        // e una frase inventata qui renderebbe l'attesa una tautologia.
        private static string MessageFor(ServiceOutcome outcome) => outcome switch
        {
            ServiceOutcome.TokenRejected =>
                "The service on this machine rejected the token (401).",
            _ =>
                "The Observer service isn't answering on this machine. Check that it is running.",
        };
    }
}