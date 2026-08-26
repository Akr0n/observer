using Observer.Core.Metrics;
using Observer.Service.Persistence;

namespace Observer.Service;

/// <summary>Gli endpoint che espongono lo storico.</summary>
/// <remarks>
/// Vengono mappati DOPO il middleware di autenticazione, come quelli gia' esistenti: lo
/// storico di una macchina dice quando e' accesa, quanto lavora e quando nessuno la usa, che
/// e' piu' di quanto dica un singolo campionamento.
/// </remarks>
public static class StorageEndpoints
{
    /// <summary>Finestra usata quando la richiesta non dice da quando a quando.</summary>
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(1);

    /// <summary>Mappa /metrics/series, /metrics/history e /metrics/storage.</summary>
    /// <param name="endpoints">Il costruttore di rotte dell'applicazione.</param>
    public static void MapStorageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Quali serie esistono nello storico. E' l'equivalente di /metrics/catalog per il
        // passato: il catalogo dice cosa il servizio SA misurare, questo dice cosa ha
        // effettivamente misurato su questa macchina.
        endpoints.MapGet("/metrics/series", (MetricStore store, StorageOptions options) =>
            options.Enabled
                ? Results.Ok(store.ListSeries().Select(ToResponse).ToList())
                : Disabled());

        endpoints.MapGet("/metrics/history", (
            MetricStore store,
            StorageOptions options,
            string? collector,
            string? metric,
            string? instance,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? resolution) =>
            History(store, options, collector, metric, instance, from, to, resolution));

