using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Observer.App.Services;
using Observer.Core.Metrics;

namespace Observer.App.ViewModels;

/// <summary>
/// L'unica schermata: interroga il servizio una volta al secondo e mostra cio' che risponde.
/// </summary>
/// <remarks>
/// Regola non negoziabile di questa classe: non lascia MAI la finestra vuota e non lascia mai
/// uscire un'eccezione. Chi usa questa applicazione non legge i log, quindi ogni guasto deve
/// diventare una frase in italiano dentro la barra di stato.
/// </remarks>
public sealed partial class MainViewModel : ViewModelBase
{
    /// <summary>Ogni quanto si interroga il servizio.</summary>
    /// <remarks>
    /// Pubblico perche' un test possa confrontarlo con <see cref="Controls.Gauge.Corsa"/>: la
    /// corsa della lancetta deve restare piu' breve di questo, altrimenti non finirebbe mai e
    /// il quadrante non starebbe fermo su un valore misurato nemmeno per un istante.
    /// </remarks>
    public static readonly TimeSpan Intervallo = TimeSpan.FromSeconds(1);

    /// <summary>Ogni quanto si interroga il servizio quando la finestra e' ridotta a icona.</summary>
    /// <remarks>
    /// Non si ferma: riaprendo la finestra la barra di stato deve dire subito com'e' andata,
    /// non "collegamento in corso". Ma un campione al secondo per una finestra che nessuno
    /// guarda e' lavoro fatto alla macchina che si sta misurando, e questo e' uno strumento
    /// che rientra nel numero che mostra.
    /// </remarks>
    public static readonly TimeSpan IntervalloRidotto = TimeSpan.FromSeconds(10);

    /// <summary>Ogni quanto si riprova una lettura di storico fallita.</summary>
    /// <remarks>
    /// Non il passo del periodo: a sette giorni quello vale due ore, e un timeout lascerebbe
    /// accanto alla striscia un "No history" vecchio di due ore su dati che intanto sono
    /// tornati. Si riprova presto, e si rallenta solo quando e' andata bene.
    /// </remarks>
    private static readonly TimeSpan RiprovaStorico = TimeSpan.FromSeconds(15);

    /// <summary>In quante riletture si divide un passo, quando la lettura e' andata bene.</summary>
    /// <remarks>
    /// Rileggere OGNI passo sembrava la cadenza giusta - piu' spesso non aggiunge una barra,
    /// aggiunge solo traffico - e non lo era: l'ultima barra della striscia e' l'intervallo IN
    /// CORSO, e da 0.18.0 si disegna larga quanto la parte che ha coperto. Rileggendo ogni
    /// passo si guarda ogni volta una barra appena nata, sempre alla stessa frazione: a sette
    /// giorni l'estremo destro - quello che l'occhio legge come "adesso" - resterebbe una riga
    /// da un pixel per tutta la sessione, accanto a quadranti vivi. Un quarto del passo la fa
    /// crescere in quattro scatti, e resta un trentesimo del traffico della vista da un'ora.
    /// </remarks>
    private const int RiletturePerPasso = 4;

    /// <summary>Il minimo fra due riletture dello storico, quale che sia il periodo.</summary>
    /// <remarks>
    /// Tocca solo la vista da un'ora, il cui passo vale gia' un minuto: li' la barra in corso
    /// resta congelata alla frazione che aveva quando si e' scelto il periodo, e si accetta.
    /// Scenderebbe a quindici secondi, ma sono dodici richieste ogni quindici secondi - una
    /// volta e mezza il campionamento stesso - per animare una barretta da tredici pixel. Il
    /// prezzo lo paga la macchina che questa finestra sta misurando, e compare nel numero che
    /// la finestra mostra. Sui periodi lunghi il quarto di passo costa molto meno di cosi' e
    /// il difetto e' molto piu' grosso: e' li' che si spende.
    /// </remarks>
    private static readonly TimeSpan RiletturaMinima = TimeSpan.FromMinutes(1);

    /// <summary>Il minimo da cui rileggere il grezzo, quale che sia il periodo.</summary>
    /// <remarks>
    /// Il consolidamento degli aggregati ha una grazia di quattro minuti: il livello a un
    /// minuto e' indietro di cinque o sei rispetto ad adesso. Senza questa seconda lettura le
    /// ultime barrette sarebbero SEMPRE vuote, e la striscia direbbe "non misurato" proprio
    /// sull'adesso, mentre il quadrante sopra mostra un valore vivo. Con una sorgente a cinque
    /// minuti il ritardo cresce, e la coda si allarga con lei: vedi CodaDi.
    /// </remarks>
    private static readonly TimeSpan CodaMinima = TimeSpan.FromMinutes(10);

    /// <summary>Quante righe chiedere al pannello dei processi.</summary>
    /// <remarks>
    /// Quindici, non tutti: la domanda a cui il pannello risponde e' "chi mi sta mangiando la
    /// macchina", e la coda dell'elenco - centinaia di processi fermi - non risponde a niente
    /// e costa banda a ogni secondo.
    /// </remarks>
    private const int QuantiProcessi = 15;

    private readonly Func<IMetricsClient?>? rileggiConfigurazione;
    private readonly Func<DateTimeOffset> adesso;

    /// <summary>Come aprire un client verso una macchina scelta nell'elenco.</summary>
    private readonly Func<ObserverEndpoint, IMetricsClient>? apriMacchina;

    /// <summary>Come rileggere da disco la voce di una macchina, quando la sua credenziale non vale piu'.</summary>
    private readonly Func<ObserverEndpoint, ObserverEndpoint?>? rileggiPunto;

    private readonly Func<string, Task>? copiaNegliAppunti;

    /// <summary>L'ultima scrittura negli appunti, per metterci in fila la prossima.</summary>
    private Task codaAppunti = Task.CompletedTask;

    /// <summary>La voce dell'elenco che il giro principale sta leggendo davvero.</summary>
    /// <remarks>
    /// NON la selezione della lista: quella puo' diventare null (un Ctrl+clic sulla voce
    /// evidenziata la deseleziona) mentre il giro continua a leggere la stessa macchina, e
    /// allora la sonda la interrogherebbe una seconda volta e il suo pallino smetterebbe di
    /// seguire la barra. E' questa voce che le sonde saltano e che la barra aggiorna.
    /// </remarks>
    private MacchinaInElenco? voceGuardata;


    private IMetricsClient? client;

    private MetricCatalog catalogo = MetricCatalog.Empty;
    private bool catalogoLetto;

    /// <summary>
    /// Da quando le letture falliscono di fila, oppure null se l'ultima e' andata bene.
    /// </summary>
    /// <remarks>
    /// E' cio' che distingue un servizio che sta partendo da un servizio che non c'e'. Va
    /// azzerato anche quando si cambia endpoint: a una macchina diversa spetta un'attesa
    /// nuova, non quella gia' consumata dalla precedente.
    /// </remarks>
    private DateTimeOffset? guastoDa;

