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
    /// Percorso del file. Se relativo, viene risolto da <see cref="ResolveDatabasePath"/>
    /// sotto la cartella dati dell'utente, MAI sulla cartella di lavoro del processo.
    /// I file *.db, *.db-wal e *.db-shm sono gia' esclusi da git.
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
    /// <remarks>
    /// Deve coprire il tempo che un campionamento puo' passare in coda prima di arrivare su
    /// disco, altrimenti un campione in ritardo non entra mai nella media del suo intervallo
    /// e poco dopo il grezzo viene cancellato: resta un numero credibile calcolato su meta'
    /// dei campioni. Il predefinito e' allineato a <see cref="QueueCapacity"/>, che a 1 Hz
    /// vale altrettanti secondi, e la coerenza fra i due e' imposta da <see cref="Validate"/>.
    /// </remarks>
    public TimeSpan ConsolidationGrace { get; set; } = TimeSpan.FromSeconds(240);

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
    /// Il percorso assoluto del database. Un percorso gia' assoluto viene rispettato; uno
    /// relativo viene risolto sotto la cartella dati dell'utente, MAI sulla cartella di lavoro.
    /// </summary>
    /// <remarks>
    /// Un servizio di sistema non ha una cartella di lavoro prevedibile: su Windows parte da
    /// system32, sotto systemd da "/" salvo direttive esplicite. Con un percorso relativo il
    /// database finirebbe in un posto diverso a seconda di come il servizio e' stato avviato —
    /// e in sviluppo dentro l'albero dei sorgenti — dando l'impressione di aver perso lo
    /// storico ogni volta che cambia il modo di avvio.
    /// </remarks>
    /// <returns>Il percorso assoluto del file SQLite.</returns>
    public string ResolveDatabasePath()
    {
        if (Path.IsPathRooted(DatabasePath))
        {
            return DatabasePath;
        }

        // LocalApplicationData e' scrivibile sia da un utente sia da un account di servizio,
        // su entrambe le piattaforme: %LOCALAPPDATA% su Windows, ~/.local/share su Linux.
        // /var/lib sarebbe piu' ortodosso per un servizio di sistema Linux, ma richiede
        // privilegi che qui non vogliamo pretendere.
        string baseDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        return Path.Combine(baseDirectory, "Observer", DatabasePath);
    }

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
                FormattableString.Invariant($"{SectionName}:{nameof(ConsolidationGrace)} cannot be negative."));
        }

        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"{SectionName}:{nameof(DatabasePath)} cannot be empty."));
        }

        if (QueueCapacity < 1)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"{SectionName}:{nameof(QueueCapacity)} must be at least 1."));
        }

        if (MaxHistoryPoints < 1)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"{SectionName}:{nameof(MaxHistoryPoints)} must be at least 1."));
        }

        // La coda puo' trattenere QueueCapacity campionamenti — a 1 Hz, altrettanti secondi —
        // prima che raggiungano il disco. Se il consolidamento chiude un intervallo prima che
        // quei campioni siano arrivati, non ci rientrano piu' e poco dopo il grezzo viene
        // cancellato: resta una media credibile calcolata su una parte dei campioni, senza
        // eccezioni ne' log. E' un errore che nessuno puo' vedere guardando un grafico, quindi
        // va impedito qui, all'avvio, dove si nota subito.
        TimeSpan worstCaseQueueDelay = TimeSpan.FromSeconds(QueueCapacity);

        if (ConsolidationGrace < worstCaseQueueDelay)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"{SectionName}:{nameof(ConsolidationGrace)} is {ConsolidationGrace.TotalSeconds:0} s but must be at least {worstCaseQueueDelay.TotalSeconds:0} s, because {SectionName}:{nameof(QueueCapacity)} is {QueueCapacity} and a sample can wait that long before it is written to disk. A shorter grace period would leave late samples out of their own average."));
        }
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"{SectionName}:{name} must be positive, but is {value}."));
        }
    }
}
