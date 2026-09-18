using FluentAvalonia.UI.Controls;
using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>
/// That the grace proven in <see cref="StatusEscalationTests"/> is really wired into the loop.
/// </summary>
/// <remarks>
/// Without this class the escalation table could be perfect and the window still keep opening
/// red: it would be enough for the view model to always pass zero as the duration, and no pure
/// test would notice. This class checks what actually shows on screen.
/// </remarks>
public class MainViewModelStatusTests
{
    [Fact]
    public async Task TheWindowOpeningWhileTheServiceStarts_ShowsNoRedBar()
    {
        // The measured defect, reproduced: the service is not answering yet, and the window
        // has just been opened.
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
        // The mandatory twin of the test above: postponing the alarm must not mean suppressing
        // it. A service that is not there has to be reported, and reported in red.
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
        // The grace applies only where waiting can change the outcome. A wrong token will still
        // be wrong ten seconds from now: postponing the alarm would only postpone the moment
        // the user can correct it.
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

    /// <summary>A client that always fails in the same way.</summary>
    private sealed class FakeMetricsClient(ServiceOutcome outcome) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.LocalChannel();

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(outcome, MessageFor(outcome), null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(outcome, MessageFor(outcome), null));
        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(outcome, MessageFor(outcome), null));

        // Mirrors the real sentences of MetricsClient: the tests wait for what shows on screen,
        // and a made-up sentence here would turn that wait into a tautology.
        private static string MessageFor(ServiceOutcome outcome) => outcome switch
        {
            ServiceOutcome.TokenRejected =>
                "The service on this machine rejected the token (401).",
            _ =>
                "The Observer service isn't answering on this machine. Check that it is running.",
        };
    }
}