        endpoints.MapGet("/metrics/storage", (
            MetricStore store,
            StorageOptions options,
            SnapshotBuffer buffer) => Storage(store, options, buffer));
    }

    private static IResult History(
        MetricStore store,
        StorageOptions options,
        string? collector,
        string? metric,
        string? instance,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? resolution)
    {
        if (!options.Enabled)
        {
            return Disabled();
        }

        if (string.IsNullOrWhiteSpace(collector) || string.IsNullOrWhiteSpace(metric))
        {
            return Problem("Servono sia 'collector' sia 'metric': senza, non si sa quale serie leggere.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset upper = to ?? now;
        DateTimeOffset lower = from ?? upper - DefaultWindow;

        if (upper <= lower)
        {
            return Problem("'to' deve venire dopo 'from': la finestra richiesta e' vuota o rovesciata.");
        }

        int bucketSeconds;

        if (IsAuto(resolution))
        {
            // Il grezzo piu' vecchio della ritenzione e' gia' stato cancellato: chiederlo
            // darebbe un grafico vuoto, che si legge come "macchina non monitorata".
            bucketSeconds = HistoryResolution.Choose(
                lower, upper, options.MaxHistoryPoints, now - options.RawRetention);
        }
        else if (!TryParseResolution(resolution, out bucketSeconds))
        {
            return Problem(
                "Risoluzione sconosciuta: sono previste 'auto', 'raw', '1m' e '5m'.");
        }

        IReadOnlyList<HistoryPoint> points = store.ReadHistory(
            new SeriesKey(collector, metric, instance ?? string.Empty),
            bucketSeconds,
            lower,
            upper,
            options.MaxHistoryPoints);

        return Results.Ok(new HistoryResponse(
            collector,
            metric,
            string.IsNullOrEmpty(instance) ? null : instance,
            ResolutionLabel(bucketSeconds),
            bucketSeconds,
            lower,
            upper,

            // Dichiarare il troncamento e' cio' che distingue "il grafico finisce qui"
            // da "qui la macchina era spenta".
            points.Count >= options.MaxHistoryPoints,
            points.Select(point => new HistoryPointResponse(
                point.Timestamp,
                point.Count,
                point.Average,
                point.Min,
                point.Max,
                point.Last)).ToList()));
    }

    private static IResult Storage(MetricStore store, StorageOptions options, SnapshotBuffer buffer)
    {
        if (!options.Enabled)
        {
            return Disabled();
        }

        StorageStats stats = store.ReadStats();

        return Results.Ok(new StorageResponse(
            Enabled: true,
            stats.DatabasePath,
            stats.FileSizeBytes,
            stats.SeriesCount,
            stats.RawSamples,
            stats.MinuteBuckets,
            stats.FiveMinuteBuckets,
            stats.MinuteConsolidatedThrough,
            stats.FiveMinuteConsolidatedThrough,
            buffer.DroppedCount,
            new RetentionResponse(
                options.RawRetention,
                options.MinuteRetention,
                options.FiveMinuteRetention)));
    }

    private static StoredSeriesResponse ToResponse(StoredSeries series) =>
        new(
            series.Key.CollectorId,
            series.Key.MetricId,

            // Sul filo l'istanza assente e' null, come in MetricPoint: la stringa vuota vive
            // solo dentro il database, dove serve all'indice UNIQUE.
            string.IsNullOrEmpty(series.Key.Instance) ? null : series.Key.Instance,
            (int)series.Kind);

    private static bool IsAuto(string? resolution) =>
        string.IsNullOrWhiteSpace(resolution)
        || string.Equals(resolution, "auto", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseResolution(string? resolution, out int bucketSeconds)
    {
        if (string.Equals(resolution, "raw", StringComparison.OrdinalIgnoreCase))
        {
            bucketSeconds = BucketWidths.RawSeconds;
            return true;
        }

        if (string.Equals(resolution, "1m", StringComparison.OrdinalIgnoreCase))
        {
            bucketSeconds = BucketWidths.MinuteSeconds;
            return true;
        }

        if (string.Equals(resolution, "5m", StringComparison.OrdinalIgnoreCase))
        {
            bucketSeconds = BucketWidths.FiveMinuteSeconds;
            return true;
        }

        bucketSeconds = 0;

        return false;
    }

    private static string ResolutionLabel(int bucketSeconds) => bucketSeconds switch
    {
        BucketWidths.RawSeconds => "raw",
        BucketWidths.MinuteSeconds => "1m",
        _ => "5m",
    };

    private static IResult Problem(string message) =>
        Results.BadRequest(new ErrorResponse(message));

    private static IResult Disabled() =>
        Results.Json(
            new ErrorResponse(
                "Lo storico e' disattivato su questo servizio (Observer:Storage:Enabled). " +
                "Gli endpoint /metrics/catalog e /metrics/latest continuano a funzionare."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
}

/// <summary>Il motivo per cui una richiesta non ha prodotto dati.</summary>
/// <param name="Message">Spiegazione leggibile.</param>
public sealed record ErrorResponse(string Message);

/// <summary>Una serie presente nello storico.</summary>
/// <param name="CollectorId">Chi la produce.</param>
/// <param name="MetricId">Quale metrica.</param>
/// <param name="Instance">Il core, il disco, l'interfaccia; null se la metrica e' unica.</param>
/// <param name="ValueKind">
/// Il ramo di <see cref="MetricValue"/> da cui proviene, con la stessa codifica numerica di
/// /metrics/latest.
/// </param>
public sealed record StoredSeriesResponse(string CollectorId, string MetricId, string? Instance, int ValueKind);

/// <summary>Un punto di storico.</summary>
/// <param name="Timestamp">Istante del campione, o inizio del bucket.</param>
/// <param name="Count">Quanti campioni grezzi ci sono dentro. Sul grezzo vale 1.</param>
/// <param name="Avg">Media dei campioni.</param>
/// <param name="Min">Valore minimo.</param>
/// <param name="Max">Valore massimo.</param>
/// <param name="Last">Ultimo valore in ordine di tempo.</param>
public sealed record HistoryPointResponse(
    DateTimeOffset Timestamp,
    int Count,
    double Avg,
    double Min,
    double Max,
    double Last);

/// <summary>La risposta di /metrics/history.</summary>
/// <param name="CollectorId">Chi produce la serie.</param>
/// <param name="MetricId">Quale metrica.</param>
/// <param name="Instance">L'istanza, oppure null.</param>
/// <param name="Resolution">La risoluzione effettivamente usata: "raw", "1m" o "5m".</param>
/// <param name="BucketSeconds">La stessa risoluzione in secondi.</param>
/// <param name="From">Inizio della finestra, incluso.</param>
/// <param name="To">Fine della finestra, esclusa.</param>
/// <param name="Truncated">True se il limite di punti ha tagliato la risposta.</param>
/// <param name="Points">I punti, in ordine di tempo crescente.</param>
public sealed record HistoryResponse(
    string CollectorId,
    string MetricId,
    string? Instance,
    string Resolution,
    int BucketSeconds,
    DateTimeOffset From,
    DateTimeOffset To,
    bool Truncated,
    IReadOnlyList<HistoryPointResponse> Points);

/// <summary>Le durate di conservazione configurate.</summary>
/// <param name="Raw">Per quanto si tiene il campionamento al secondo.</param>
/// <param name="Minute">Per quanto si tengono i bucket da un minuto.</param>
/// <param name="FiveMinute">Per quanto si tengono i bucket da cinque minuti.</param>
public sealed record RetentionResponse(TimeSpan Raw, TimeSpan Minute, TimeSpan FiveMinute);

/// <summary>La risposta di /metrics/storage.</summary>
/// <param name="Enabled">Se lo storico e' attivo.</param>
/// <param name="DatabasePath">Dove si trova il file.</param>
/// <param name="FileSizeBytes">Quanto occupa, WAL compreso.</param>
/// <param name="SeriesCount">Quante serie distinte.</param>
/// <param name="RawSamples">Campioni grezzi ancora presenti.</param>
/// <param name="MinuteBuckets">Bucket da un minuto presenti.</param>
/// <param name="FiveMinuteBuckets">Bucket da cinque minuti presenti.</param>
/// <param name="MinuteConsolidatedThrough">Fin dove il livello a un minuto ha aggregato.</param>
/// <param name="FiveMinuteConsolidatedThrough">Fin dove il livello a cinque minuti ha aggregato.</param>
/// <param name="DroppedSnapshots">
/// Quanti campionamenti sono stati scartati perche' il disco non stava al passo. Diverso da
/// zero significa che lo storico ha buchi.
/// </param>
/// <param name="Retention">Le durate configurate.</param>
public sealed record StorageResponse(
    bool Enabled,
    string DatabasePath,
    long FileSizeBytes,
    long SeriesCount,
    long RawSamples,
    long MinuteBuckets,
    long FiveMinuteBuckets,
    DateTimeOffset? MinuteConsolidatedThrough,
    DateTimeOffset? FiveMinuteConsolidatedThrough,
    long DroppedSnapshots,
    RetentionResponse Retention);
