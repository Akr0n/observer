using System.Globalization;

namespace Observer.App.Services;

/// <summary>
/// La riga che racconta a una persona cosa si e' persa mentre non guardava.
/// </summary>
/// <remarks>
/// Pura e senza finestra, come <see cref="Downtime"/>: si prova senza disegnare niente. Sta
/// separata da <see cref="HistoryStrip.Assenze"/> di proposito - quella produce i fatti, questa
/// li mette in parole.
/// </remarks>
public static class Riepilogo
{
    /// <summary>La riga per una macchina, vuota quando non c'e' niente da dire.</summary>
    /// <param name="nome">Come si chiama la macchina a schermo.</param>
    /// <param name="assenze">I vuoti trovati nel suo storico.</param>
    /// <param name="colGiorno">Se gli istanti devono portare il giorno della settimana.</param>
    /// <returns>La frase, o stringa vuota.</returns>
    /// <remarks>
    /// <para>
    /// Il silenzio e' un risultato, non un guasto: nessuna riga vuol dire "ho chiesto e non
    /// c'era niente da dire". E' il contrario di un avviso che non compare - li' non si sa se e'
    /// andato tutto bene o se il canale e' rotto - perche' l'impossibilita' di chiedere la
    /// scrive il chiamante con un'altra frase, e quella frase compare.
    /// </para>
    /// <para>
    /// Un vuoto che tocca il bordo vecchio NON si conta fra le interruzioni: la ritenzione
    /// cancella un prefisso, e un prefisso mancante e' indistinguibile da una macchina accesa a
    /// meta' finestra. Chiamarlo "interruzione di tre ore" sarebbe inventare una cosa che
    /// nessuno ha visto, e una sola frase inventata insegna a non fidarsi di tutte le altre.
    /// </para>
    /// </remarks>
    public static string Riga(string nome, IReadOnlyList<Assenza> assenze, bool colGiorno)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        ArgumentNullException.ThrowIfNull(assenze);

        Assenza? bordo = assenze.FirstOrDefault(assenza => assenza.DalBordo);
        Assenza[] vere = [.. assenze.Where(assenza => !assenza.DalBordo)];

        string coda = bordo is null
            ? string.Empty
            : "nothing known before " + Istante(bordo.Fine, colGiorno);

        if (vere.Length == 0)
        {
            return coda.Length == 0 ? string.Empty : $"{nome}: {coda}";
        }

        TimeSpan totale = TimeSpan.Zero;
        Assenza piuLunga = vere[0];

        foreach (Assenza assenza in vere)
        {
            totale += assenza.Durata;

            if (assenza.Durata > piuLunga.Durata)
            {
                piuLunga = assenza;
            }
        }

        string quando = Intervallo(piuLunga, colGiorno);

        // Con una sola interruzione il totale E' quella: ripetere "in 1 period" sarebbe rumore.
        // Con piu' di una il totale da solo mentirebbe per omissione - tre ore in un colpo e tre
        // ore in dieci singhiozzi sono due macchine diverse - quindi si dice quante sono e si
        // mostra la piu' lunga, che e' quella che decide se alzarsi dalla sedia.
        string corpo = vere.Length == 1
            ? $"not measured for {Downtime.Frase(totale)} ({quando})"
            : $"not measured for {Downtime.Frase(totale)} in {vere.Length.ToString(CultureInfo.InvariantCulture)} periods (longest {quando})";

        return coda.Length == 0 ? $"{nome}: {corpo}" : $"{nome}: {corpo}; {coda}";
    }

    /// <summary>I due estremi di un'interruzione, col giorno quando serve davvero.</summary>
    /// <remarks>
    /// Il giorno si mette anche quando <paramref name="colGiorno"/> e' falso ma i due estremi
    /// cadono in due GIORNATE diverse, e non e' pignoleria: qui si stampa l'arco di
    /// un'interruzione intera, non i due lati di una barra. A ventiquattro ore un'assenza puo'
    /// durare quasi l'intera finestra, e senza il giorno la riga direbbe
    /// "not measured for 23 h 45 min (09:25 – 09:10)" - una durata di quasi un giorno accanto a
    /// un intervallo che si legge come un quarto d'ora all'indietro. Succede anche a un'ora, su
    /// una macchina spenta a cavallo di mezzanotte. Per coppia e non per soglia, cosi' e' giusto
    /// in ogni periodo invece che in quelli che si e' pensato di controllare.
    /// </remarks>
    private static string Intervallo(Assenza assenza, bool colGiorno)
    {
        bool giorniDiversi = assenza.Inizio.ToLocalTime().Date != assenza.Fine.ToLocalTime().Date;
        bool conGiorno = colGiorno || giorniDiversi;

        return Istante(assenza.Inizio, conGiorno) + " – " + Istante(assenza.Fine, conGiorno);
    }

    /// <remarks>
    /// Stesso formato di <see cref="HistoryStrip.Descrivi"/>: InvariantCulture perche' cio' che
    /// si vede e' in inglese.
    /// </remarks>
    private static string Istante(DateTimeOffset istante, bool colGiorno) =>
        istante.ToLocalTime().ToString(colGiorno ? "ddd HH:mm" : "HH:mm", CultureInfo.InvariantCulture);
}