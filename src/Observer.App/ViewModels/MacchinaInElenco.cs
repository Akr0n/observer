using CommunityToolkit.Mvvm.ComponentModel;
using Observer.App.Services;

namespace Observer.App.ViewModels;

/// <summary>Come sta una macchina dell'elenco, per il pallino accanto al nome.</summary>
public enum StatoVoce
{
    /// <summary>Non e' ancora stata interrogata. Grigio.</summary>
    Ignoto = 0,

    /// <summary>L'ultima lettura e' andata. Verde.</summary>
    Raggiungibile = 1,

    /// <summary>Non risponde da poco, o risponde con un avviso. Giallo.</summary>
    Attenzione = 2,

    /// <summary>Guasto vero, secondo la stessa regola della barra di stato. Rosso.</summary>
    Guasto = 3,
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
public sealed partial class MacchinaInElenco : ObservableObject
{
    /// <summary>Costruisce la voce, ancora senza stato.</summary>
    /// <param name="punto">La macchina.</param>
    public MacchinaInElenco(ObserverEndpoint punto)
    {
        ArgumentNullException.ThrowIfNull(punto);

        Punto = punto;
    }

    /// <summary>La macchina. Cambia solo con <see cref="Aggiorna"/>, a credenziale ruotata.</summary>
    public ObserverEndpoint Punto { get; private set; }

    /// <summary>Sostituisce il punto: stesso indirizzo, credenziale nuova.</summary>
    /// <param name="punto">La voce riletta da disco.</param>
    /// <remarks>
    /// Senza, una macchina non guardata verrebbe sondata per sempre con il token letto
    /// all'avvio, e dopo <c>observer token set</c> il suo pallino resterebbe "Token rejected"
    /// fino al riavvio: lo stesso incidente gia' chiuso tre volte per la macchina guardata.
    /// </remarks>
    internal void Aggiorna(ObserverEndpoint punto)
    {
        ArgumentNullException.ThrowIfNull(punto);

        Punto = punto;

        // La misura ricomincia: da qui in poi e' un'altra macchina, o la stessa raggiunta in
        // un altro modo, e "giu' da due giorni" riferito alla precedente sarebbe una bugia.
        // Sta QUI e non nel chiamante perche' i chiamanti sono due, e uno dei due si
        // dimenticherebbe.
        GuastoDa = null;
        DaQuanto = string.Empty;

        OnPropertyChanged(nameof(Nome));
        OnPropertyChanged(nameof(Descrizione));
    }

    /// <summary>Il nome scritto nell'elenco.</summary>
    public string Nome => Punto.NomeVisibile;

    /// <summary>Da quando le letture falliscono di fila, oppure null se l'ultima e' andata.</summary>
    /// <remarks>
    /// La stessa misura che la barra di stato tiene per la macchina guardata. Il setter e'
    /// privato: l'orologio e il testo che ne deriva devono muoversi insieme, e da fuori si
    /// azzerano solo cambiando la macchina della voce, cioe' da <see cref="Aggiorna"/>.
    /// <para>
    /// E' il primo fallimento che QUESTA finestra ha visto, non l'istante in cui la macchina
    /// e' andata giu': una dashboard appena aperta su una macchina spenta da tre giorni dira'
    /// "under 1 min". Il dato per saperlo davvero non c'e' — la macchina che dovrebbe dirlo
    /// e' proprio quella che non risponde. Per la stessa ragione e' tempo di calendario e non
    /// tempo osservato: attraverso una sospensione del PC, o un intervallo in cui la finestra
    /// era chiusa, la durata rivendica una continuita' che nessuno ha guardato.
    /// </para>
    /// </remarks>
    internal DateTimeOffset? GuastoDa { get; private set; }

    /// <summary>True mentre una sonda e' in volo: la prossima non le parte sopra.</summary>
    /// <remarks>Leggibile da fuori perche' un test lo osserva; lo scrive solo il view model.</remarks>
    public bool InSonda { get; internal set; }

    /// <summary>Come sta.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Ignoto), nameof(Raggiungibile), nameof(Attenzione), nameof(Guasto), nameof(Descrizione))]
    public partial StatoVoce Stato { get; set; }

    /// <summary>Perche' sta cosi', in una frase corta: il titolo che avrebbe la barra di stato.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Descrizione), nameof(Suggerimento))]
    public partial string Dettaglio { get; set; } = "Not checked yet";

