using Observer.Core.Metrics;

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
    private readonly ILogger<MetricSamplingService> logger;

    /// <summary>Crea il campionatore.</summary>
    public MetricSamplingService(
        IReadOnlyList<IMetricCollector> collectors,
        MetricSnapshotCache cache,
        ILogger<MetricSamplingService> logger)
    {
        ArgumentNullException.ThrowIfNull(collectors);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        this.collectors = collectors;
        this.cache = cache;
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
            cache.Publish(await CollectAllAsync(stoppingToken).ConfigureAwait(false));

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
                        $"la sorgente non ha risposto entro {CollectorTimeout.TotalMilliseconds} ms ed e' stata abbandonata per questo giro"),
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
        Message = "Il collector {CollectorId} ha lanciato un'eccezione: la sua metrica risulta degradata, il resto continua.")]
    private static partial void LogCollectorFaulted(ILogger logger, string collectorId, Exception exception);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Il collector {CollectorId} ha superato i {TimeoutMs} ms: saltato per questo giro, le altre metriche proseguono.")]
    private static partial void LogCollectorTimedOut(ILogger logger, string collectorId, double timeoutMs);
}