    /// <summary>
    /// Costruisce la schermata.
    /// </summary>
    /// <param name="client">Il client verso il servizio, oppure null se manca la configurazione.</param>
    /// <param name="problemaDiConfigurazione">
    /// La frase da mostrare quando <paramref name="client"/> e' null.
    /// </param>
    /// <param name="rileggiConfigurazione">
    /// Come riprovare a leggere la configurazione mentre l'applicazione e' aperta, oppure
    /// null per non riprovare affatto. Restituisce un client quando la configurazione
    /// diventa valida.
    /// </param>
    /// <param name="orologio">
    /// Da dove si legge l'ora, oppure null per l'orologio di sistema. Serve alle prove:
    /// l'attesa prima di dichiarare guasto un servizio dura dieci secondi, e un test che li
    /// aspettasse davvero sarebbe un test che nessuno esegue volentieri.
    /// </param>
    /// <param name="elenco">
    /// Le macchine da mettere nella barra laterale, oppure null per non mostrarla affatto.
    /// </param>
    /// <param name="apriMacchina">Come aprire un client verso una macchina dell'elenco.</param>
    /// <param name="rileggiPunto">
    /// Come rileggere da disco la voce di una macchina non guardata quando una sonda torna
    /// con un token rifiutato o un'impronta che non corrisponde, oppure null per non rileggere.
    /// </param>
    /// <param name="copiaNegliAppunti">
    /// Come scrivere negli appunti, oppure null: senza, i comandi di copia restano spenti.
    /// </param>
    public MainViewModel(
        IMetricsClient? client,
        string? problemaDiConfigurazione,
        Func<IMetricsClient?>? rileggiConfigurazione = null,
        Func<DateTimeOffset>? orologio = null,
        MachineListResult? elenco = null,
        Func<ObserverEndpoint, IMetricsClient>? apriMacchina = null,
        Func<ObserverEndpoint, ObserverEndpoint?>? rileggiPunto = null,
        Func<string, Task>? copiaNegliAppunti = null)
    {
        this.copiaNegliAppunti = copiaNegliAppunti;
        this.client = client;
        this.rileggiConfigurazione = rileggiConfigurazione;
        this.apriMacchina = apriMacchina;
        this.rileggiPunto = rileggiPunto;
        adesso = orologio ?? (static () => DateTimeOffset.UtcNow);

        foreach (ObserverEndpoint punto in elenco?.Machines ?? [])
        {
            Macchine.Add(new MacchinaInElenco(punto));
        }

        foreach (string problema in elenco?.Problems ?? [])
        {
            ProblemiDellElenco.Add(problema);
        }

        // La selezione iniziale segue il client con cui la finestra e' stata costruita. Non
        // serve alcun guardiano contro la propria stessa scrittura: il gestore qui sotto esce
        // da se' quando la macchina scelta e' gia' quella aperta.
        MacchinaSelezionata = Macchine.FirstOrDefault(
            voce => client is not null && voce.Punto == client.Endpoint) ?? Macchine.FirstOrDefault();

        // Solo il nome dell'applicazione. QUALE macchina si sta guardando lo dicono gia' la
        // riga sotto il titolo e la voce evidenziata nella barra laterale: ripeterlo nel
        // titolo grande e' rumore che si legge a ogni sguardo. La versione sta nella barra
        // del titolo della finestra, non qui: la si cerca quando serve, non la si rilegge.
        Intestazione = "Observer";

        if (client is null)
        {
            Mostra(
                FAInfoBarSeverity.Error,
                "Configuration missing",
                problemaDiConfigurazione ?? "The configuration could not be read.");
            SottoIntestazione = "Not connected.";
        }
        else
        {
            Mostra(FAInfoBarSeverity.Informational, "Connecting", "Taking the first reading…");
            SottoIntestazione = "Connecting…";
        }
    }

    /// <summary>Titolo della finestra: nome e versione del programma.</summary>
    /// <remarks>
    /// Costante per tutta la vita della finestra, quindi non e' osservabile. La versione e'
    /// quella dei metadati del binario, cioe' di <c>Directory.Build.props</c>, senza l'hash.
    /// </remarks>
    public string TitoloFinestra { get; } = Titolo(AppVersion.DiQuestoProgramma());

    /// <summary>Titolo grande in cima alla finestra.</summary>
    [ObservableProperty]
    public partial string Intestazione { get; set; }

    /// <summary>Compone il titolo della finestra dalla versione.</summary>
    /// <param name="versione">La versione corta, o vuota se non c'e'.</param>
    /// <returns><c>Observer 0.8.0</c>, oppure solo <c>Observer</c> quando la versione manca.</returns>
    public static string Titolo(string versione)
    {
        ArgumentNullException.ThrowIfNull(versione);

        return versione.Length == 0 ? "Observer" : "Observer " + versione;
    }

    /// <summary>Riga sotto il titolo: stato del collegamento e ora dell'ultima lettura.</summary>
    [ObservableProperty]
    public partial string SottoIntestazione { get; set; }

    /// <summary>Titolo della barra di stato.</summary>
    [ObservableProperty]
    public partial string StatoTitolo { get; set; } = string.Empty;

    /// <summary>Testo della barra di stato.</summary>
    [ObservableProperty]
    public partial string StatoMessaggio { get; set; } = string.Empty;

    /// <summary>Gravita' della barra di stato.</summary>
    [ObservableProperty]
    public partial FAInfoBarSeverity StatoGravita { get; set; } = FAInfoBarSeverity.Informational;

    /// <summary>True quando c'e' qualcosa da segnalare. Quando tutto va, la barra sparisce.</summary>
    [ObservableProperty]
    public partial bool StatoVisibile { get; set; } = true;

    /// <summary>I riquadri, uno per sorgente di metriche.</summary>
    public ObservableCollection<MetricGroup> Gruppi { get; } = [];

    /// <summary>I quadranti, raccolti in cima da tutte le sorgenti.</summary>
    /// <remarks>
    /// Contiene le STESSE istanze che stanno dentro i gruppi, non delle copie: le righe si
    /// aggiornano sul posto una volta al secondo, e due copie divergerebbero senza che niente
    /// lo segnali. Qui si raccolgono soltanto per mostrarle insieme.
    /// </remarks>
    public ObservableCollection<MetricRow> Quadranti { get; } = [];

    private DateTimeOffset prossimoStorico = DateTimeOffset.MinValue;

    /// <summary>True quando c'e' almeno un quadrante da mostrare.</summary>
    /// <remarks>
    /// Senza, un riquadro vuoto col suo titolo resterebbe a schermo quando nessuna metrica e'
    /// misurabile - che e' proprio il momento in cui non deve sembrare che vada tutto bene.
    /// </remarks>
    [ObservableProperty]
    public partial bool MostraQuadranti { get; set; }

    /// <summary>I processi mostrati nel pannello, quando e' aperto.</summary>
    public ObservableCollection<ProcessoMostrato> Processi { get; } = [];

    /// <summary>True quando il pannello dei processi e' aperto.</summary>
    [ObservableProperty]
    public partial bool ProcessiVisibili { get; set; }

    /// <summary>Titolo del pannello: dice di quale risorsa si stanno guardando i processi.</summary>
    [ObservableProperty]
    public partial string ProcessiTitolo { get; set; } = string.Empty;

    /// <summary>Che cosa non va nel pannello, quando qualcosa non va. Vuoto altrimenti.</summary>
    [ObservableProperty]
    public partial string ProcessiProblema { get; set; } = string.Empty;

    /// <summary>La riga selezionata, quella che il pulsante terminerebbe.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuoCopiareLaRiga))]
    [NotifyCanExecuteChangedFor(nameof(CopiaProcessoCommand))]
    public partial ProcessoMostrato? ProcessoSelezionato { get; set; }

    /// <summary>True quando c'e' una riga selezionata da poter terminare.</summary>
    [ObservableProperty]
    public partial bool PuoTerminare { get; set; }

    /// <summary>Il nome della macchina da riaprire la prossima volta, o null per questo computer.</summary>
    /// <remarks>
    /// Il nome GREZZO del punto, non <c>MacchinaInElenco.Nome</c>: quello e' il nome
    /// <i>visibile</i>, che ripiega sull'indirizzo quando una voce non ne ha uno — il caso
    /// della vecchia configurazione a macchina singola — e sulla parola "This machine" per il
    /// canale locale. Nessuna delle due e' una chiave: la prima e' un indirizzo che finirebbe
    /// in un file dove non deve stare, la seconda non corrisponde a niente in
    /// <c>machines.json</c>.
    /// <para>
    /// E' la macchina davvero LETTA, non quella selezionata: la selezione puo' essere nulla
    /// mentre il giro continua a leggere, ed e' la stessa distinzione per cui esiste
    /// <c>voceGuardata</c>. Non e' osservabile perche' la finestra la legge una volta sola,
    /// alla chiusura.
    /// </para>
    /// </remarks>
    public string? MacchinaDaRicordare => voceGuardata?.Punto.Nome?.Trim();

    /// <summary>True quando gli appunti sono raggiungibili: senza, i comandi restano spenti.</summary>
    /// <remarks>
    /// La cucitura verso gli appunti arriva da chi costruisce il view model, ed e' opzionale
    /// perche' una prova senza finestra non ce l'ha. Se un giorno qualcuno la dimenticasse
    /// nella radice di composizione, un comando che esce da se' sul null lascerebbe un
    /// pulsante che non fa niente e non lo dice — e i test resterebbero verdi, perche' loro il
    /// finto ce l'hanno. Spento si vede al primo avvio.
    /// </remarks>
    public bool PuoCopiare => copiaNegliAppunti is not null;

