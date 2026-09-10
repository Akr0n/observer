using System.Globalization;

namespace Observer.App.Services;

/// <summary>
/// Da quanto dura un guasto, detto in una frase corta.
/// </summary>
/// <remarks>
/// Sta qui e non nel view model per la stessa ragione di <see cref="StatusEscalation"/> e di
/// <c>PosizioneFinestra</c>: e' una regola che si puo' provare senza una finestra, e un refuso
/// qui non fa rumore — mostra un numero plausibile e sbagliato accanto a una macchina giu'.
/// <para>
/// La durata si TRONCA, non si arrotonda, ed e' una scelta che tiene onesto anche un testo in
/// ritardo. La riga si aggiorna quando arriva una lettura, cioe' ogni quindici secondi per le
/// macchine non guardate e ogni dieci quando la finestra e' ridotta a icona: un testo
/// arrotondato per eccesso direbbe "3 min" quando ne sono passati 2 e 31 s, mentre troncando
/// il numero mostrato e' sempre un limite INFERIORE della durata vera. Chi legge "2 min" sa
/// che sono almeno due minuti, e non e' mai smentito.
/// </para>
/// <para>
/// Al massimo due unita', e la seconda solo se non e' zero: "2 h 10 min" dice quanto serve,
/// "2 h 10 min 33 s" chiede di leggere tre numeri per sapere una cosa sola. I secondi non
/// compaiono mai, perche' con letture ogni dieci o quindici secondi sarebbero una precisione
/// che il dato non ha.
/// </para>
/// </remarks>
public static class Downtime
{
    /// <summary>La durata, in una frase da mettere accanto al nome della macchina.</summary>
    /// <param name="durata">Da quanto dura il guasto.</param>
    /// <returns>Per esempio <c>under 1 min</c>, <c>3 min</c>, <c>2 h 10 min</c>, <c>2 days 3 h</c>.</returns>
    public static string Frase(TimeSpan durata)
    {
        // Anche una durata negativa: l'orologio di sistema puo' tornare indietro fra una
        // lettura e l'altra, e "under 1 min" e' l'unica cosa vera che si possa dire allora.
        if (durata < TimeSpan.FromMinutes(1))
        {
            return "under 1 min";
        }

        if (durata < TimeSpan.FromHours(1))
        {
            return Numero(durata.Minutes) + " min";
        }

        if (durata < TimeSpan.FromDays(1))
        {
            return Componi(Numero((int)durata.TotalHours) + " h", durata.Minutes, " min");
        }

        int giorni = durata.Days;
        string testa = Numero(giorni) + (giorni == 1 ? " day" : " days");

        return Componi(testa, durata.Hours, " h");
    }

    private static string Componi(string testa, int coda, string unita) =>
        coda == 0 ? testa : testa + " " + Numero(coda) + unita;

    // CA1305 e' vivo perche' InvariantGlobalization non e' impostata: un numero senza cultura
    // esplicita non compila. Invariante e non corrente, perche' questa frase e' testo
    // dell'interfaccia, che in Observer e' in inglese.
    private static string Numero(int quanti) => quanti.ToString(CultureInfo.InvariantCulture);
}
