namespace Observer.Service.Persistence;

/// <summary>
/// Decide COSA e' consolidabile e COSA e' cancellabile. Logica pura, senza database: sono le
/// due domande le cui risposte sbagliate non fanno fallire nulla — una consolida un bucket a
/// meta' e produce medie false per sempre, l'altra cancella dati che nessuno aveva ancora
/// aggregato.
/// </summary>
public static class RetentionPolicy
{
    /// <summary>
    /// Primo istante NON consolidabile: il limite superiore esclusivo della finestra da
    /// aggregare adesso.
    /// </summary>
    /// <param name="nowMs">Adesso, in millisecondi da Unix epoch (UTC).</param>
    /// <param name="bucketWidth">Ampiezza dei bucket da produrre.</param>
    /// <param name="grace">
    /// Attesa aggiuntiva dopo la chiusura di un bucket. Serve perche' i campioni dell'ultimo
    /// secondo passano da una coda in memoria e potrebbero non essere ancora su disco.
    /// </param>
    /// <returns>L'inizio del primo bucket ancora intoccabile.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Se la grazia e' negativa.</exception>
    public static long ConsolidationHorizon(long nowMs, TimeSpan bucketWidth, TimeSpan grace)
    {
        // Una grazia negativa guarderebbe nel futuro e consoliderebbe bucket ancora vuoti.
        ArgumentOutOfRangeException.ThrowIfLessThan(grace, TimeSpan.Zero);

        // Allineare "adesso meno la grazia" all'ampiezza fa due cose in un colpo solo:
        // esclude il bucket in corso (che e' incompleto per definizione) e, se la grazia
        // sconfina nel bucket precedente, esclude anche quello (che e' completo ma potrebbe
        // avere campioni ancora in coda).
        return RollupMath.AlignToBucketStart(nowMs - (long)grace.TotalMilliseconds, bucketWidth);
    }

    /// <summary>
    /// Istante sotto il quale si puo' cancellare. Tutto cio' che e' antecedente e' eliminabile.
    /// </summary>
    /// <param name="nowMs">Adesso, in millisecondi da Unix epoch (UTC).</param>
    /// <param name="retention">Per quanto tempo si vuole conservare questo livello.</param>
    /// <param name="consolidatedThroughMs">
    /// Fin dove il livello SUCCESSIVO ha gia' aggregato, oppure null se non ha mai girato.
    /// </param>
    /// <returns>La soglia, oppure null se non si deve cancellare nulla.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Se la ritenzione non e' positiva.</exception>
    public static long? PurgeCutoff(long nowMs, TimeSpan retention, long? consolidatedThroughMs)
    {
        // Una ritenzione a zero cancellerebbe nello stesso istante in cui si scrive: il
        // servizio girerebbe, il file resterebbe piccolo e lo storico sarebbe sempre vuoto.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);

        if (consolidatedThroughMs is not { } consolidated)
        {
            // Il livello successivo non ha mai aggregato nulla: qui non esiste ancora una
            // copia riassunta di questi dati, quindi cancellarli e' una perdita secca.
            return null;
        }

        // Il vincolo che conta e' il piu' stretto dei due. La ritenzione dice "sono
        // abbastanza vecchi", il consolidamento dice "sono gia' riassunti altrove": servono
        // ENTRAMBI, altrimenti un rollup rimasto indietro fa cancellare dati mai aggregati.
        return Math.Min(nowMs - (long)retention.TotalMilliseconds, consolidated);
    }
}