    /// <summary>Da quanto dura il guasto, gia' scritto: <c>for 2 h 10 min</c>. Vuoto se non c'e'.</summary>
    /// <remarks>
    /// Una proprieta' MEMORIZZATA, scritta quando arriva una lettura, e non un getter che
    /// legge l'orologio: cosi' non serve alcun timer, e la riga non puo' cambiare mentre
    /// nessuno guarda. Il prezzo e' che il testo puo' restare indietro fino alla lettura
    /// successiva, ed e' per questo che <see cref="Downtime.Frase"/> tronca.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostraDaQuanto), nameof(Suggerimento), nameof(Descrizione))]
    public partial string DaQuanto { get; set; } = string.Empty;

    /// <summary>True quando c'e' una durata da mostrare sotto il nome.</summary>
    public bool MostraDaQuanto => DaQuanto.Length > 0;

    /// <summary>Cio' che dice il suggerimento del mouse: il motivo, e da quanto dura.</summary>
    /// <remarks>
    /// Separati da un punto medio e non da uno spazio: il prefisso e' uno solo per dieci
    /// titoli diversi, e attaccato ad alcuni cambia il senso della frase. "Token rejected for
    /// 3 min" in inglese si legge "respinto PER tre minuti", cioe' un blocco a tempo, che e'
    /// il contrario di cio' che sta succedendo. Il punto medio spezza la frase e lascia due
    /// fatti accostati, che e' quello che sono.
    /// </remarks>
    public string Suggerimento => DaQuanto.Length == 0 ? Dettaglio : $"{Dettaglio} · {DaQuanto}";

    /// <summary>True finche' nessuno l'ha interrogata.</summary>
    public bool Ignoto => Stato == StatoVoce.Ignoto;

    /// <summary>True quando l'ultima lettura e' andata.</summary>
    public bool Raggiungibile => Stato == StatoVoce.Raggiungibile;

    /// <summary>True quando c'e' un problema che potrebbe ancora passare da solo.</summary>
    public bool Attenzione => Stato == StatoVoce.Attenzione;

    /// <summary>True su un guasto vero.</summary>
    public bool Guasto => Stato == StatoVoce.Guasto;

    /// <summary>Nome e stato insieme, per chi non vede il pallino.</summary>
    /// <remarks>
    /// Passa da <see cref="Suggerimento"/> e non da <see cref="Dettaglio"/>: cosi' il
    /// suggerimento del mouse e cio' che annuncia un lettore di schermo non possono divergere,
    /// e la durata la sente anche chi la riga non la vede.
    /// </remarks>
    public string Descrizione => $"{Nome}: {Suggerimento}";

    /// <summary>Registra l'esito di una lettura, dalla sonda o dal giro principale.</summary>
    /// <param name="esito">Com'e' andata.</param>
    /// <param name="problema">La frase del client, quando non e' andata.</param>
    /// <param name="adesso">L'ora, per misurare da quanto dura un guasto.</param>
    public void Registra(ServiceOutcome esito, string problema, DateTimeOffset adesso)
    {
        if (esito == ServiceOutcome.Ok)
        {
            GuastoDa = null;
            DaQuanto = string.Empty;
            Stato = StatoVoce.Raggiungibile;
            Dettaglio = "Reachable";

            return;
        }

        GuastoDa ??= adesso;

        StatusMessage messaggio = StatusEscalation.Per(
            esito, problema, adesso - GuastoDa.Value, Punto, valoriGiaMostrati: false);

        Stato = messaggio.Tone == StatusTone.Error ? StatoVoce.Guasto : StatoVoce.Attenzione;
        Dettaglio = messaggio.Title;

        // Il cancello e' il TONO, non lo stato: dentro i dieci secondi di tolleranza il tono
        // e' neutro e non si dice ancora niente, perche' un contatore che parte su ogni
        // singhiozzo insegna a ignorarlo - che e' cio' che StatusEscalation esiste per
        // impedire. E' il tono e non lo stato Guasto perche' un "No readings yet" arriva DOPO
        // la tolleranza ma resta un avviso, non un rosso, e puo' durare giorni: filtrare sul
        // rosso lo lascerebbe fuori proprio mentre e' la cosa che dura di piu'.
        DaQuanto = messaggio.Tone == StatusTone.Informational
            ? string.Empty
            : "for " + Downtime.Frase(adesso - GuastoDa.Value);
    }
}