    /// <summary>True quando c'e' una riga di processo da copiare.</summary>
    /// <remarks>
    /// Sulla SELEZIONE e non su <see cref="PuoTerminare"/>, anche se oggi coincidono: copiare
    /// una riga e' di sola lettura, terminarla no, e far viaggiare la prima sul permesso della
    /// seconda vuol dire che il giorno in cui si stringe il cancello di chi puo' uccidere un
    /// processo — un utente senza diritti, una macchina di sola lettura — sparirebbe anche la
    /// possibilita' di copiarne il nome, senza che nessuno l'abbia deciso.
    /// </remarks>
    public bool PuoCopiareLaRiga => PuoCopiare && ProcessoSelezionato is not null;

    /// <summary>
    /// True quando il pulsante di terminazione e' gia' stato premuto una volta e sta
    /// aspettando la conferma.
    /// </summary>
    /// <remarks>
    /// La conferma sta nel pulsante e non in una finestra di dialogo, e non e' pigrizia: una
    /// finestra modale qui richiederebbe di passare la finestra padre al view model, cioe' di
    /// legare la logica all'interfaccia proprio dove finora non lo e'. Due clic sullo stesso
    /// pulsante, col testo che cambia, difendono dallo stesso errore — un clic distratto su
    /// una riga sbagliata — senza quella dipendenza.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestoTermina))]
    public partial bool ConfermaTerminazione { get; set; }

    /// <summary>Che cosa c'e' scritto sul pulsante di terminazione, adesso.</summary>
    /// <remarks>
    /// UN pulsante che cambia scritta, e non due che si alternano: con due, al primo clic il
    /// pulsante premuto spariva e il fuoco della tastiera cadeva nel vuoto, e chi conferma con
    /// Invio si trovava a premere Invio su niente.
    /// </remarks>
    public string TestoTermina => ConfermaTerminazione ? "Click again to end it" : "End process";

    /// <summary>
    /// True quando la finestra e' ridotta a icona: la cadenza delle letture si allunga.
    /// </summary>
    /// <remarks>Lo imposta la finestra; il view model non sa cos'e' una finestra.</remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Cadenza))]
    public partial bool InSecondoPiano { get; set; }

    /// <summary>Ogni quanto si legge, adesso.</summary>
    public TimeSpan Cadenza => InSecondoPiano ? IntervalloRidotto : Intervallo;

    /// <summary>Quanto e' scalata la finestra: 1 e' la misura normale, sotto 1 e' piu' piccola.</summary>
    /// <remarks>
    /// Avalonia non legge la dimensione del testo di sistema, quindi chi l'ha alzata in Windows
    /// qui non la ritrova. Questa e' l'impostazione interna che la sostituisce, e va anche sotto
    /// il 100 %, dove Windows non va: e' uno zoom, non solo una misura del testo. La applica la
    /// finestra, che scala tutto - quadranti compresi - e la ricorda fra un avvio e l'altro.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScalaScelta))]
    public partial double ScalaTesto { get; set; } = Preferenze.ScalaNormale;

    /// <summary>Le scale fra cui si sceglie, come voci del selettore.</summary>
    public static IReadOnlyList<OpzioneScala> OpzioniScala { get; } =
        [.. Preferenze.ScaleAmmesse.Select(fattore => new OpzioneScala(fattore))];

    /// <summary>La scala come voce del selettore: e' <see cref="ScalaTesto"/> con un'etichetta.</summary>
    /// <remarks>
    /// Il selettore puo' assegnare null mentre cambia elenco: allora la scala resta com'e'.
    /// </remarks>
    public OpzioneScala ScalaScelta
    {
        get => new(ScalaTesto);
        set => ScalaTesto = value?.Fattore ?? ScalaTesto;
    }

    /// <summary>Una scala non ammessa non entra: la si riporta alla normale.</summary>
    /// <param name="value">La scala richiesta.</param>
    partial void OnScalaTestoChanged(double value)
    {
        double valida = Preferenze.ScalaValida(value);

        if (valida != value)
        {
            ScalaTesto = valida;
        }
    }

    /// <summary>Il tema: <c>system</c>, <c>light</c> o <c>dark</c>.</summary>
    /// <remarks>
    /// Lo applica l'applicazione, non questa classe, che non sa cos'e' un tema: qui sta solo
    /// la scelta, perche' e' cio' che la tendina mostra e cio' che si ricorda.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemaScelto))]
    public partial string Tema { get; set; } = Preferenze.TemiAmmessi[0];

    /// <summary>I temi fra cui si sceglie, come voci del selettore.</summary>
    public static IReadOnlyList<OpzioneTema> OpzioniTema { get; } =
        [.. Preferenze.TemiAmmessi.Select(chiave => new OpzioneTema(chiave))];

    /// <summary>Quanto storico mostra la striscia: <c>1h</c>, <c>24h</c> o <c>7d</c>.</summary>
    /// <remarks>
    /// La chiave e non la voce, per la stessa ragione del tema: e' cio' che finisce nel file.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodoScelto))]
    public partial string Periodo { get; set; } = Preferenze.PeriodiAmmessi[0];

    /// <summary>I periodi fra cui si sceglie, come voci del selettore.</summary>
    public static IReadOnlyList<OpzionePeriodo> OpzioniPeriodo { get; } =
        [.. Preferenze.PeriodiAmmessi.Select(chiave => new OpzionePeriodo(chiave))];

    /// <summary>Il periodo come voce del selettore.</summary>
    /// <remarks>
    /// Il getter non lancia MAI, come quello del tema: una chiave che non e' nella tabella
    /// ricadrebbe sulla prima voce invece di far cadere la finestra mentre si disegna.
    /// </remarks>
    public OpzionePeriodo PeriodoScelto
    {
        get => OpzioniPeriodo.FirstOrDefault(voce => voce.Chiave == Periodo) ?? OpzioniPeriodo[0];
        set => Periodo = value?.Chiave ?? Periodo;
    }

    /// <summary>Il titolo sopra la striscia: dice il periodo DISEGNATO, non quello scelto.</summary>
    /// <remarks>
    /// Non si calcola da <see cref="Periodo"/>, ed e' una scelta. Calcolato, cambiava con il
    /// selettore - cioe' all'istante - mentre sotto restavano le barre del periodo precedente
    /// finche' la lettura nuova non atterrava: fino a otto secondi su una macchina lenta, e per
    /// SEMPRE su una macchina che non risponde, perche' li' lo storico non si rilegge affatto.
    /// La finestra chiamava "Last 7 days" sessanta barre da un minuto, e una macchina a riposo
    /// da un'ora si leggeva come a riposo da una settimana. Adesso lo scrive chi disegna, dopo
    /// la guardia sulle risposte in ritardo: il selettore dice cosa e' stato chiesto, il titolo
    /// cosa si sta guardando, e quando divergono e' perche' divergono davvero.
    /// </remarks>
    [ObservableProperty]
    public partial string TitoloStorico { get; set; } = new OpzionePeriodo(Preferenze.PeriodiAmmessi[0]).Titolo;

    /// <summary>Il tema come voce del selettore: e' <see cref="Tema"/> con un'etichetta.</summary>
    /// <remarks>Il selettore puo' assegnare null mentre cambia elenco: allora il tema resta com'e'.</remarks>
    public OpzioneTema TemaScelto
    {
        get => new(Tema);
        set => Tema = value?.Chiave ?? Tema;
    }

    /// <summary>Un tema non ammesso non entra: si torna a quello del sistema.</summary>
    /// <param name="value">Il tema richiesto.</param>
    partial void OnTemaChanged(string value)
    {
        string valido = Preferenze.TemaValido(value);

        if (!string.Equals(valido, value, StringComparison.Ordinal))
        {
            Tema = valido;
        }
    }

    /// <summary>Un periodo non ammesso non entra, e quello nuovo si legge subito.</summary>
    /// <param name="value">Il periodo richiesto.</param>
    /// <remarks>
    /// Subito e non al prossimo giro: chi sceglie "7 days" guarda la striscia, e aspettare fino
    /// a due ore per vederla cambiare sarebbe indistinguibile da un selettore che non funziona.
    /// La scadenza torna indietro invece di chiamare la lettura da qui: cosi' la richiesta
    /// parte dal ciclo, dove c'e' gia' il token di annullamento e la guardia sull'esito, e non
    /// da un setter che il selettore chiama sul thread dell'interfaccia.
    /// </remarks>
    partial void OnPeriodoChanged(string value)
    {
        string valido = Preferenze.PeriodoValido(value);

        if (!string.Equals(valido, value, StringComparison.Ordinal))
        {
            Periodo = valido;

            return;
        }

        // Finche' non c'e' niente disegnato il titolo segue il selettore: non c'e' striscia da
        // contraddire, e all'avvio con "7d" nel file dire "Last hour" sopra il vuoto sarebbe
        // sbagliato e basta. Appena una lettura atterra, il titolo torna a dire cio' che si vede.
        if (!Quadranti.Any(riga => riga.MostraStorico))
        {
            TitoloStorico = PeriodoScelto.Titolo;
        }

        prossimoStorico = DateTimeOffset.MinValue;
    }

