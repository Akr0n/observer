namespace Observer.Service;

/// <summary>
/// Lascia passare un messaggio quando il motivo CAMBIA, e poi non piu' di uno ogni
/// <see cref="Riepilogo"/>, contando quelli taciuti.
/// </summary>
/// <remarks>
/// Un messaggio dentro un ciclo che gira una volta al secondo non e' un messaggio: sono
/// 86 400 righe al giorno. Su Windows finiscono nel registro eventi <i>Application</i>, che
/// di default e' grande venti megabyte e sovrascrive i piu' vecchi, quindi un solo guasto
/// che si ripete sfratta in poche ore la cronologia eventi di TUTTA la macchina — compresa
/// quella che servirebbe a capire cos'e' successo. E se il guasto e' "disco pieno", il
/// registro che lo segnala consuma disco.
/// <para>
/// <b>Il solo confronto col motivo non basta</b>, ed e' la trappola che ha fatto riscrivere
/// questa classe: un guasto che LAMPEGGIA — un collector che ondeggia intorno alla sua
/// scadenza, un file agganciato a intermittenza da un antivirus — alterna guasto e successo
/// a ogni giro, e con la sola regola "scrivi quando cambia" ogni ritorno del guasto e' un
/// motivo nuovo. Restava meta' del diluvio, e l'alternanza e' la forma piu' probabile dei
/// guasti che si frenano qui. Per questo cio' che conta e' l'ultimo motivo <i>scritto</i>,
/// che sopravvive alla cessazione, piu' una finestra di tempo oltre la quale un guasto che
/// dura si fa risentire — con i numeri aggiornati, che altrimenti resterebbero quelli del
/// primo giro.
/// </para>
/// <para>
/// Il motivo dev'essere una chiave STABILE — il tipo dell'eccezione, non il suo messaggio;
/// "lungo", non i millisecondi del giro — altrimenti cambia a ogni ripetizione e non frena
/// niente.
/// </para>
/// <para>
/// Non e' sincronizzato. Non perche' "gira su un thread solo" — i freni delle sorgenti
/// vengono passati a task che <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/>
/// manda avanti insieme — ma perche' ogni freno e' toccato da UNA sola sorgente, e il giro
/// successivo comincia solo dopo che il precedente e' stato atteso: e' quell'attesa a fare
/// da barriera fra il thread che scrive al giro N e quello che legge al giro N+1. Chi un
/// domani smettesse di attendere il giro precedente per non perdere tick — che e' proprio il
/// rimedio che verrebbe in mente leggendo l'avviso sul giro troppo lungo — aprirebbe qui una
/// corsa.
/// </para>
/// </remarks>
public sealed class FrenoDiRipetizione
{
    /// <summary>Ogni quanto un guasto che dura torna a farsi scrivere.</summary>
    /// <remarks>
    /// Cinque minuti: abbastanza raro da non riempire niente (288 righe al giorno nel caso
    /// peggiore, contro 86 400), abbastanza frequente da far vedere in un registro che il
    /// guasto sta ancora durando, e da aggiornare i numeri che il messaggio porta con se'.
    /// </remarks>
    public static readonly TimeSpan Riepilogo = TimeSpan.FromMinutes(5);

    private readonly TimeProvider orologio;
    private readonly TimeSpan riepilogo;

    private string? inCorso;
    private string? ultimoScritto;
    private long quandoScritto;
    private bool scrittoPerQuesto;
    private int taciute;

    /// <summary>Crea un freno.</summary>
    /// <param name="timeProvider">L'orologio, o null per quello di sistema.</param>
    /// <param name="riepilogo">Ogni quanto ripetersi, o null per <see cref="Riepilogo"/>.</param>
    public FrenoDiRipetizione(TimeProvider? timeProvider = null, TimeSpan? riepilogo = null)
    {
        orologio = timeProvider ?? TimeProvider.System;
        this.riepilogo = riepilogo ?? Riepilogo;
    }

    /// <summary>La condizione c'e' adesso.</summary>
    /// <param name="motivo">Una chiave stabile: cambia solo se cambia la natura del guasto.</param>
    /// <returns><c>true</c> se il messaggio va scritto adesso.</returns>
    public bool Segnala(string motivo)
    {
        ArgumentNullException.ThrowIfNull(motivo);

        inCorso = motivo;

        bool altroMotivo = !string.Equals(ultimoScritto, motivo, StringComparison.Ordinal);
        bool scaduta = ultimoScritto is not null && orologio.GetElapsedTime(quandoScritto) >= riepilogo;

        if (!altroMotivo && !scaduta)
        {
            taciute++;

            return false;
        }

        ultimoScritto = motivo;
        quandoScritto = orologio.GetTimestamp();
        scrittoPerQuesto = true;
        taciute = 0;

        return true;
    }

    /// <summary>La condizione non c'e' piu'.</summary>
    /// <param name="taciute">Quante volte si e' ripetuta senza che nessuno la scrivesse.</param>
    /// <returns><c>true</c> se il rientro va annunciato.</returns>
    /// <remarks>
    /// Il rientro va detto: un guasto che smette e' un'informazione quanto un guasto che
    /// comincia, e senza questa riga il registro mostrerebbe un errore e poi il nulla, che si
    /// legge come "sta ancora succedendo". Si annuncia anche quando il guasto e' durato un
    /// giro solo — e' il caso piu' comune, ed e' proprio quello in cui una riga sola
    /// lascerebbe credere a un guasto ancora aperto. Non si annuncia invece un guasto che
    /// nessuno ha scritto, perche' taciuto dentro la finestra: sarebbe la fine di una storia
    /// che il registro non ha mai cominciato, ed e' cio' che tiene silenzioso un guasto che
    /// lampeggia.
    /// </remarks>
    public bool Cessato(out int taciute)
    {
        taciute = this.taciute;

        bool daAnnunciare = inCorso is not null && scrittoPerQuesto;

        inCorso = null;
        scrittoPerQuesto = false;

        return daAnnunciare;
    }
}
