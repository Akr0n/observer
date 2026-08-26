namespace Observer.Service.Persistence;

/// <summary>
/// Configurazione dello storico. Ogni durata qui dentro e' una SCELTA, non una verita': sono
/// esposte proprio perche' chi installa il servizio possa cambiarle senza toccare il codice.
/// </summary>
public sealed class StorageOptions
{
    /// <summary>Sezione di configurazione da cui si legge.</summary>
    public const string SectionName = "Observer:Storage";

    /// <summary>Se false il servizio funziona esattamente come prima, senza scrivere nulla.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Percorso del file. Relativo, viene risolto rispetto alla cartella di lavoro del
    /// processo. I file *.db, *.db-wal e *.db-shm sono gia' esclusi da git.
    /// </summary>
    public string DatabasePath { get; set; } = "observer.db";

    /// <summary>
    /// Per quanto si tiene il campionamento al secondo. E' il parametro che decide quanto
    /// cresce il file: a 1 Hz il grezzo e' circa 3600 righe l'ora PER SERIE.
    /// </summary>
    public TimeSpan RawRetention { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Per quanto si tengono i bucket da un minuto.</summary>
    public TimeSpan MinuteRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Per quanto si tengono i bucket da cinque minuti.</summary>
    public TimeSpan FiveMinuteRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Ogni quanto girano consolidamento e cancellazione.</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Quanto si aspetta dopo la chiusura di un bucket prima di consolidarlo. Copre il tempo
    /// che un campione passa nella coda in memoria prima di arrivare su disco.
    /// </summary>
    public TimeSpan ConsolidationGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Quanto tempo di storico si consolida al massimo in un solo giro. Serve dopo un lungo
    /// fermo: senza questo limite il primo giro proverebbe ad aggregare ore di dati in
    /// un'unica transazione.
    /// </summary>
    public TimeSpan MaxSpanPerPass { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Quanti snapshot possono aspettare in coda prima che i piu' vecchi vengano scartati.
    /// La coda esiste perche' il campionatore a 1 Hz non deve MAI aspettare il disco.
    /// </summary>
    public int QueueCapacity { get; set; } = 240;

    /// <summary>Quanti punti al massimo puo' restituire una singola interrogazione.</summary>
    public int MaxHistoryPoints { get; set; } = 5000;

    /// <summary>
    /// Controlla la configurazione all'avvio. Fallisce subito e rumorosamente: una ritenzione
    /// a zero non romperebbe nulla, cancellerebbe solo tutto lo storico in silenzio.
    /// </summary>
    /// <exception cref="InvalidOperationException">Se un valore non e' utilizzabile.</exception>
    public void Validate()
    {
        RequirePositive(RawRetention, nameof(RawRetention));
        RequirePositive(MinuteRetention, nameof(MinuteRetention));
        RequirePositive(FiveMinuteRetention, nameof(FiveMinuteRetention));
        RequirePositive(MaintenanceInterval, nameof(MaintenanceInterval));
        RequirePositive(MaxSpanPerPass, nameof(MaxSpanPerPass));

        if (ConsolidationGrace < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"{SectionName}:{nameof(ConsolidationGrace)} non puo' essere negativo."));
        }

        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"{SectionName}:{nameof(DatabasePath)} non puo' essere vuoto."));
        }

        if (QueueCapacity < 1)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"{SectionName}:{nameof(QueueCapacity)} deve essere almeno 1."));
        }

        if (MaxHistoryPoints < 1)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"{SectionName}:{nameof(MaxHistoryPoints)} deve essere almeno 1."));
        }
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"{SectionName}:{name} deve essere positivo, invece vale {value}."));
        }
    }
}