    /// <summary>Quale risorsa sta guardando il pannello: <c>cpu</c>, <c>memory</c>, o null.</summary>
    private string? risorsaMostrata;

    /// <summary>Le macchine fra cui si puo' scegliere, ognuna col suo stato. La prima e' sempre questa.</summary>
    public ObservableCollection<MacchinaInElenco> Macchine { get; } = [];

    /// <summary>Ogni quanto si sondano le macchine che NON si stanno guardando.</summary>
    /// <remarks>
    /// Quindici secondi e non uno: un pallino accanto al nome deve dire "e' viva", non seguire
    /// la CPU. E le sonde partono e non si aspettano: una macchina spenta costa otto secondi
    /// di timeout, e il giro dei quadranti non deve pagarli.
    /// </remarks>
    public static readonly TimeSpan RicaricaStati = TimeSpan.FromSeconds(15);

    private DateTimeOffset prossimaSonda = DateTimeOffset.MinValue;

    /// <summary>Le voci dell'elenco che sono state scartate, e perche'.</summary>
    /// <remarks>
    /// Mostrate accanto all'elenco invece che nascoste in un log: una macchina configurata male
    /// che semplicemente NON COMPARE e' indistinguibile da una macchina che non e' stata
    /// aggiunta, e chi la cerca non ha modo di sapere che cosa correggere.
    /// </remarks>
    public ObservableCollection<string> ProblemiDellElenco { get; } = [];

    /// <summary>
    /// Vero quando l'elenco contiene solo questa macchina e non c'e' niente da correggere.
    /// </summary>
    /// <remarks>
    /// La barra laterale si vede SEMPRE, e prima non era cosi': compariva solo quando c'era
    /// gia' una seconda macchina. Il risultato e' che nessuno poteva scoprire di poterne
    /// aggiungere una, perche' l'unico posto dove la funzione si annuncia e' la funzione
    /// stessa. Una funzione che si mostra solo a chi sa gia' che esiste non esiste.
    /// <para>
    /// Al suo posto, quando c'e' una macchina sola, si spiega come aggiungerne un'altra e si
    /// dice il percorso esatto del file da scrivere.
    /// </para>
    /// </remarks>
    public bool MostraSuggerimento => Macchine.Count <= 1 && ProblemiDellElenco.Count == 0;

    /// <summary>Come si aggiunge una macchina, col percorso del file da scrivere.</summary>
    public string SuggerimentoElenco { get; } =
        "Only this machine so far. To watch another one, run \"observer share\" on it and put " +
        "what it prints into " + MachineDirectory.FilePath;

    /// <summary>La macchina attualmente guardata.</summary>
    [ObservableProperty]
    public partial MacchinaInElenco? MacchinaSelezionata { get; set; }

    /// <summary>Cambia macchina senza riavviare la finestra.</summary>
    /// <param name="value">La macchina scelta nell'elenco.</param>
    partial void OnMacchinaSelezionataChanged(MacchinaInElenco? value)
    {
        if (value is null)
        {
            // La lista non dovrebbe arrivarci (AlwaysSelected); se ci arriva, la macchina
            // guardata resta quella di prima e voceGuardata non cambia.
            return;
        }

        voceGuardata = value;

        // Il carico e' un derivato della macchina guardata come il catalogo e i quadranti, e va
        // buttato QUI, prima delle uscite anticipate, perche' l'invariante e' legata a
        // voceGuardata e non al client. Senza questa riga la voce appena cliccata continua a
        // mostrare i numeri che la sonda le aveva scritto fino a quindici secondi prima: circa
        // un secondo se la macchina risponde, ma gli interi otto del budget di richiesta se non
        // risponde - cioe' proprio quando la si e' cliccata per capire cosa le succede, sotto
        // il nome evidenziato resta scritto che sta lavorando mentre la barra dice "Connecting".
        value.Carico = Carico.Nessuno;

        if (apriMacchina is null)
        {
            return;
        }

        if (client is not null && value.Punto == client.Endpoint)
        {
            return;
        }

        client = apriMacchina(value.Punto);

        // Tutto cio' che descriveva la macchina PRECEDENTE va buttato: il catalogo, perche' le
        // etichette appartengono a quel servizio, e i riquadri, perche' sono le sue misure.
        // L'orologio dei guasti invece si EREDITA dalla voce: se la sonda sa gia' da venti
        // secondi che questa macchina e' spenta, la barra apre rossa subito invece di recitare
        // dieci secondi di "Connecting" - la grazia serve a un servizio che sta partendo, non a
        // uno gia' misurato spento. E cosi' barra e pallino hanno un orologio solo.
        catalogoLetto = false;
        catalogo = MetricCatalog.Empty;
        guastoDa = value.GuastoDa;
        Gruppi.Clear();

        // E i quadranti, che sono una SECONDA collezione sulle stesse righe. Svuotare solo i
        // riquadri lasciava a schermo le lancette e le strisce della macchina precedente,
        // sotto il nome di quella nuova: numeri veri, attribuiti alla macchina sbagliata. Si
        // vedeva a colpo d'occhio proprio perche' meta' della finestra si svuotava e meta' no.
        // Chi aggiunge una terza collezione derivata la aggiunga QUI.
        Quadranti.Clear();
        MostraQuadranti = false;

        // E anche la scadenza dello storico e' un derivato della macchina precedente. Le righe
        // rinascono senza striscia e senza nota - ne' barre ne' il motivo per cui non ci sono -
        // e senza questa riga restano cosi' fino alla scadenza EREDITATA: mezz'ora a sette
        // giorni, quasi quattro minuti a ventiquattro ore, con i quadranti sopra gia' vivi.
        prossimoStorico = DateTimeOffset.MinValue;

        Mostra(FAInfoBarSeverity.Informational, "Connecting", "Taking the first reading...");
        SottoIntestazione = "Connecting...";
    }

    /// <summary>
    /// Il ciclo di aggiornamento. Non lancia mai: qualunque guasto diventa testo a schermo.
    /// </summary>
    /// <param name="cancellationToken">Annullato alla chiusura dell'applicazione.</param>
    /// <summary>
    /// Riprova a leggere la configurazione finche' non diventa valida.
    /// </summary>
    /// <returns>True se un client e' stato adottato, false se non c'e' modo di riprovare.</returns>
    private async Task<bool> AttendiConfigurazioneAsync(CancellationToken cancellationToken)
    {
        if (rileggiConfigurazione is null)
        {
            return false;
        }

        using PeriodicTimer attesa = new(Intervallo);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await attesa.WaitForNextTickAsync(cancellationToken))
            {
                return false;
            }

            if (rileggiConfigurazione() is not { } comparso)
            {
                continue;
            }

