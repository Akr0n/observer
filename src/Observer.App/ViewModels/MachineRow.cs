using CommunityToolkit.Mvvm.ComponentModel;
using Observer.App.Services;
using Observer.Core.Metrics;

namespace Observer.App.ViewModels;

/// <summary>Come sta una macchina dell'elenco, per il pallino accanto al nome.</summary>
public enum MachineStatus
{
    /// <summary>Non e' ancora stata interrogata. Grigio.</summary>
    Unknown = 0,

    /// <summary>L'ultima lettura e' andata. Verde.</summary>
    Reachable = 1,

    /// <summary>Non risponde da poco, o risponde con un avviso. Giallo.</summary>
    Warning = 2,

    /// <summary>Faulted vero, secondo la stessa regola della barra di stato. Rosso.</summary>
    Faulted = 3,
}

/// <summary>
/// Una voce della barra laterale: la macchina, e come sta.
/// </summary>
/// <remarks>
/// Deriva da <see cref="ObservableObject"/> e NON da <see cref="ViewModelBase"/>, per la stessa
/// ragione di <see cref="MetricRow"/>: ViewLocator aggancia qualunque ViewModelBase e
/// disegnerebbe un "Not Found" al posto della riga.
/// <para>
/// Lo stato segue la regola della barra di stato - <see cref="StatusEscalation"/>, con la sua
/// grazia di dieci secondi - cosi' un pallino rosso vuol dire la stessa cosa di una barra
/// rossa. Prima della barra laterale con i pallini, per sapere come stava una macchina
/// bisognava cliccarci sopra.
/// </para>
/// </remarks>
public sealed partial class MachineRow : ObservableObject
{
    /// <summary>Costruisce la voce, ancora senza stato.</summary>
    /// <param name="punto">La macchina.</param>
    public MachineRow(ObserverEndpoint punto)
    {
        ArgumentNullException.ThrowIfNull(punto);

        Endpoint = punto;
    }

    /// <summary>La macchina. Cambia solo con <see cref="Update"/>, a credenziale ruotata.</summary>
    public ObserverEndpoint Endpoint { get; private set; }

