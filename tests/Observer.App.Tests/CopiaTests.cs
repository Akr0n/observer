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
public class CopiaTests
{
    [Fact]
    public async Task CopiareLaBarraDiStatoPrendeTitoloEMessaggio()
    {
        Appunti appunti = new();
        MainViewModel viewModel = new(
            client: null,
            problemaDiConfigurazione: "client.json is missing",
            copiaNegliAppunti: appunti.Scrivi);

        await viewModel.CopiaStatoCommand.ExecuteAsync(null);

        Assert.Equal(
            viewModel.StatoTitolo + Environment.NewLine + viewModel.StatoMessaggio,
            appunti.Ultimo);
        Assert.Contains("client.json", appunti.Ultimo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopiareLaRigaDiProcessoPrendeIlSuoPid()
    {
        Appunti appunti = new();
        MainViewModel viewModel = new(
            client: null,
            problemaDiConfigurazione: null,
            copiaNegliAppunti: appunti.Scrivi)
        {
            ProcessoSelezionato = new ProcessoMostrato(22, "tranquillo", "1.0 %", "10 MiB"),
        };

        await viewModel.CopiaProcessoCommand.ExecuteAsync(null);

        // La stringa intera, non un Contains: con "22" il numero potrebbe arrivare da una
        // percentuale o da un conteggio di megabyte, e la prova resterebbe verde col PID fuori.
        Assert.Equal("tranquillo (pid 22), CPU 1.0 %, memory 10 MiB, I/O —", appunti.Ultimo);
    }

    [Fact]
    public void IlPidNonEntraNelNomeAccessibileDellaRiga()
    {
        // Cio' che un lettore di schermo pronuncia a OGNI freccia sull'elenco: un numero di
        // cinque cifre letto cifra per cifra a ogni riga e' rumore fra chi scorre e cio' che
        // sta cercando. Due frasi quasi identiche, e la differenza e' voluta.
        ProcessoMostrato riga = new(31337, "claude", "15.1 %", "228.5 MiB", "1.1 MiB/s");

        Assert.DoesNotContain("pid", riga.Descrizione, StringComparison.Ordinal);
        Assert.DoesNotContain("31337", riga.Descrizione, StringComparison.Ordinal);
        Assert.Contains("31337", riga.PerGliAppunti, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SenzaRigaSelezionataLaCopiaNonToccaGliAppunti()
    {
        // Ctrl+C su un elenco senza selezione non deve svuotare gli appunti di chi stava
        // copiando qualcos'altro.
        Appunti appunti = new();
        MainViewModel viewModel = new(
            client: null,
            problemaDiConfigurazione: null,
            copiaNegliAppunti: appunti.Scrivi);

        Assert.False(viewModel.CopiaProcessoCommand.CanExecute(null));

        await viewModel.CopiaProcessoCommand.ExecuteAsync(null);

        Assert.Equal(0, appunti.Quante);
    }

    [Fact]
    public void SenzaAppuntiCollegatiIComandiSonoSpenti()
    {
        // La cucitura e' opzionale perche' una prova senza finestra non ce l'ha. Se un giorno
        // sparisse dalla radice di composizione, un comando che esce da se' sul null
        // lascerebbe un pulsante muto che nessun test vedrebbe, perche' i test il finto ce
        // l'hanno. Spento si vede al primo avvio.
        MainViewModel viewModel = new(client: null, problemaDiConfigurazione: "qualcosa")
        {
            ProcessoSelezionato = new ProcessoMostrato(1, "x", "0 %", "1 MiB"),
        };

        Assert.False(viewModel.PuoCopiare);
        Assert.False(viewModel.CopiaStatoCommand.CanExecute(null));
        Assert.False(viewModel.CopiaProcessoCommand.CanExecute(null));
    }

    [Fact]
    public async Task UnGuastoDegliAppuntiNonCancellaIlMessaggioCheSiStavaCopiando()
    {
        // Gli appunti possono essere tenuti da un altro programma. L'unico posto dove dirlo
        // sarebbe la barra di stato, cioe' proprio cio' che si sta copiando: raccontare il
        // guasto vorrebbe dire perdere il testo per cui si e' premuto il pulsante.
        MainViewModel viewModel = new(
            client: null,
            problemaDiConfigurazione: "client.json is missing",
            copiaNegliAppunti: _ => throw new InvalidOperationException("appunti occupati"));

        string titolo = viewModel.StatoTitolo;
        string messaggio = viewModel.StatoMessaggio;

        await viewModel.CopiaStatoCommand.ExecuteAsync(null);

        Assert.Equal(titolo, viewModel.StatoTitolo);
        Assert.Equal(messaggio, viewModel.StatoMessaggio);
    }

    [Fact]
    public async Task UnSecondoClicNonVieneScartatoMentreIlPrimoEInVolo()
    {
        // Il difetto gia' pagato dai sei pulsanti dei quadranti: un AsyncRelayCommand in
        // esecuzione si disabilita e rifiuta ogni altra chiamata, quindi il secondo clic
        // cadrebbe nel vuoto con il pulsante che lampeggia spento.
        TaskCompletionSource appeso = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Appunti appunti = new();
        MainViewModel viewModel = new(
            client: null,
            problemaDiConfigurazione: "qualcosa",
            copiaNegliAppunti: async testo =>
            {
                await appeso.Task;
                await appunti.Scrivi(testo);
            });

        Task primo = viewModel.CopiaStatoCommand.ExecuteAsync(null);

        Assert.True(viewModel.CopiaStatoCommand.CanExecute(null));

        Task secondo = viewModel.CopiaStatoCommand.ExecuteAsync(null);

        appeso.SetResult();
        await primo;
        await secondo;

        Assert.Equal(2, appunti.Quante);
    }

    private sealed class Appunti
    {
        public string Ultimo { get; private set; } = string.Empty;

        public int Quante { get; private set; }

        public Task Scrivi(string testo)
        {
            Ultimo = testo;
            Quante++;

            return Task.CompletedTask;
        }
    }
}
