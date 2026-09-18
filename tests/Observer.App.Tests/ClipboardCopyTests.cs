using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>
/// Portare via un numero o un messaggio d'errore senza ricopiarlo a mano.
/// </summary>
/// <remarks>
/// Nessuna scritta della finestra e' selezionabile, ed e' una decisione presa e non una
/// dimenticanza. Rendere selezionabili le celle costerebbe il gesto che conta: un
/// SelectableTextBlock si prende il PointerPressed per cominciare la selezione, e sopra una
/// lista quel clic non arriva piu' alla riga — trascinare su un quadrante ne aprirebbe il
/// pannello, e trascinare su una riga di processo non la selezionerebbe. Al loro posto c'e'
/// un comando esplicito, in due punti soli: la barra di stato, che e' il caso che pesa perche'
/// un'impronta sbagliata stampa due impronte intere, e la riga di processo selezionata.
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

        // La stringa intera, non un Contains: con "22" il numero potrebbe arrivare da una
        // percentuale o da un conteggio di megabyte, e la prova resterebbe verde col PID fuori.
        Assert.Equal("tranquillo (pid 22), CPU 1.0 %, memory 10 MiB, I/O —", clipboard.LastText);
    }

    [Fact]
    public void ThePidIsInTheClipboardTextButNotInTheAccessibleName()
    {
        // Cio' che un lettore di schermo pronuncia a OGNI freccia sull'elenco: un numero di
        // cinque cifre letto cifra per cifra a ogni riga e' rumore fra chi scorre e cio' che
        // sta cercando. Due frasi quasi identiche, e la differenza e' voluta.
        ProcessRowState row = new(31337, "claude", "15.1 %", "228.5 MiB", "1.1 MiB/s");

        Assert.DoesNotContain("pid", row.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain("31337", row.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("31337", row.ForClipboard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoRowSelectedCopyDoesNotTouchTheClipboard()
    {
        // Ctrl+C su un elenco senza selezione non deve svuotare gli appunti di chi stava
        // copiando qualcos'altro.
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
        // La cucitura e' opzionale perche' una prova senza finestra non ce l'ha. Se un giorno
        // sparisse dalla radice di composizione, un comando che esce da se' sul null
        // lascerebbe un pulsante muto che nessun test vedrebbe, perche' i test il finto ce
        // l'hanno. Spento si vede al primo avvio.
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
        // Gli appunti possono essere tenuti da un altro programma. L'unico posto dove dirlo
        // sarebbe la barra di stato, cioe' proprio cio' che si sta copiando: raccontare il
        // guasto vorrebbe dire perdere il testo per cui si e' premuto il pulsante.
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
        // Il difetto gia' pagato dai sei pulsanti dei quadranti: un AsyncRelayCommand in
        // esecuzione si disabilita e rifiuta ogni altra chiamata, quindi il secondo clic
        // cadrebbe nel vuoto con il pulsante che lampeggia spento.
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
