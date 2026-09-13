using System.Globalization;

namespace Observer.App.Services;

/// <summary>
/// Da quanto dura un guasto, detto in una frase corta.
/// </summary>
/// <remarks>
/// Sta qui e non nel view model per la stessa ragione di <see cref="StatusEscalation"/> e di
/// <c>WindowPlacement</c>: e' una regola che si puo' provare senza una finestra, e un refuso
/// qui non fa rumore — mostra un numero plausibile e sbagliato accanto a una macchina giu'.
/// <para>
/// La duration si TRONCA, non si arrotonda, ed e' una scelta che tiene onesto anche un testo in
/// ritardo. La riga si aggiorna quando arriva una lettura: ogni secondo per la macchina
/// guardata, ogni dieci quando la finestra e' ridotta a icona, e ogni quindici per le altre —
/// venti a icona, perche' la sonda scatta sul giro del timer — piu' l'attesa della risposta,
/// che arriva a otto secondi. Il caso peggiore e' quindi vicino al mezzo minuto. Un testo
/// arrotondato per eccesso direbbe "3 min" quando ne sono passati 2 e 31 s, mentre troncando
/// il numero mostrato resta un limite INFERIORE della duration misurata: chi legge "2 min" sa
/// che sono almeno due minuti.
/// </para>
/// <para>
/// Quel limite vale rispetto al RITARDO delle letture, non rispetto a un orologio che salta.
/// La misura nasce da due letture dell'ora di parete, quindi un passo in avanti — un
/// allineamento NTP, una macchina virtuale ripresa da sospensione — gonfia la duration della
/// propria dimensione, su un intervallo in cui non e' stata fatta alcuna misura. Misurare da
/// una sorgente monotona lo chiuderebbe, ma toccherebbe anche le scadenze delle sonde e dello
/// storico, che leggono lo stesso orologio: e' una decisione a parte, non una riga.
/// </para>
/// <para>
/// Al massimo due unit', e la seconda solo se non e' zero: "2 h 10 min" dice quanto serve,
/// "2 h 10 min 33 s" chiede di leggere tre numeri per sapere una cosa sola. I secondi non
/// compaiono mai, perche' con letture cosi' distanziate sarebbero una precisione che il dato
/// non ha.
/// </para>
/// </remarks>
public static class Downtime
{
    /// <summary>La duration, in una frase da mettere accanto al nome della macchina.</summary>
    /// <param name="duration">Da quanto dura il guasto.</param>
    /// <returns>Per esempio <c>under 1 min</c>, <c>3 min</c>, <c>2 h 10 min</c>, <c>2 days 3 h</c>.</returns>
    public static string Describe(TimeSpan duration)
    {
        // Anche una duration negativa: l'orologio di sistema puo' tornare indietro fra una
        // lettura e l'altra, e "under 1 min" e' l'unica cosa vera che si possa dire allora.
        if (duration < TimeSpan.FromMinutes(1))
        {
            return "under 1 min";
        }

        if (duration < TimeSpan.FromHours(1))
        {
            return Format(duration.Minutes) + " min";
        }

        if (duration < TimeSpan.FromDays(1))
        {
            return Append(Format((int)duration.TotalHours) + " h", duration.Minutes, " min");
        }

        int days = duration.Days;
        string head = Format(days) + (days == 1 ? " day" : " days");

        return Append(head, duration.Hours, " h");
    }

    private static string Append(string head, int tail, string unit) =>
        tail == 0 ? head : head + " " + Format(tail) + unit;

    // CA1305 e' vivo perche' InvariantGlobalization non e' impostata: un numero senza cultura
    // esplicita non compila. Invariante e non corrente, perche' questa frase e' testo
    // dell'interfaccia, che in Observer e' in inglese.
    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}
