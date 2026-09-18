using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>
/// Taking a number or an error message away without retyping it by hand.
/// </summary>
/// <remarks>
/// No text in the window is selectable, and that is a decision that was taken, not an
/// oversight. Making the cells selectable would cost the gesture that matters: a
/// SelectableTextBlock takes the PointerPressed that starts a selection, and over a list that
/// click no longer reaches the row — dragging over a gauge would open its panel, and dragging
/// over a process row would not select it. In their place there is an explicit command, in two
/// places only: the status bar, which is the case that carries weight because a wrong
/// fingerprint prints two whole fingerprints, and the selected process row.
/// </remarks>
public class ClipboardCopyTests
{
    [Fact]
    public async Task CopyingTheStatusBarTakesTitleAndMessage()
    {
        FakeClipboard clipboard = new();
        MainViewModel viewModel = new(
            client: null,
            configurationProblem: "client.json is missing",
            copyToClipboard: clipboard.Write);

        await viewModel.CopyStatusCommand.ExecuteAsync(null);

        Assert.Equal(
            viewModel.StatusTitle + Environment.NewLine + viewModel.StatusText,
            clipboard.LastText);
        Assert.Contains("client.json", clipboard.LastText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyingTheProcessRowTakesItsPid()
    {
        FakeClipboard clipboard = new();
        MainViewModel viewModel = new(
            client: null,
            configurationProblem: null,
            copyToClipboard: clipboard.Write)
        {
            SelectedProcess = new ProcessRowState(22, "tranquillo", "1.0 %", "10 MiB"),
        };

        await viewModel.CopyProcessRowCommand.ExecuteAsync(null);

        // The whole string, not a Contains: with "22" the number could come from a percentage
        // or from a megabyte count, and the test would stay green with the PID left out.
        Assert.Equal("tranquillo (pid 22), CPU 1.0 %, memory 10 MiB, I/O —", clipboard.LastText);
    }

    [Fact]
    public void ThePidIsInTheClipboardTextButNotInTheAccessibleName()
    {
        // What a screen reader says out loud at EVERY arrow key down the list: a five-digit
        // number read digit by digit on every row is noise between whoever is scrolling and
        // what they are looking for. Two almost identical sentences, and the difference is
        // deliberate.
        ProcessRowState row = new(31337, "claude", "15.1 %", "228.5 MiB", "1.1 MiB/s");

        Assert.DoesNotContain("pid", row.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain("31337", row.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("31337", row.ForClipboard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoRowSelectedCopyDoesNotTouchTheClipboard()
    {
        // Ctrl+C on a list with no selection must not empty the clipboard of someone who was
        // copying something else.
        FakeClipboard clipboard = new();
        MainViewModel viewModel = new(
            client: null,
            configurationProblem: null,
            copyToClipboard: clipboard.Write);

        Assert.False(viewModel.CopyProcessRowCommand.CanExecute(null));

        await viewModel.CopyProcessRowCommand.ExecuteAsync(null);

        Assert.Equal(0, clipboard.WriteCount);
    }

    [Fact]
    public void WithNoClipboardWiredTheCommandsAreDisabled()
    {
        // The wiring is optional because a test with no window does not have it. If one day it
        // disappeared from the composition root, a command that simply returns on a null would
        // leave a button that does nothing and that no test would see, because the tests do
        // have the fake. A disabled button is visible at the first start.
        MainViewModel viewModel = new(client: null, configurationProblem: "qualcosa")
        {
            SelectedProcess = new ProcessRowState(1, "x", "0 %", "1 MiB"),
        };

        Assert.False(viewModel.CanCopy);
        Assert.False(viewModel.CopyStatusCommand.CanExecute(null));
        Assert.False(viewModel.CopyProcessRowCommand.CanExecute(null));
    }

    [Fact]
    public async Task AClipboardFailureDoesNotEraseTheMessageBeingCopied()
    {
        // The clipboard can be held by another program. The only place to say so would be the
        // status bar, which is exactly what is being copied: reporting the failure would mean
        // losing the very text the button was pressed for.
        MainViewModel viewModel = new(
            client: null,
            configurationProblem: "client.json is missing",
            copyToClipboard: _ => throw new InvalidOperationException("appunti occupati"));

        string title = viewModel.StatusTitle;
        string message = viewModel.StatusText;

        await viewModel.CopyStatusCommand.ExecuteAsync(null);

        Assert.Equal(title, viewModel.StatusTitle);
        Assert.Equal(message, viewModel.StatusText);
    }

    [Fact]
    public async Task ASecondClickIsNotDroppedWhileTheFirstIsInFlight()
    {
        // The defect already paid for by the six gauge buttons: an AsyncRelayCommand that is
        // running disables itself and refuses every other call, so the second click would fall
        // into nothing with the button flickering disabled.
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeClipboard clipboard = new();
        MainViewModel viewModel = new(
            client: null,
            configurationProblem: "qualcosa",
            copyToClipboard: async text =>
            {
                await gate.Task;
                await clipboard.Write(text);
            });

        Task first = viewModel.CopyStatusCommand.ExecuteAsync(null);

        Assert.True(viewModel.CopyStatusCommand.CanExecute(null));

        Task second = viewModel.CopyStatusCommand.ExecuteAsync(null);

        gate.SetResult();
        await first;
        await second;

        Assert.Equal(2, clipboard.WriteCount);
    }

    private sealed class FakeClipboard
    {
        public string LastText { get; private set; } = string.Empty;

        public int WriteCount { get; private set; }

        public Task Write(string text)
        {
            LastText = text;
            WriteCount++;

            return Task.CompletedTask;
        }
    }
}
