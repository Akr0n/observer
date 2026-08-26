namespace Observer.Service.Persistence;

/// <summary>
/// Sceglie la risoluzione con cui rispondere a un'interrogazione di storico.
/// </summary>
/// <remarks>
/// Serve perche' il client non puo' saperlo: chiedere un mese a risoluzione un secondo non
/// e' un errore dell'utente, e' semplicemente una domanda a cui va risposto con l'aggregato
/// giusto invece che con due milioni e mezzo di punti.
/// </remarks>
public static class HistoryResolution
{
    /// <summary>Sceglie la risoluzione piu' fine che sta nel limite di punti.</summary>
    /// <param name="from">Inizio della finestra richiesta.</param>
    /// <param name="toExclusive">Fine della finestra richiesta.</param>
    /// <param name="maxPoints">Quanti punti al massimo si vogliono in risposta.</param>
    /// <param name="rawAvailableFrom">
    /// Da quando in poi il grezzo esiste ancora: piu' indietro e' gia' stato cancellato.
    /// </param>
    /// <returns>L'ampiezza in secondi: 1 per il grezzo, 60 o 300 per gli aggregati.</returns>
    /// <exception cref="ArgumentException">Se la finestra e' vuota o rovesciata.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Se il limite di punti non e' positivo.</exception>
    public static int Choose(
        DateTimeOffset from,
        DateTimeOffset toExclusive,
        int maxPoints,
        DateTimeOffset rawAvailableFrom)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoints, 1);

        if (toExclusive <= from)
        {
            throw new ArgumentException(
                "La fine della finestra deve venire dopo l'inizio.",
                nameof(toExclusive));
        }

        double windowSeconds = (toExclusive - from).TotalSeconds;

        foreach (int width in Candidates)
        {
            // Il grezzo si puo' chiedere solo dove esiste ancora. Restituirlo per una
            // finestra gia' cancellata darebbe un grafico vuoto invece di uno aggregato, e
            // "vuoto" si legge come "la macchina non era monitorata".
            if (width == BucketWidths.RawSeconds && from < rawAvailableFrom)
            {
                continue;
            }

            if (windowSeconds / width <= maxPoints)
            {
                return width;
            }
        }

        // Nemmeno il livello piu' grosso ci sta. Non ne esiste uno piu' grosso, quindi si
        // risponde con quello e il limite di righe tronca: un grafico incompleto e' piu'
        // utile di un errore.
        return BucketWidths.FiveMinuteSeconds;
    }

    /// <summary>Dal piu' fine al piu' grosso: si sceglie il primo che ci sta.</summary>
    private static readonly int[] Candidates =
    [
        BucketWidths.RawSeconds,
        BucketWidths.MinuteSeconds,
        BucketWidths.FiveMinuteSeconds,
    ];
}