            client = comparso;
            guastoDa = null;
            Mostra(FAInfoBarSeverity.Informational, "Connecting", "Taking the first reading…");
            SottoIntestazione = "Connecting…";
            return true;
        }

        return false;
    }

    public async Task EseguiAsync(CancellationToken cancellationToken)
    {
        if (client is null)
        {
            // Senza token non c'e' niente da interrogare: martellare il servizio con richieste
            // destinate al 401 non aiuta. Ma il messaggio a schermo dice all'utente di creare
            // un file di configurazione, e se crearlo non producesse alcun effetto finche' non
            // riavvia — cosa che il messaggio non dice — l'utente seguirebbe le istruzioni alla
            // lettera e concluderebbe che l'applicazione e' rotta. Quindi si rilegge.
            if (!await AttendiConfigurazioneAsync(cancellationToken))
            {
                return;
            }
        }

        using PeriodicTimer timer = new(Intervallo);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ServiceOutcome esito = await AggiornaAsync(cancellationToken);

                // Lo storico dopo il campionamento e solo se il campionamento e' andato: se
                // la macchina non risponde, insistere sullo storico aggiungerebbe attese a una
                // finestra che sta gia' aspettando, senza poter dire niente di nuovo.
                if (esito == ServiceOutcome.Ok && adesso() >= prossimoStorico)
                {
                    // La scadenza la sposta la lettura, non questa riga: e' l'unica che sa se
                    // e' andata bene, male, o se la risposta e' arrivata quando non serviva
                    // piu'. Spostarla da qui significava tre cose sbagliate insieme - un
                    // timeout rimandava di un passo intero, cioe' due ore di "No history" su
                    // dati gia' tornati; una risposta scartata cancellava l'azzeramento che il
                    // selettore aveva appena fatto, spegnendolo per quindici secondi; e la
                    // scadenza si leggeva dal periodo di ADESSO invece che da quello chiesto.
                    await AggiornaStoricoAsync(cancellationToken);
                }

                // I processi seguono lo stesso giro dei quadranti, ma solo a pannello aperto:
                // chiedere un elenco che nessuno sta guardando costerebbe una richiesta al
                // secondo per niente.
                if (esito == ServiceOutcome.Ok && ProcessiVisibili)
                {
                    await AggiornaProcessiAsync(cancellationToken);
                }

                // Le altre macchine, per il pallino accanto al nome. Partono e non si
                // aspettano: vedi SondaLeAltre.
                if (adesso() >= prossimaSonda)
                {
                    prossimaSonda = adesso() + RicaricaStati;
                    SondaLeAltre(cancellationToken);
                }

                // Un 401 su una finestra GIA' collegata significa quasi sempre che il token e'
                // stato ruotato. Senza rileggere qui, la finestra resterebbe bloccata su
                // "Token rejected" fino al riavvio: e' lo stesso incidente di "Configuration
                // missing", su un altro percorso, e va chiuso allo stesso modo.
                // Anche ImprontaNonCorrisponde, e per la stessa ragione: il messaggio dice
                // all'utente di correggere machines.json, e correggerlo deve BASTARE. E' il
                // terzo percorso su cui questo incidente si presenta - dopo "Configuration
                // missing" e "Token rejected" - e chiuderne due su tre non serve a niente.
                if (esito is ServiceOutcome.TokenRifiutato or ServiceOutcome.ImprontaNonCorrisponde)
                {
                    AdottaConfigurazioneAggiornata();
                }

                // La cadenza segue la finestra: ridotta a icona si legge ogni dieci secondi.
                // Cambiare il periodo di un PeriodicTimer vale dal tick successivo, che e'
                // esattamente quello che serve: nessun timer da ricreare, nessun giro perso.
                timer.Period = Cadenza;

                if (!await timer.WaitForNextTickAsync(cancellationToken))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Chiusura dell'applicazione: uscita normale, non un errore da mostrare.
        }
#pragma warning disable CA1031 // Questo ciclo e' avviato senza nessuno che ne attenda l'esito:
        catch (Exception ex) // un'eccezione qui sparirebbe in silenzio e la finestra si
