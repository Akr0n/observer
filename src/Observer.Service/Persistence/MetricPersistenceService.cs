namespace Observer.Service.Persistence;

/// <summary>
/// L'UNICO scrittore del database. Svuota la coda a intervallo fisso e, ogni tanto, consolida
/// e cancella.
/// </summary>
/// <remarks>
/// <para>
/// Un solo scrittore non e' una scelta di prestazioni: SQLite serializza comunque le
/// scritture, e con due scrittori l'unica differenza sarebbe una coppia di transazioni che
/// si aspettano a vicenda, piu' un errore SQLITE_BUSY da gestire in un punto in cui non
/// serve.
/// </para>
/// <para>
/// Consolidamento e cancellazione girano su questo stesso ciclo, quindi non possono
/// sovrapporsi a una scrittura. Se un giro di manutenzione e' lento, la coda accumula — e se
/// arriva a riempirsi scarta i piu' vecchi. Il campionatore, in nessuno di questi casi,
/// aspetta.
/// </para>
/// </remarks>
public sealed partial class MetricPersistenceService : BackgroundService
{
    /// <summary>
    /// Ogni quanto la coda finisce su disco. Un secondo: piu' spesso sarebbe una transazione
    /// per campione, piu' di rado allungherebbe solo la finestra di dati che un arresto
    /// brutale porta via.
    /// </summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly MetricWriter writer;
    private readonly MetricStore store;
    private readonly SnapshotBuffer buffer;
    private readonly StorageOptions options;
    private readonly ILogger<MetricPersistenceService> logger;

    private long lastReportedDrops;

    /// <summary>Crea il servizio di persistenza.</summary>
    /// <param name="writer">Chi svuota la coda sul database.</param>
    /// <param name="store">Il magazzino, per la manutenzione.</param>
    /// <param name="buffer">La coda, per sapere quanto si sta scartando.</param>
    /// <param name="options">La configurazione dello storico.</param>
    /// <param name="logger">Dove segnalare guasti e scarti.</param>
    public MetricPersistenceService(
        MetricWriter writer,
        MetricStore store,
        SnapshotBuffer buffer,
        StorageOptions options,
        ILogger<MetricPersistenceService> logger)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.writer = writer;
        this.store = store;
        this.buffer = buffer;
        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PRIMA di qualunque await, quindi ancora dentro StartAsync: se il file non si puo'
        // creare, il servizio non parte e lo si vede subito. Un servizio che parte e non
        // conserva niente e' molto peggio di uno che non parte.
        store.Initialize();

        await Task.Yield();

        DateTimeOffset nextMaintenance = DateTimeOffset.UtcNow + options.MaintenanceInterval;
        using PeriodicTimer timer = new(FlushInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            FlushSafely();
            ReportDrops();

            DateTimeOffset now = DateTimeOffset.UtcNow;

            if (now >= nextMaintenance)
            {
                MaintainSafely(now);
                nextMaintenance = now + options.MaintenanceInterval;
            }
        }

        // Un ultimo giro: cio' che e' rimasto in coda vale quanto il resto, e qui non c'e'
        // piu' nessun campionatore da non far aspettare.
        FlushSafely();
    }

    private void FlushSafely()
    {
        try
        {
            writer.FlushPending();
        }
#pragma warning disable CA1031 // Un disco pieno o un file agganciato devono far perdere un
        catch (Exception ex) // giro di storico, non fermare il monitoraggio dal vivo.
#pragma warning restore CA1031
        {
            LogFlushFailed(logger, ex);
        }
    }

    private void MaintainSafely(DateTimeOffset now)
    {
        try
        {
            MaintenanceReport report = store.RunMaintenance(now, options);
            LogMaintenance(
                logger,
                report.MinuteBucketsWritten,
                report.FiveMinuteBucketsWritten,
                report.RawRowsPurged);
        }
#pragma warning disable CA1031 // Idem: la manutenzione saltata si recupera al giro dopo,
        catch (Exception ex) // perche' il segnaposto non avanza se la transazione fallisce.
#pragma warning restore CA1031
        {
            LogMaintenanceFailed(logger, ex);
        }
    }

    private void ReportDrops()
    {
        long dropped = buffer.DroppedCount;

        if (dropped == lastReportedDrops)
        {
            return;
        }

        // Uno storico con buchi va DETTO: altrimenti e' indistinguibile da uno storico in
        // cui non e' successo niente.
        LogDropped(logger, dropped - lastReportedDrops, dropped);
        lastReportedDrops = dropped;
    }

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Error,
        Message = "Scrittura dello storico fallita: questo giro di campioni e' perso, il monitoraggio dal vivo continua.")]
    private static partial void LogFlushFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Error,
        Message = "Manutenzione dello storico fallita: nessun dato e' stato cancellato e il consolidamento riprendera' al giro successivo.")]
    private static partial void LogMaintenanceFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Debug,
        Message = "Manutenzione: {MinuteBuckets} bucket da un minuto, {FiveMinuteBuckets} da cinque, {RawPurged} campioni grezzi cancellati.")]
    private static partial void LogMaintenance(ILogger logger, int minuteBuckets, int fiveMinuteBuckets, int rawPurged);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Warning,
        Message = "Lo storico ha scartato {NewDrops} campionamenti (totale {TotalDrops}): la scrittura su disco non sta al passo del campionatore.")]
    private static partial void LogDropped(ILogger logger, long newDrops, long totalDrops);
}