    /// <summary>Sostituisce il punto: stesso indirizzo, credenziale nuova.</summary>
    /// <param name="punto">La voce riletta da disco.</param>
    /// <remarks>
    /// Senza, una macchina non guardata verrebbe sondata per sempre con il token letto
    /// all'avvio, e dopo <c>observer token set</c> il suo pallino resterebbe "Token rejected"
    /// fino al riavvio: lo stesso incidente gia' chiuso tre volte per la macchina guardata.
    /// </remarks>
    internal void Update(ObserverEndpoint punto)
    {
        ArgumentNullException.ThrowIfNull(punto);

        Endpoint = punto;

        // La misura ricomincia: da qui in poi e' un'altra macchina, o la stessa raggiunta in
        // un altro modo, e "giu' da due giorni" riferito alla precedente sarebbe una bugia.
        // Sta QUI e non nel chiamante perche' i chiamanti sono due, e uno dei due si
        // dimenticherebbe.
        FailingSince = null;
        DowntimeText = string.Empty;

        // E il carico con loro: era di quell'altro endpoint. Un numero vero riferito a una
        // macchina che non e' piu' quella si legge come se fosse di questa. Vale identico per
        // il riepilogo, che racconta la storia di un'altra macchina: va rifatto, non tradotto.
        MachineLoad = MachineLoad.None;
        SummaryLine = string.Empty;
        SummaryPeriodKey = null;

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(AccessibleName));
    }

    /// <summary>Il nome scritto nell'elenco.</summary>
    public string Name => Endpoint.DisplayName;

    /// <summary>From quando le letture falliscono di fila, oppure null se l'ultima e' andata.</summary>
    /// <remarks>
    /// La stessa misura che la barra di stato tiene per la macchina guardata. Il setter e'
    /// privato: l'orologio e il testo che ne deriva devono muoversi insieme, e da fuori si
    /// azzerano solo cambiando la macchina della voce, cioe' da <see cref="Update"/>.
    /// <para>
    /// E' il primo fallimento che QUESTA finestra ha visto, non l'istante in cui la macchina
    /// e' andata giu': una dashboard appena aperta su una macchina spenta da tre giorni dira'
    /// "under 1 min". Il dato per saperlo davvero non c'e' — la macchina che dovrebbe dirlo
    /// e' proprio quella che non risponde. MessageFor la stessa ragione e' tempo di calendario e non
    /// tempo osservato: attraverso una sospensione del PC, o un intervallo in cui la finestra
    /// era chiusa, la durata rivendica una continuita' che nessuno ha guardato.
    /// </para>
    /// </remarks>
    internal DateTimeOffset? FailingSince { get; private set; }

    /// <summary>True mentre una sonda e' in volo: la prossima non le parte sopra.</summary>
    /// <remarks>Leggibile da fuori perche' un test lo osserva; lo scrive solo il view model.</remarks>
    public bool IsProbing { get; internal set; }

    /// <summary>True mentre si legge lo storico per il riepilogo. Gemella di <see cref="IsProbing"/>.</summary>
    public bool IsSummarizing { get; internal set; }

    /// <summary>MessageFor quale periodo il riepilogo e' stato calcolato, o null se mai.</summary>
    /// <remarks>
    /// La chiave e non la voce, come ovunque: e' cio' che si confronta per sapere se va rifatto.
    /// Cambiando periodo cambia la domanda - "cosa mi sono perso nell'ultima ora" non e' "negli
    /// ultimi sette giorni" - quindi la risposta vecchia non vale piu'.
    /// </remarks>
    public string? SummaryPeriodKey { get; internal set; }

    /// <summary>Cosa e' successo a questa macchina mentre nessuno guardava. Vuota se niente.</summary>
    public string SummaryLine { get; internal set; } = string.Empty;

    /// <summary>Come sta.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnknown), nameof(IsReachable), nameof(IsWarning), nameof(IsFaulted), nameof(AccessibleName))]
    public partial MachineStatus Status { get; set; }

    /// <summary>Perche' sta cosi', in una frase corta: il titolo che avrebbe la barra di stato.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(ToolTipText))]
    public partial string Detail { get; set; } = "Not checked yet";

    /// <summary>From quanto dura il guasto, gia' scritto: <c>for 2 h 10 min</c>. Vuoto se non c'e'.</summary>
    /// <remarks>
    /// Una proprieta' MEMORIZZATA, scritta quando arriva una lettura, e non un getter che
    /// legge l'orologio: cosi' non serve alcun timer, e la riga non puo' cambiare mentre
    /// nessuno guarda. Il prezzo e' che il testo puo' restare indietro fino alla lettura
    /// successiva, ed e' per questo che <see cref="Downtime.Describe"/> tronca.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle), nameof(ToolTipText), nameof(AccessibleName))]
    public partial string DowntimeText { get; set; } = string.Empty;

    /// <summary>Quanto sta lavorando questa macchina, quando si sa.</summary>
    /// <remarks>
    /// Lo scrive <see cref="Record"/> dal snapshot che la sonda ha gia' in mano, e resta
    /// <see cref="MachineLoad.None"/> per la macchina GUARDATA: li' i numeri sono nei quadranti,
    /// grandi, a due centimetri di distanza, e ripeterli piccoli accanto al nome vorrebbe dire
    /// due letture della stessa macchina che possono contraddirsi a vista - la sonda gira ogni
    /// quindici secondi, il giro principale ogni secondo. La barra laterale risponde a "devo
    /// cambiare macchina?", e per quella su cui si e' gia' la risposta e' gia' a schermo.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle), nameof(ToolTipText), nameof(AccessibleName))]
    public partial MachineLoad MachineLoad { get; set; } = MachineLoad.None;

    /// <summary>La riga sotto il nome: o da quanto e' giu', o quanto sta lavorando.</summary>
    /// <remarks>
    /// Una riga sola e non due, perche' i due contenuti si escludono per costruzione: il carico
    /// esiste solo quando la lettura e' andata, e <see cref="DowntimeText"/> si scrive solo quando
    /// NON e' andata. La durata vince comunque, esplicitamente: se un giorno le due potessero
    /// coesistere, "giu' da tre minuti" e' cio' che si deve leggere.
    /// </remarks>
    public string Subtitle => DowntimeText.Length > 0 ? DowntimeText : MachineLoad.Caption;

    /// <summary>Cio' che dice il suggerimento del mouse: il motivo, e da quanto dura.</summary>
    /// <remarks>
    /// Separati da un punto medio e non da uno spazio: il prefisso e' uno solo per dieci
    /// titoli diversi, e attaccato ad alcuni cambia il senso della frase. "Token rejected for
    /// 3 min" in inglese si legge "respinto PER tre minuti", cioe' un blocco a tempo, che e'
    /// il contrario di cio' che sta succedendo. Il punto medio spezza la frase e lascia due
    /// fatti accostati, che e' quello che sono.
    /// </remarks>
    /// <remarks>
    /// Dice cio' che dice la riga, non <see cref="DowntimeText"/>: cosi' il carico di una macchina
    /// arriva anche a chi la riga non la vede. Non cambia di continuo, e non e' un caso - la
    /// sonda gira ogni quindici secondi e la macchina guardata non ha carico, quindi la voce
    /// SELEZIONATA, che e' l'unica che un lettore di schermo riannuncia, ha esattamente il
    /// testo che aveva prima di questa aggiunta.
    /// </remarks>
    public string ToolTipText => Subtitle.Length == 0 ? Detail : $"{Detail} · {Subtitle}";

    /// <summary>True finche' nessuno l'ha interrogata.</summary>
    public bool IsUnknown => Status == MachineStatus.Unknown;

    /// <summary>True quando l'ultima lettura e' andata.</summary>
    public bool IsReachable => Status == MachineStatus.Reachable;

    /// <summary>True quando c'e' un reason che potrebbe ancora passare da solo.</summary>
    public bool IsWarning => Status == MachineStatus.Warning;

    /// <summary>True su un guasto vero.</summary>
    public bool IsFaulted => Status == MachineStatus.Faulted;

    /// <summary>Name e stato insieme, per chi non vede il pallino.</summary>
    /// <remarks>
    /// Passa da <see cref="ToolTipText"/> e non da <see cref="Detail"/>: cosi' il
    /// suggerimento del mouse e cio' che annuncia un lettore di schermo non possono divergere,
    /// e la durata la sente anche chi la riga non la vede.
    /// </remarks>
    public string AccessibleName => $"{Name}: {ToolTipText}";

    /// <summary>Record l'outcome di una lettura, dalla sonda o dal giro principale.</summary>
    /// <param name="outcome">Com'e' andata.</param>
    /// <param name="reason">La frase del client, quando non e' andata.</param>
    /// <param name="now">L'ora, per misurare da quanto dura un guasto.</param>
    /// <param name="snapshot">
    /// Cio' che la lettura ha riportato, da cui si ricava il carico. Null - ed e' il valore
    /// predefinito - per la macchina GUARDATA: vedi <see cref="MachineLoad"/>.
    /// </param>
    public void Record(
        ServiceOutcome outcome,
        string reason,
        DateTimeOffset now,
        MachineSnapshot? snapshot = null)
    {
        // Sta QUI e non nei chiamanti per la stessa ragione scritta in Update: i chiamanti
        // sono tre, e uno si dimenticherebbe di azzerarlo - lasciando sotto il nome di una
        // macchina che non risponde il carico che aveva l'ultima volta che rispondeva.
        MachineLoad = outcome == ServiceOutcome.Ok ? MachineLoad.From(snapshot) : MachineLoad.None;

        if (outcome == ServiceOutcome.Ok)
        {
            FailingSince = null;
            DowntimeText = string.Empty;
            Status = MachineStatus.Reachable;
            Detail = "Reachable";

            return;
        }

        FailingSince ??= now;

        StatusMessage message = StatusEscalation.MessageFor(
            outcome, reason, now - FailingSince.Value, Endpoint, hasValuesOnScreen: false);

        Status = message.Tone == StatusTone.Error ? MachineStatus.Faulted : MachineStatus.Warning;
        Detail = message.Title;

        // Il cancello e' il TONO, non lo stato: dentro i dieci secondi di tolleranza il tono
        // e' neutro e non si dice ancora niente, perche' un contatore che parte su ogni
        // singhiozzo insegna a ignorarlo - che e' cio' che StatusEscalation esiste per
        // impedire. E' il tono e non lo stato IsFaulted perche' un "No readings yet" arriva DOPO
        // la tolleranza ma resta un avviso, non un rosso, e puo' durare giorni: filtrare sul
        // rosso lo lascerebbe fuori proprio mentre e' la cosa che dura di piu'.
        DowntimeText = message.Tone == StatusTone.Informational
            ? string.Empty
            : "for " + Downtime.Describe(now - FailingSince.Value);
    }
}