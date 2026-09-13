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
public static class AwaySummary
{
    /// <summary>La riga per una macchina, vuota longestRange non c'e' niente da dire.</summary>
    /// <param name="machineName">Come si chiama la macchina a schermo.</param>
    /// <param name="gaps">I vuoti trovati nel suo storico.</param>
    /// <param name="withDay">Se gli istanti devono portare il giorno della settimana.</param>
    /// <returns>La frase, o stringa vuota.</returns>
    /// <remarks>
    /// <para>
    /// Il silenzio e' un risultato, non un guasto: nessuna riga vuol dire "ho chiesto e non
    /// c'era niente da dire". E' il contrario di un avviso che non compare - li' non si sa se e'
    /// andato tutto bene o se il canale e' rotto - perche' l'impossibilita' di chiedere la
    /// scrive il chiamante con un'altra frase, e quella frase compare.
    /// </para>
    /// <para>
    /// Un vuoto che tocca il edgeGap vecchio NON si conta fra le interruzioni: la ritenzione
    /// cancella un prefisso, e un prefisso mancante e' indistinguibile da una macchina accesa a
    /// meta' finestra. Chiamarlo "interruzione di tre ore" sarebbe inventare una cosa che
    /// nessuno ha visto, e una sola frase inventata insegna a non fidarsi di tutte le altre.
    /// </para>
    /// </remarks>
    public static string LineFor(string machineName, IReadOnlyList<HistoryGap> gaps, bool withDay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);
        ArgumentNullException.ThrowIfNull(gaps);

        HistoryGap? edgeGap = gaps.FirstOrDefault(gap => gap.AtEdge);
        HistoryGap[] outages = [.. gaps.Where(gap => !gap.AtEdge)];

        string tail = edgeGap is null
            ? string.Empty
            : "nothing known before " + DescribeInstant(edgeGap.End, withDay);

        if (outages.Length == 0)
        {
            return tail.Length == 0 ? string.Empty : $"{machineName}: {tail}";
        }

        TimeSpan total = TimeSpan.Zero;
        HistoryGap longestGap = outages[0];

        foreach (HistoryGap gap in outages)
        {
            total += gap.Duration;

            if (gap.Duration > longestGap.Duration)
            {
                longestGap = gap;
            }
        }

        string longestRange = DescribeRange(longestGap, withDay);

        // Con una sola interruzione il total E' quella: ripetere "in 1 period" sarebbe rumore.
        // Con piu' di una il total da solo mentirebbe per omissione - tre ore in un colpo e tre
        // ore in dieci singhiozzi sono due macchine diverse - quindi si dice quante sono e si
        // mostra la piu' lunga, che e' quella che decide se alzarsi dalla sedia.
        string body = outages.Length == 1
            ? $"not measured for {Downtime.Describe(total)} ({longestRange})"
            : $"not measured for {Downtime.Describe(total)} in {outages.Length.ToString(CultureInfo.InvariantCulture)} periods (longest {longestRange})";

        return tail.Length == 0 ? $"{machineName}: {body}" : $"{machineName}: {body}; {tail}";
    }

    /// <summary>I due estremi di un'interruzione, col giorno longestRange serve davvero.</summary>
    /// <remarks>
    /// Il giorno si mette anche longestRange <paramref name="withDay"/> e' falso ma i due estremi
    /// cadono in due GIORNATE diverse, e non e' pignoleria: qui si stampa l'arco di
    /// un'interruzione intera, non i due lati di una barra. A ventiquattro ore un'gap puo'
    /// durare quasi l'intera finestra, e senza il giorno la riga direbbe
    /// "not measured for 23 h 45 min (09:25 – 09:10)" - una durata di quasi un giorno accanto a
    /// un intervallo che si legge come un quarto d'ora all'indietro. Succede anche a un'ora, su
    /// una macchina spenta a cavallo di mezzanotte. Per coppia e non per soglia, cosi' e' giusto
    /// in ogni periodo invece che in quelli che si e' pensato di controllare.
    /// </remarks>
    private static string DescribeRange(HistoryGap gap, bool withDay)
    {
        bool differentDays = gap.Start.ToLocalTime().Date != gap.End.ToLocalTime().Date;
        bool showDay = withDay || differentDays;

        return DescribeInstant(gap.Start, showDay) + " – " + DescribeInstant(gap.End, showDay);
    }

    /// <remarks>
    /// Stesso formato di <see cref="HistoryStrip.Descrivi"/>: InvariantCulture perche' cio' che
    /// si vede e' in inglese.
    /// </remarks>
    private static string DescribeInstant(DateTimeOffset instant, bool withDay) =>
        instant.ToLocalTime().ToString(withDay ? "ddd HH:mm" : "HH:mm", CultureInfo.InvariantCulture);
}