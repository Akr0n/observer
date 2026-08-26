using Observer.Core.Metrics;
using Observer.Service.Persistence;

namespace Observer.Service;

/// <summary>
/// Conserva l'ultimo campionamento. Gli endpoint HTTP leggono DA QUI e non chiamano mai
/// direttamente i collector.
/// </summary>
/// <remarks>
/// Non e' un dettaglio di prestazioni, e' una difesa dalla concorrenza. Il collector CPU
/// tiene il campione precedente in un campo: due raccolte simultanee calcolerebbero
/// percentuali sbagliate in modo intermittente e plausibile, il peggior tipo di bug. Con un
/// solo campionatore e letture dalla cache, quella situazione non puo' verificarsi.
/// </remarks>
public sealed class MetricSnapshotCache
{
    private MachineSnapshot? latest;

    /// <summary>L'ultimo campionamento, oppure null se non e' ancora avvenuto.</summary>
    public MachineSnapshot? Latest => Volatile.Read(ref latest);

    /// <summary>Pubblica un nuovo campionamento.</summary>
    public void Publish(MachineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref latest, snapshot);
    }
}

/// <summary>
/// L'UNICO campionatore del processo. Interroga i collector a intervallo fisso e pubblica
/// il risultato nella cache.
/// </summary>
public sealed partial class MetricSamplingService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <remarks>
    /// Sotto il periodo di campionamento, cosi' una sorgente lenta non manda in ritardo
    /// tutte le altre. ATTENZIONE al limite reale: una P/Invoke o una query WMI che si
    /// pianta NON osserva il token di annullamento. Il thread resta occupato e la scadenza
    /// vale solo dal punto di vista del campionatore. Per sorgenti note per bloccarsi —
    /// SMART, WMI — la soluzione vera e' un processo separato, non un CancellationToken.
    /// </remarks>
    private static readonly TimeSpan CollectorTimeout = TimeSpan.FromMilliseconds(750);

    private readonly IReadOnlyList<IMetricCollector> collectors;
    private readonly MetricSnapshotCache cache;
    private readonly IMetricSnapshotSink sink;
    private readonly ILogger<MetricSamplingService> logger;

    /// <summary>Crea il campionatore.</summary>
    /// <param name="collectors">Le sorgenti da interrogare.</param>
    /// <param name="cache">Dove pubblicare l'ultimo campionamento per gli endpoint.</param>
    /// <param name="sink">
    /// Dove depositare lo stesso campionamento perche' finisca nello storico. Deposita e
    /// basta: se aspettasse il disco, un fsync lento non renderebbe il grafico lento,
    /// falserebbe la percentuale di CPU della lettura successiva, che si calcola sulla
    /// DISTANZA fra due campionamenti.
    /// </param>
    /// <param name="logger">Dove segnalare i collector lenti o guasti.</param>
    public MetricSamplingService(
        IReadOnlyList<IMetricCollector> collectors,
        MetricSnapshotCache cache,
        IMetricSnapshotSink sink,
        ILogger<MetricSamplingService> logger)
    {
        ArgumentNullException.ThrowIfNull(collectors);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(logger);

        this.collectors = collectors;
        this.cache = cache;
        this.sink = sink;
        this.logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Restituisce subito il controllo all'host: cosi' una prima raccolta lenta ritarda
        // la prima metrica, non l'apertura della porta HTTP.
        await Task.Yield();

        using PeriodicTimer timer = new(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            MachineSnapshot snapshot = await CollectAllAsync(stoppingToken).ConfigureAwait(false);

            cache.Publish(snapshot);

            // Lo STESSO oggetto va anche allo storico: cosi' il grafico di un istante e la
            // piastrella del presente non possono mostrare numeri diversi. Enqueue non
            // aspetta il disco, per costruzione.
            sink.Enqueue(snapshot);

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Arresto richiesto: uscita normale, non un errore.
                return;
            }
        }
    }

    private async Task<MachineSnapshot> CollectAllAsync(CancellationToken cancellationToken)
    {
        List<MetricSnapshot> results = new(collectors.Count);

        foreach (IMetricCollector collector in collectors)
        {
            using CancellationTokenSource attempt =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            attempt.CancelAfter(CollectorTimeout);

            try
            {
                results.Add(await collector.CollectAsync(attempt.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Arresto del servizio: propaga, non e' un guasto della metrica.
                throw;
            }
            catch (OperationCanceledException)
            {
                // Scaduto il tempo: la sorgente e' lenta, non rotta. Le altre proseguono.
                LogCollectorTimedOut(logger, collector.Id, CollectorTimeout.TotalMilliseconds);
                results.Add(new MetricSnapshot(
                    collector.Id,
                    CollectorStatus.Unavailable,
                    FormattableString.Invariant(
                        $"the source didn't respond within {CollectorTimeout.TotalMilliseconds} ms and was skipped for this round"),
                    []));
            }
#pragma warning disable CA1031 // Un collector che esplode deve degradare una piastrella, non
            catch (Exception ex) // abbattere il campionamento di tutte le altre metriche.
#pragma warning restore CA1031
            {
                LogCollectorFaulted(logger, collector.Id, ex);
                results.Add(new MetricSnapshot(collector.Id, CollectorStatus.Faulted, ex.Message, []));
            }
        }

        return new MachineSnapshot(MachineSnapshot.CurrentSchemaVersion, DateTimeOffset.UtcNow, results);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Collector {CollectorId} threw an exception: its metric is degraded, everything else continues.")]
    private static partial void LogCollectorFaulted(ILogger logger, string collectorId, Exception exception);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Collector {CollectorId} exceeded {TimeoutMs} ms: skipped for this round, the other metrics continue.")]
    private static partial void LogCollectorTimedOut(ILogger logger, string collectorId, double timeoutMs);
}
