using System.Diagnostics;
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
            long inizio = Stopwatch.GetTimestamp();

            MachineSnapshot snapshot = await CollectAllAsync(stoppingToken).ConfigureAwait(false);

            // Un giro piu' lungo del periodo fa cadere un tick, e PeriodicTimer lo lascia
            // cadere in SILENZIO: il campione non c'e', e a valle si legge come un momento in
            // cui non si stava misurando - indistinguibile da una macchina spenta. Se succede,
            // che almeno resti scritto da qualche parte quale sorgente ha allungato il giro.
            TimeSpan durata = Stopwatch.GetElapsedTime(inizio);

            if (durata > Interval)
            {
                LogGiroTroppoLungo(logger, durata.TotalMilliseconds, Interval.TotalMilliseconds);
            }

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

    /// <summary>
    /// Interroga TUTTE le sorgenti insieme e aspetta che abbiano finito.
    /// </summary>
    /// <remarks>
    /// <b>Insieme, non una dopo l'altra, e la differenza cresce con ogni sorgente nuova.</b>
    /// In sequenza il giro dura la SOMMA dei tempi, quindi il caso peggiore e' il numero di
    /// collector moltiplicato per <see cref="CollectorTimeout"/>: con due gia' supera il
    /// secondo, con cinque lo quadruplica. E un giro piu' lungo del periodo non fa rumore -
    /// <see cref="PeriodicTimer"/> lascia cadere i tick in silenzio, i campioni spariscono, e
    /// la striscia dello storico dichiara "non misurato" un'ora in cui la macchina era accesa
    /// e sana. Insieme, il caso peggiore e' il collector piu' lento, e resta sotto il periodo
    /// per costruzione.
    /// <para>
    /// Non introduce la concorrenza che <see cref="MetricSnapshotCache"/> teme: quella nasce
    /// da due raccolte <i>dello stesso</i> collector che si sovrappongono, e qui ogni sorgente
    /// viene interrogata una volta sola per giro. E' il ciclo a restare unico, non la fila.
    /// </para>
    /// </remarks>
    private async Task<MachineSnapshot> CollectAllAsync(CancellationToken cancellationToken)
    {
        // L'ordine dei risultati resta quello dei collector, perche' WhenAll conserva
        // l'ordine dei task: i riquadri a schermo non si scambiano di posto a ogni giro.
        MetricSnapshot[] results = await Task.WhenAll(
            collectors.Select(collector => CollectOneAsync(collector, cancellationToken)))
            .ConfigureAwait(false);

        return new MachineSnapshot(MachineSnapshot.CurrentSchemaVersion, DateTimeOffset.UtcNow, results);
    }

    /// <summary>Interroga una sorgente, e non lascia mai passare un guasto suo.</summary>
    private async Task<MetricSnapshot> CollectOneAsync(
        IMetricCollector collector,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource attempt =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        attempt.CancelAfter(CollectorTimeout);

        try
        {
            return await collector.CollectAsync(attempt.Token).ConfigureAwait(false);
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

            return new MetricSnapshot(
                collector.Id,
                CollectorStatus.Unavailable,
                FormattableString.Invariant(
                    $"the source didn't respond within {CollectorTimeout.TotalMilliseconds} ms and was skipped for this round"),
                []);
        }
#pragma warning disable CA1031 // Un collector che esplode deve degradare una piastrella, non
        catch (Exception ex) // abbattere il campionamento di tutte le altre metriche.
#pragma warning restore CA1031
        {
            LogCollectorFaulted(logger, collector.Id, ex);

            return new MetricSnapshot(collector.Id, CollectorStatus.Faulted, ex.Message, []);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Collector {CollectorId} threw an exception: its metric is degraded, everything else continues.")]
    private static partial void LogCollectorFaulted(ILogger logger, string collectorId, Exception exception);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "A sampling round took {ElapsedMs} ms, longer than the {IntervalMs} ms period: at least one sample was skipped, and a skipped sample is indistinguishable from a machine that was off.")]
    private static partial void LogGiroTroppoLungo(ILogger logger, double elapsedMs, double intervalMs);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Collector {CollectorId} exceeded {TimeoutMs} ms: skipped for this round, the other metrics continue.")]
    private static partial void LogCollectorTimedOut(ILogger logger, string collectorId, double timeoutMs);
}