namespace Observer.Service;

/// <summary>
/// Lascia passare un messaggio solo quando il motivo CAMBIA, e conta quelli taciuti.
/// </summary>
/// <remarks>
/// Un messaggio dentro un ciclo che gira una volta al secondo non e' un messaggio: sono
/// 86 400 righe al giorno. Su Windows finiscono nel registro eventi <i>Application</i>, che
/// di default e' grande venti megabyte e sovrascrive i piu' vecchi, quindi un solo guasto
/// che si ripete sfratta in poche ore la cronologia eventi di TUTTA la macchina — compresa
/// quella che servirebbe a capire cos'e' successo. E se il guasto e' "disco pieno", il
/// registro che lo segnala consuma disco.
/// <para>
/// La regola e' quella che <c>ReportDrops</c> applica gia' agli scarti dello storico: si
/// scrive quando la situazione cambia. Il motivo dev'essere una chiave STABILE — il tipo
/// dell'eccezione, non il suo messaggio; "lungo", non i millisecondi del giro — altrimenti
/// cambia a ogni ripetizione e non frena niente.
/// </para>
/// <para>
/// Non e' sincronizzato di proposito: sta dentro un ciclo a thread singolo, e nasconderne la
/// contesa dietro una serratura renderebbe legittimo usarlo da piu' thread, che non e' il
/// modo in cui va usato.
/// </para>
/// </remarks>
public sealed class FrenoDiRipetizione
{
    private string? motivo;
    private int taciute;

    /// <summary>La condizione c'e' adesso.</summary>
    /// <param name="motivo">Una chiave stabile: cambia solo se cambia la natura del guasto.</param>
    /// <returns><c>true</c> se il messaggio va scritto adesso.</returns>
    public bool Segnala(string motivo)
    {
        ArgumentNullException.ThrowIfNull(motivo);

        if (string.Equals(this.motivo, motivo, StringComparison.Ordinal))
        {
            taciute++;

            return false;
        }

        this.motivo = motivo;
        taciute = 0;

        return true;
    }

    /// <summary>La condizione non c'e' piu'.</summary>
    /// <param name="taciute">Quante volte si e' ripetuta senza che nessuno la scrivesse.</param>
    /// <returns><c>true</c> se c'era davvero qualcosa da cui rientrare.</returns>
    /// <remarks>
    /// Il rientro va detto: un guasto che smette e' un'informazione quanto un guasto che
    /// comincia, e senza questa riga il registro mostrerebbe un errore e poi il nulla, che
    /// si legge come "sta ancora succedendo".
    /// </remarks>
    public bool Cessato(out int taciute)
    {
        taciute = this.taciute;

        bool cEra = motivo is not null;

        motivo = null;
        this.taciute = 0;

        return cEra;
    }
}