#pragma warning restore CA1031 // congelerebbe senza dire niente. Va mostrata, non propagata.
        {
            Mostra(
                FAInfoBarSeverity.Error,
                "Updates stopped",
                $"Automatic refresh stopped after an unexpected error ({ex.GetType().Name}: " +
                $"{ex.Message}). The values on screen have stopped updating: close and reopen the application.");
            SottoIntestazione = "Refresh stopped.";
        }
    }

    /// <summary>
    /// Rilegge la configurazione e adotta il client risultante, se e' cambiato.
    /// </summary>
    /// <remarks>
    /// Non chiude il client precedente: chi lo ha costruito ne conserva il riferimento e lo
    /// chiude all'uscita. Chiuderlo qui lo strapperebbe da sotto una richiesta ancora in volo.
    /// </remarks>
    private void AdottaConfigurazioneAggiornata()
    {
        if (rileggiConfigurazione?.Invoke() is not { } ricomparso || ReferenceEquals(ricomparso, client))
        {
            return;
        }

        client = ricomparso;
        voceGuardata?.Aggiorna(ricomparso.Endpoint);

        // Attesa nuova: l'endpoint e' cambiato, e i secondi gia' consumati contro il
        // precedente non dicono niente su questo. Il pallino ha lo stesso orologio, ma lo
        // azzera Aggiorna qui sopra, insieme al testo che ne deriva: da fuori la voce non lo
        // tocca piu' nessuno.
        guastoDa = null;

        // Il catalogo appartiene al servizio precedente: va riletto, altrimenti le etichette
        // resterebbero quelle di una macchina diversa.
        catalogoLetto = false;
        catalogo = MetricCatalog.Empty;
    }

    private async Task<ServiceOutcome> AggiornaAsync(CancellationToken cancellationToken)
    {
        if (client is not { } corrente)
        {
            return ServiceOutcome.Unknown;
        }

        // Prima il campionamento e SOLO POI il catalogo. Verificato sperimentalmente: con il
        // servizio spento, chiedere prima il catalogo raddoppia l'attesa — due timeout invece
        // di uno — e la finestra resta a dire "collegamento in corso" per sei secondi prima di
        // ammettere che non si collega.
        SnapshotFetch fetch = await corrente.GetLatestAsync(cancellationToken);

        // Fra la partenza della richiesta e la sua risposta l'utente puo' aver cambiato
        // macchina nella barra laterale. Applicare qui i valori appena arrivati significherebbe
        // mostrare le misure della macchina PRECEDENTE sotto il nome di quella nuova, e
        // riempirne il catalogo con etichette che non sono le sue.
        if (!ReferenceEquals(client, corrente))
        {
            return ServiceOutcome.Unknown;
        }

        if (!fetch.IsOk)
        {
            SegnalaProblema(fetch.Outcome, fetch.Problem, corrente.Endpoint);
            return fetch.Outcome;
        }

        // Il catalogo cambia solo quando cambia il servizio: si legge una volta sola, e si
        // ritenta al giro dopo se non riesce. Va letto PRIMA di disegnare, altrimenti il primo
        // fotogramma mostrerebbe "cpu.usage.total" al posto di "CPU usage". Se non arriva
        // mai, le metriche restano visibili con il loro identificatore grezzo invece di sparire.
        if (!catalogoLetto)
        {
            CatalogFetch catalogFetch = await corrente.GetCatalogAsync(cancellationToken);

            if (catalogFetch.IsOk && ReferenceEquals(client, corrente))
            {
                catalogo = catalogFetch.Catalog!;
                catalogoLetto = true;
            }
        }

        // Stessa guardia anche dopo il catalogo: e' un secondo await, e l'utente puo' aver
        // cambiato macchina proprio li'. Senza, il pallino di una macchina mai contattata
        // diventava "Reachable" con la lettura di quella precedente.
        if (!ReferenceEquals(client, corrente))
        {
            return ServiceOutcome.Unknown;
        }

        MachineSnapshot snapshot = fetch.Snapshot!;
        Applica(SnapshotProjection.Project(snapshot, catalogo));

        StatoVisibile = false;
        voceGuardata?.Registra(ServiceOutcome.Ok, string.Empty, adesso());

        // La serie di guasti e' finita: la prossima ricomincia da capo, e ha diritto alla
        // stessa attesa che ha avuto questa.
        guastoDa = null;

        // Solo QUANDO. Il dove non e' sparito, si e' spostato dove non va riletto a ogni
        // sguardo: la macchina che si sta guardando e' quella selezionata nell'elenco a
        // sinistra, e questa riga cambia una volta al secondo mentre quella non cambia mai.
        string ora = snapshot.CapturedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        SottoIntestazione = $"Last Reading: {ora}";

        return ServiceOutcome.Ok;
    }

    /// <summary>
    /// Traduce una lettura fallita in cio' che si vede a schermo.
    /// </summary>
    /// <remarks>
    /// La gravita' NON dipende dal singolo tentativo andato male ma da quanto dura la serie:
    /// e' <see cref="StatusEscalation"/> a deciderlo, ed e' li' che sta la tabella provata.
    /// Qui resta solo la misura del tempo e la traduzione in colore.
    /// </remarks>
    private void SegnalaProblema(ServiceOutcome esito, string testo, ObserverEndpoint punto)
    {
        DateTimeOffset ora = adesso();
        guastoDa ??= ora;

        StatusMessage messaggio = StatusEscalation.Per(
            esito,
            testo,
            ora - guastoDa.Value,
            punto,
            valoriGiaMostrati: Gruppi.Count > 0);

        Mostra(Gravita(messaggio.Tone), messaggio.Title, messaggio.Text);
        SottoIntestazione = messaggio.Subheading;

        // Il pallino della macchina guardata segue la barra di stato, con lo stesso orologio,
        // cosi' i due non dicono mai cose diverse.
        voceGuardata?.Registra(esito, testo, ora);
    }

    /// <summary>Interroga le macchine che non si stanno guardando, tutte insieme e senza aspettarle.</summary>
    /// <param name="cancellationToken">Annullato alla chiusura.</param>
    /// <remarks>
    /// Il giro principale NON attende le sonde: una macchina spenta risponde dopo otto secondi
    /// di timeout, e i quadranti della macchina guardata non devono fermarsi per questo. Ogni
    /// sonda aggiorna la propria voce quando torna, e finche' e' in volo non ne parte un'altra.
    /// </remarks>
    private void SondaLeAltre(CancellationToken cancellationToken)
    {
        if (apriMacchina is null)
        {
            return;
        }

        foreach (MacchinaInElenco voce in Macchine)
        {
            // Si salta la voce che il giro principale legge DAVVERO, non la selezione della
            // lista: vedi voceGuardata.
            if (voce.InSonda || ReferenceEquals(voce, voceGuardata))
            {
                continue;
            }

            voce.InSonda = true;
            _ = SondaAsync(voce, cancellationToken);
        }
    }

    private async Task SondaAsync(MacchinaInElenco voce, CancellationToken cancellationToken)
    {
        try
        {
            SnapshotFetch fetch = await apriMacchina!(voce.Punto).GetLatestAsync(cancellationToken);

            // La voce e' diventata la GUARDATA mentre la sonda era in volo: non si scrive, e
            // non si rilegge nemmeno il punto. La sonda parte saltando la guardata ma torna
            // fino a otto secondi dopo, e un clic basta. Scrivere vorrebbe dire due letture
            // della stessa macchina a cadenze diverse, che con i numeri accanto al nome si
            // contraddicono a vista. E rileggere sarebbe peggio che inutile: Aggiorna
            // sostituisce Punto senza toccare client, e AdottaConfigurazioneAggiornata
            // confronta proprio quel Punto con il disco - trovandolo gia' aggiornato non
            // riparerebbe piu', e la finestra resterebbe su "Token rejected" dopo un
            // "observer token set" andato a buon fine. Sulla guardata ci pensa il giro
            // principale, che ha in mano sia il punto sia il client.
            if (ReferenceEquals(voce, voceGuardata))
            {
                return;
            }

            voce.Registra(fetch.Outcome, fetch.Problem, adesso(), fetch.Snapshot);

            // Token rifiutato o impronta che non corrisponde: la voce va riletta da disco,
            // come fa gia' il giro principale per la macchina guardata. Altrimenti la sonda
            // successiva riparte con la credenziale vecchia e il pallino resta rosso fino al
            // riavvio, anche dopo "observer token set".
            if (fetch.Outcome is ServiceOutcome.TokenRifiutato or ServiceOutcome.ImprontaNonCorrisponde
                && rileggiPunto?.Invoke(voce.Punto) is { } riletto
                && riletto != voce.Punto)
            {
                voce.Aggiorna(riletto);
            }
        }
        catch (OperationCanceledException)
        {
            // Chiusura: niente da registrare.
        }
#pragma warning disable CA1031 // Una sonda che lancia non deve far cadere niente: il suo esito e' un pallino.
        catch (Exception errore)
#pragma warning restore CA1031
        {
            // Stessa guardia del ramo riuscito, e per la stessa ragione.
            if (!ReferenceEquals(voce, voceGuardata))
            {
                voce.Registra(ServiceOutcome.Unknown, errore.Message, adesso());
            }
        }
        finally
        {
            voce.InSonda = false;
        }
    }

    private static FAInfoBarSeverity Gravita(StatusTone tono) => tono switch
    {
        StatusTone.Informational => FAInfoBarSeverity.Informational,
        StatusTone.Warning => FAInfoBarSeverity.Warning,
        _ => FAInfoBarSeverity.Error,
    };

    private void Mostra(FAInfoBarSeverity gravita, string titolo, string messaggio)
    {
        StatoGravita = gravita;
        StatoTitolo = titolo;
        StatoMessaggio = messaggio;
        StatoVisibile = true;
    }

    private void Applica(IReadOnlyList<MetricGroupState> stati)
    {
        if (!StessiCollector(stati))
        {
            Gruppi.Clear();

            foreach (MetricGroupState stato in stati)
            {
                Gruppi.Add(new MetricGroup(stato));
            }
        }

        else
        {
            for (int i = 0; i < stati.Count; i++)
            {
                Gruppi[i].Aggiorna(stati[i]);
            }
        }

        AggiornaQuadranti();
    }

    /// <summary>Rilegge lo storico di ogni metrica che ha un quadrante.</summary>
    /// <param name="cancellationToken">Annullato alla chiusura.</param>
    /// <remarks>
    /// <para>
    /// Non lancia e non tocca <c>guastoDa</c> ne' la barra di stato, di proposito: <b>un
    /// guasto dello storico non e' un guasto della macchina</b>. Il servizio puo' rispondere
    /// benissimo al campionamento e avere la persistenza spenta, e colorare di rosso la
    /// finestra per questo insegnerebbe a ignorare anche gli allarmi veri. Il motivo finisce
    /// accanto alla striscia, dove riguarda.
    /// </para>
    /// <para>
    /// Questa funzione possiede <c>prossimoStorico</c>, e ci sono TRE esiti, non due: andata
    /// bene, andata male, e arrivata quando non serviva piu'. Il terzo non tocca la scadenza -
    /// chi ha cambiato periodo o macchina l'ha appena riportata indietro di proposito, e
    /// spostarla qui vorrebbe dire lasciare a schermo la striscia vecchia sotto il titolo nuovo
    /// per quindici secondi, che da fuori e' indistinguibile da un selettore rotto.
    /// </para>
    /// </remarks>
    private async Task AggiornaStoricoAsync(CancellationToken cancellationToken)
    {
        if (client is not { } corrente)
        {
            return;
        }

        DateTimeOffset ora = adesso();
        OpzionePeriodo periodo = PeriodoScelto;

        // Tutte le strisce insieme, non una dopo l'altra: sei quadranti facevano dodici
        // richieste in fila, e il tempo del giro era la SOMMA delle latenze. Le richieste
        // partono qui, in parallelo; le righe si toccano solo dopo, quando sono tornate tutte,
        // e sul thread dell'interfaccia.
        List<MetricRow> righe = [.. Quadranti];

        (HistoryFetch Aggregato, HistoryFetch? Coda)[] letture = await Task.WhenAll(
            righe.Select(riga => LeggiStoricoAsync(corrente, riga.Key, periodo, ora, cancellationToken)))
            .ConfigureAwait(true);

        // A sette giorni una lettura puo' durare l'intero budget di otto secondi, e in quel
        // tempo possono essere cambiate DUE cose: il periodo scelto e la macchina guardata.
        // Scrivere queste barre adesso vorrebbe dire disegnare una settimana dentro una
        // striscia da un'ora, o lo storico della macchina sbagliata.
        // Il confronto sulle righe non e' un di piu' rispetto a quello sul client: App.Apri
        // tiene UN client per punto, quindi due cambi di macchina in fila (A->B->A) riportano
        // lo stesso identico oggetto, mentre Quadranti e' stata svuotata due volte e queste
        // righe non sono piu' a schermo. Scriverci dentro perderebbe la lettura in silenzio.
        if (!ReferenceEquals(client, corrente)
            || PeriodoScelto != periodo
            || !righe.SequenceEqual(Quadranti))
        {
            return;
        }

        // Il titolo lo scrive chi disegna, qui e non nel selettore: da questa riga in poi la
        // striscia e la frase sopra parlano dello stesso periodo.
        TitoloStorico = periodo.Titolo;

        // "Andata bene" vuol dire TUTTE, non almeno una. Con "almeno una" cinque strisce su sei
        // possono restare due ore a dire "No history" mentre la sesta si aggiorna, che e' lo
        // stesso difetto di prima ridotto di un sesto. Solo l'aggregato conta: la coda grezza
        // puo' mancare senza che la striscia ne soffra - la disegna comunque l'aggregato - e
        // guardarla qui trasformerebbe un guasto innocuo in una richiesta ogni quindici secondi
        // per sempre. Zero quadranti conta come non andata: non e' partita nessuna richiesta,
        // quindi riprovare presto e' gratis e copre i quadranti che compaiono piu' tardi.
        bool riuscita = righe.Count > 0;

        for (int i = 0; i < righe.Count; i++)
        {
            ApplicaStorico(righe[i], letture[i].Aggregato, letture[i].Coda, periodo, ora);
            riuscita &= letture[i].Aggregato.Outcome == ServiceOutcome.Ok;
        }

        prossimoStorico = adesso() + ProssimaLettura(periodo, riuscita);
    }

    /// <summary>Fra quanto si rilegge lo storico, dato il periodo e com'e' andata.</summary>
    /// <param name="periodo">Il periodo mostrato.</param>
    /// <param name="riuscita">True se ogni striscia ha ricevuto i suoi dati.</param>
    /// <returns>Quanto aspettare prima della lettura successiva.</returns>
    /// <remarks>
    /// Pura e pubblica perche' i due errori che ha gia' fatto non si vedono da nessuna parte
    /// se non qui: <b>rimandare un guasto di un passo intero</b> - a sette giorni due ore di
    /// "No history" su dati tornati da un secondo - e <b>rileggere esattamente ogni passo</b>,
    /// che sembra la cadenza giusta e non lo e', perche' guarderebbe ogni volta una barra
    /// appena nata e l'estremo destro della striscia resterebbe un pixel per sempre.
    /// </remarks>
    public static TimeSpan ProssimaLettura(OpzionePeriodo periodo, bool riuscita)
    {
        ArgumentNullException.ThrowIfNull(periodo);

        if (!riuscita)
        {
            return RiprovaStorico;
        }

        TimeSpan cadenza = periodo.Passo / RiletturePerPasso;

        return cadenza > RiletturaMinima ? cadenza : RiletturaMinima;
    }

    /// <summary>Da quanto indietro leggere il grezzo per la coda, dato il passo della sorgente.</summary>
    /// <remarks>
    /// Tre punti di sorgente, mai meno del minimo. Con la sorgente a un minuto restano i dieci
    /// minuti di sempre; a cinque minuti servono quindici, perche' il consolidamento di quel
    /// livello aspetta anche il livello sotto e resta indietro piu' a lungo.
    /// </remarks>
    private static TimeSpan CodaDi(OpzionePeriodo periodo)
    {
        TimeSpan tre = periodo.PassoSorgente * 3;

        return tre > CodaMinima ? tre : CodaMinima;
    }

    /// <summary>Le due letture dello storico di UNA metrica: l'aggregato al minuto e la coda grezza.</summary>
    /// <returns>La coda e' null quando l'aggregato non c'e': senza quello non serve.</returns>
    private static async Task<(HistoryFetch Aggregato, HistoryFetch? Coda)> LeggiStoricoAsync(
        IMetricsClient corrente,
        string chiave,
        OpzionePeriodo periodo,
        DateTimeOffset ora,
        CancellationToken cancellationToken)
    {
        string[] pezzi = chiave.Split('|');

        if (pezzi.Length < 2)
        {
            return (new HistoryFetch(ServiceOutcome.Unknown, "malformed metric key", null), null);
        }

        string? istanza = pezzi.Length > 2 && pezzi[2].Length > 0 ? pezzi[2] : null;

        HistoryFetch aggregato = await corrente.GetHistoryAsync(
            new HistoryQuery(pezzi[0], pezzi[1], istanza, ora - periodo.Finestra, periodo.Risoluzione),
            cancellationToken).ConfigureAwait(false);

        if (aggregato.Outcome != ServiceOutcome.Ok || aggregato.Points is null)
        {
            return (aggregato, null);
        }

        HistoryFetch coda = await corrente.GetHistoryAsync(
            new HistoryQuery(pezzi[0], pezzi[1], istanza, ora - CodaDi(periodo), "raw"),
            cancellationToken).ConfigureAwait(false);

        return (aggregato, coda);
    }

    private static void ApplicaStorico(
        MetricRow riga,
        HistoryFetch aggregato,
        HistoryFetch? coda,
        OpzionePeriodo periodo,
        DateTimeOffset ora)
    {
        if (aggregato.Outcome != ServiceOutcome.Ok || aggregato.Points is null)
        {
            riga.Storico = null;
            riga.NotaStorico = "No history: " + aggregato.Problem;

            return;
        }

        // La coda grezza si raggruppa al passo della SORGENTE, non a quello della barra: e'
        // cio' che la rende confrontabile con i punti aggregati prima di unirli. Il passo
        // della barra lo applica Costruisci, una volta sola e su tutto.
        IReadOnlyList<HistoryPoint> punti = coda is { Outcome: ServiceOutcome.Ok, Points: not null }
            ? HistoryStrip.Unisci(aggregato.Points, HistoryStrip.Raggruppa(coda.Points, periodo.PassoSorgente))
            : aggregato.Points;

        riga.NotaStorico = punti.Count > 0
            ? string.Empty
            : "No history recorded for this metric yet.";

        riga.Storico = HistoryStrip.Costruisci(InFrazioni(punti), ora, periodo.Barre, periodo.Passo);
    }

    /// <summary>Porta i valori dello storico nella scala 0..1 dei quadranti.</summary>
    /// <remarks>
    /// Lo storico conserva i valori come sono stati misurati, quindi una percentuale arriva
    /// da 0 a 100. E' la stessa divisione che <c>MetricFormatting.Fraction</c> fa per la riga
    /// a schermo: se le due divergessero, quadrante e striscia racconterebbero due storie
    /// diverse della stessa metrica.
    /// </remarks>
    private static IReadOnlyList<HistoryPoint> InFrazioni(IReadOnlyList<HistoryPoint> punti) =>
        [.. punti.Select(punto => punto with
        {
            Avg = Math.Clamp(punto.Avg / 100d, 0d, 1d),
            Min = Math.Clamp(punto.Min / 100d, 0d, 1d),
            Max = Math.Clamp(punto.Max / 100d, 0d, 1d),
            Last = Math.Clamp(punto.Last / 100d, 0d, 1d),
        })];

    /// <summary>Rifa' l'elenco dei quadranti solo quando cambia davvero.</summary>
    /// <remarks>
    /// Il confronto e' per RIFERIMENTO, e deve restarlo: le righe sono le stesse istanze che
    /// stanno nei gruppi e si aggiornano da sole, quindi svuotare e riempire la collezione a
    /// ogni giro ricostruirebbe ogni quadrante una volta al secondo, facendo lampeggiare la
    /// finestra. Si ricostruisce quando un collector va o viene, oppure quando una metrica
    /// smette di essere misurabile e il suo quadrante non ha piu' senso.
    /// </remarks>
    private void AggiornaQuadranti()
    {
        List<MetricRow> attesi =
            [.. Gruppi.SelectMany(gruppo => gruppo.Righe).Where(riga => riga.HaQuadrante)];

        MostraQuadranti = attesi.Count > 0;

        if (attesi.Count == Quadranti.Count
            && !attesi.Where((riga, i) => !ReferenceEquals(riga, Quadranti[i])).Any())
        {
            return;
        }

        Quadranti.Clear();

        foreach (MetricRow riga in attesi)
        {
            Quadranti.Add(riga);
        }
    }

    /// <summary>Apre il pannello dei processi per la risorsa del quadrante scelto.</summary>
    /// <param name="riga">Il quadrante su cui si e' cliccato.</param>
    /// <returns>L'attesa della prima lettura.</returns>
    /// <remarks>
    /// Le esecuzioni concorrenti vanno PERMESSE: il comando e' uno solo per tutti i quadranti,
    /// e un comando asincrono, finche' e' in esecuzione, rifiuta ogni altra esecuzione. Senza
    /// questo, mentre la prima lettura e' in volo su una macchina remota lenta, ogni altro clic
    /// — su un altro quadrante, o sullo stesso per chiudere — verrebbe scartato in silenzio, e
    /// la finestra sembrerebbe non rispondere. La risposta di una lettura ormai superata la
    /// scarta <see cref="AggiornaProcessiAsync"/>.
    /// </remarks>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ApriProcessiAsync(MetricRow? riga)
    {
        if (riga is null || ProcessResource.Da(riga.Key) is not { } risorsa)
        {
            return;
        }

        // Lo stesso quadrante una seconda volta CHIUDE: e' il gesto che chiunque prova per
        // primo per far sparire una cosa che ha appena fatto comparire. Un altro quadrante
        // invece cambia elenco senza chiudere.
        if (ProcessiVisibili && string.Equals(risorsaMostrata, risorsa, StringComparison.Ordinal))
        {
            ChiudiProcessi();

            return;
        }

        risorsaMostrata = risorsa;
        // "Whole machine" sta nel titolo perche' il quadrante da cui si arriva e' quello di UN
        // disco, e l'elenco non lo e': i contatori di I/O sono per processo, non per dispositivo.
        ProcessiTitolo = risorsa switch
        {
            "memory" => "Top processes by memory",
            "io" => "Top processes by I/O (whole machine)",
            _ => "Top processes by CPU",
        };

        ProcessiVisibili = true;
        ConfermaTerminazione = false;
        ProcessiProblema = string.Empty;

        await AggiornaProcessiAsync(CancellationToken.None);
    }

    /// <summary>Copia negli appunti cio' che dice la barra di stato.</summary>
    /// <returns>L'attesa della scrittura negli appunti.</returns>
    /// <remarks>
    /// E' il caso che pesa: un messaggio d'errore lungo — un'impronta che non corrisponde, con
    /// le due impronte per intero — altrimenti va ricopiato a mano per incollarlo in una
    /// ricerca. Il titolo e il messaggio su due righe, perche' sono due frasi.
    /// <para>
    /// <c>AllowConcurrentExecutions</c> non e' decorazione: un <c>AsyncRelayCommand</c> in
    /// esecuzione si disabilita e rifiuta ogni altra chiamata, quindi un secondo clic mentre
    /// gli appunti stanno scrivendo cadrebbe nel vuoto con il pulsante che lampeggia spento.
    /// E' il difetto gia' pagato dai sei pulsanti dei quadranti.
    /// </para>
    /// </remarks>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(PuoCopiare))]
    private Task CopiaStatoAsync() =>
        NegliAppuntiAsync(StatoTitolo + Environment.NewLine + StatoMessaggio);

    /// <summary>Copia negli appunti la riga di processo selezionata, col suo PID.</summary>
    /// <returns>L'attesa della scrittura negli appunti.</returns>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(PuoCopiareLaRiga))]
    private Task CopiaProcessoAsync() =>
        ProcessoSelezionato is { } scelto ? NegliAppuntiAsync(scelto.PerGliAppunti) : Task.CompletedTask;

    /// <summary>Scrive negli appunti, e non lascia che un loro guasto si veda altrove.</summary>
    /// <param name="testo">Cio' che va negli appunti.</param>
    /// <returns>L'attesa della scrittura.</returns>
    /// <remarks>
    /// Un guasto degli appunti non ha dove dirsi: l'unico posto sarebbe la barra di stato, che
    /// e' proprio cio' che si sta copiando, e sovrascriverla cancellerebbe il messaggio.
    /// Meglio non fare niente che perdere il testo per raccontare che non si e' riusciti a
    /// copiarlo.
    /// </remarks>
    private Task NegliAppuntiAsync(string testo)
    {
        if (copiaNegliAppunti is not { } copia)
        {
            return Task.CompletedTask;
        }

        // IN FILA, una dopo l'altra. Gli appunti di Windows possono essere tenuti da un altro
        // programma, e Avalonia in quel caso riprova dieci volte a cento millisecondi l'una:
        // due scritture partite a poca distanza hanno due cicli di ritentativo indipendenti, e
        // vince quella che RIESCE per ultima, non quella che si e' chiesta per ultima. Senza
        // fila, un secondo clic puo' lasciare negli appunti il testo del primo — misurato, e in
        // silenzio. Tutto gira sul thread dell'interfaccia, quindi la fila non ha bisogno di
        // serrature: basta incatenare i Task.
        codaAppunti = InFilaAsync(codaAppunti, copia, testo);

        return codaAppunti;
    }

    /// <summary>Aspetta la scrittura precedente, poi scrive. Non lancia mai.</summary>
    /// <param name="precedente">La scrittura da aspettare.</param>
    /// <param name="copia">Come scrivere.</param>
    /// <param name="testo">Cosa scrivere.</param>
    /// <returns>L'attesa della propria scrittura.</returns>
    /// <remarks>
    /// Che non lanci mai e' cio' che rende sicuro aspettarla dalla chiamata successiva: una
    /// scrittura fallita non deve trascinarsi dietro quelle dopo.
    /// </remarks>
    private static async Task InFilaAsync(Task precedente, Func<string, Task> copia, string testo)
    {
        await precedente;

        try
        {
            await copia(testo);
        }
#pragma warning disable CA1031 // Gli appunti possono essere tenuti da un altro programma: e'
        catch (Exception) // un fallimento del sistema, non un guasto della dashboard.
#pragma warning restore CA1031
        {
            // Niente. Un guasto degli appunti non ha dove dirsi: l'unico posto sarebbe la
            // barra di stato, che e' proprio cio' che si sta copiando.
        }
    }

    /// <summary>Chiude il pannello e dimentica cosa c'era dentro.</summary>
    [RelayCommand]
    private void ChiudiProcessi()
    {
        ProcessiVisibili = false;
        risorsaMostrata = null;
        ProcessoSelezionato = null;
        ConfermaTerminazione = false;
        ProcessiProblema = string.Empty;
        Processi.Clear();
    }

    /// <summary>Termina il processo selezionato, chiedendo conferma al primo clic.</summary>
    /// <returns>L'attesa della richiesta e della rilettura.</returns>
    [RelayCommand]
    private async Task TerminaSelezionatoAsync()
    {
        if (client is null || ProcessoSelezionato is not { } scelto)
        {
            return;
        }

        // Primo clic: arma soltanto. Il pulsante cambia testo, e chi ha cliccato per sbaglio
        // se ne accorge prima che succeda qualcosa.
        if (!ConfermaTerminazione)
        {
            ConfermaTerminazione = true;

            return;
        }

        ConfermaTerminazione = false;

        KillFetch esito = await client.KillProcessAsync(scelto.Pid, CancellationToken.None);

        ProcessiProblema = esito.Outcome == ServiceOutcome.Ok ? string.Empty : esito.Problem;

        await AggiornaProcessiAsync(CancellationToken.None);
    }

    /// <summary>Cambiare riga disarma la conferma.</summary>
    /// <param name="value">La riga appena selezionata.</param>
    /// <remarks>
    /// Senza, una conferma armata su un processo resterebbe armata dopo aver selezionato un
    /// altro processo, e il secondo clic terminerebbe quello sbagliato.
    /// </remarks>
    partial void OnProcessoSelezionatoChanged(ProcessoMostrato? value)
    {
        ConfermaTerminazione = false;
        PuoTerminare = value is not null;
    }

    private async Task AggiornaProcessiAsync(CancellationToken cancellationToken)
    {
        if (client is null || risorsaMostrata is not { } risorsa)
        {
            return;
        }

        ProcessFetch esito = await client.GetProcessesAsync(risorsa, QuantiProcessi, cancellationToken);

        // Mentre la risposta era in volo il pannello puo' essere stato chiuso, o portato su
        // un'altra risorsa: questa risposta allora non e' piu' di nessuno. Applicarla
        // riempirebbe un pannello chiuso, o metterebbe le righe della CPU sotto il titolo
        // della memoria.
        if (!ProcessiVisibili || !string.Equals(risorsaMostrata, risorsa, StringComparison.Ordinal))
        {
            return;
        }

        if (esito.Outcome != ServiceOutcome.Ok)
        {
            ProcessiProblema = esito.Problem;

            return;
        }

        ProcessiProblema = string.Empty;

        // La selezione si tiene sul PID e non sull'oggetto: le righe arrivano nuove a ogni
        // giro, e senza questo la selezione si perderebbe una volta al secondo — cioe' proprio
        // mentre si sta puntando il processo da terminare.
        int? selezionato = ProcessoSelezionato?.Pid;

        Processi.Clear();

        foreach (ProcessoMostrato riga in esito.Processi)
        {
            Processi.Add(riga);
        }

        ProcessoSelezionato = selezionato is { } pid
            ? Processi.FirstOrDefault(riga => riga.Pid == pid)
            : null;
    }

    private bool StessiCollector(IReadOnlyList<MetricGroupState> stati)
    {
        if (Gruppi.Count != stati.Count)
        {
            return false;
        }

        for (int i = 0; i < stati.Count; i++)
        {
            if (!string.Equals(Gruppi[i].CollectorId, stati[i].CollectorId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}