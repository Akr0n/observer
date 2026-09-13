using Observer.Core.Metrics;
using Observer.Core.Metrics.Memory;

namespace Observer.App.Services;

/// <summary>
/// Quanto e' grave cio' che una row o un gruppo sta dicendo. Governa solo il colore.
/// </summary>
public enum MetricSeverity
{
    /// <summary>Valore valido.</summary>
    Ok = 0,

    /// <summary>In avvio: manca il secondo campione. Normale, non un guasto.</summary>
    Warmup = 1,

    /// <summary>Non misurabile su questa piattaforma. E' un'informazione, non un errore.</summary>
    Unsupported = 2,

    /// <summary>Doveva esserci un valueIndex e non c'e'.</summary>
    Problem = 3,
}

/// <summary>
/// Una row della schermata.
/// </summary>
/// <param name="Key">Identita' stabile della row, per aggiornarla senza ricrearla.</param>
/// <param name="Label">Nome leggibile, con l'istanza fra parentesi quando c'e'.</param>
/// <param name="Display">Il valueIndex formattato, oppure il motivo per cui manca.</param>
/// <param name="Fraction">Frazione 0..1 per la barra, null quando non e' una percentuale.</param>
/// <param name="Severity">SeverityFor' di cio' che la row sta dicendo.</param>
public sealed record MetricRowState(
    string Key,
    string Label,
    string Display,
    double? Fraction,
    MetricSeverity Severity);

/// <summary>
/// Un riquadro della schermata: un collector con le sue rows.
/// </summary>
/// <param name="CollectorId">Identificatore del collector.</param>
/// <param name="Title">TitleFor leggibile del riquadro.</param>
/// <param name="Note">
/// Motivo per cui il collector e' degradato, oppure null. E' cio' che riempie il riquadro
/// quando <paramref name="Rows"/> e' vuoto, perche' un riquadro vuoto non si diagnostica.
/// </param>
/// <param name="Severity">SeverityFor' dello status del collector.</param>
/// <param name="Rows">Le rows misurate.</param>
public sealed record MetricGroupState(
    string CollectorId,
    string Title,
    string? Note,
    MetricSeverity Severity,
    IReadOnlyList<MetricRowState> Rows);

/// <summary>
/// Traduce un campionamento nelle rows da disegnare.
/// </summary>
/// <remarks>
/// E' una funzione pura: campionamento e catalogo entrano, rows escono. E' il pezzo
/// dell'applicazione che si puo' verificare con dei test invece che a occhio, ed e' anche
/// quello dove i difetti sono silenziosi — uno status degradato tradotto in uno zero
/// somiglia troppo a una misura vera.
/// </remarks>
public static class SnapshotProjection
{
    // Il servizio non dichiara un nome leggibile per il COLLECTOR, solo per le metriche.
    // Questa tabellina serve a non intitolare un riquadro "memory": chi non programma legge
    // "Memory". Un collector sconosciuto tiene il proprio identificatore, quindi
    // aggiungerne uno nuovo al servizio non richiede di toccare questo file.
    private static readonly Dictionary<string, string> KnownTitles = new(StringComparer.Ordinal)
    {
        ["cpu"] = "CPU",
        ["memory"] = "Memory",
        ["disk"] = "Disks",
    };

    /// <summary>Costruisce i riquadri da mostrare.</summary>
    /// <param name="snapshot">L'ultimo campionamento ricevuto.</param>
    /// <param name="catalog">Il catalogo, oppure <see cref="MetricCatalog.Empty"/>.</param>
    public static IReadOnlyList<MetricGroupState> Project(MachineSnapshot snapshot, MetricCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalog);

        List<MetricGroupState> groups = new(snapshot.Collectors?.Count ?? 0);

        foreach (MetricSnapshot collector in snapshot.Collectors ?? [])
        {
            if (collector is null)
            {
                continue;
            }

            List<MetricRowState> rows = [];

            foreach (MetricPoint point in collector.Points ?? [])
            {
                if (point is not null)
                {
                    rows.Add(RowFor(collector.CollectorId, point, catalog));
                }
            }

            Disambiguate(rows, catalog);
            FoldEstimateIntoValue(rows);

            groups.Add(new MetricGroupState(
                collector.CollectorId,
                TitleFor(collector.CollectorId),
                NoteFor(collector),
                SeverityFor(collector.Status),
                rows));
        }

        return groups;
    }

    /// <summary>
    /// Toglie la row "Available memory is an estimate" e, quando la answer e' si', la
    /// attacca al numero che qualifica.
    /// </summary>
    /// <remarks>
    /// Quella row rispondeva a una domanda che nessuno aveva fatto, e su Windows rispondeva
    /// sempre "No": la memoria disponibile la' e' esposta dal sistema, quindi il flag e'
    /// cablato a falso e quella row non avrebbe mai detto altro. Una row che ripete
    /// all'infinito la stessa answer insegna a saltarla, e la salterebbe anche il giorno in
    /// cui dicesse qualcosa.
    /// <para>
    /// L'intenzione era giusta e resta: una memoria disponibile RICOSTRUITA - su Linux, quando
    /// il kernel non espone MemAvailable e la si somma da memoria libera, buffer, cache e
    /// memoria recuperabile - non e' una misura, e spacciarla per tale sarebbe una bugia
    /// silenziosa. Ma si dichiara dove serve: attaccata al valueIndex, e solo quando c'e'
    /// qualcosa da dichiarare.
    /// </para>
    /// <para>
    /// Se il point NON e' ne' si' ne' no, la row resta dov'e': vuol dire che quella lettura
    /// e' fallita, e un guasto che sparisce dallo schermo e' peggio di una row di troppo.
    /// </para>
    /// </remarks>
    private static void FoldEstimateIntoValue(List<MetricRowState> rows)
    {
        int flagIndex = rows.FindIndex(row => MetricIdOf(row) == MemoryCollector.AvailableEstimatedMetricId);

        if (flagIndex < 0)
        {
            return;
        }

        string answer = rows[flagIndex].Display;

        if (!string.Equals(answer, MetricFormatting.Yes, StringComparison.Ordinal)
            && !string.Equals(answer, MetricFormatting.No, StringComparison.Ordinal))
        {
            return;
        }

        rows.RemoveAt(flagIndex);

        if (!string.Equals(answer, MetricFormatting.Yes, StringComparison.Ordinal))
        {
            return;
        }

        int valueIndex = rows.FindIndex(row => MetricIdOf(row) == MemoryCollector.AvailableBytesMetricId);

        if (valueIndex >= 0)
        {
            rows[valueIndex] = rows[valueIndex] with { Display = rows[valueIndex].Display + " (estimated)" };
        }
    }

    private static string MetricIdOf(MetricRowState row) =>
        row.Key.Split('|').ElementAtOrDefault(1) ?? string.Empty;

    /// <summary>
    /// Aggiunge l'unit' fra parentesi alle rows che, dentro lo stesso riquadro, finirebbero
    /// con lo stesso nome.
    /// </summary>
    /// <remarks>
    /// Serve davvero: il collector della memoria dichiara "Used memory" sia per i byte sia
    /// per la percentuale, e due rows con lo stesso nome e numeri diversi sembrano una
    /// contraddizione. La regola e' generica, quindi vale anche per un collector futuro che
    /// commetta lo stesso battesimo doppio.
    /// </remarks>
    private static void Disambiguate(List<MetricRowState> rows, MetricCatalog catalog)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);

        foreach (MetricRowState row in rows)
        {
            counts[row.Label] = counts.TryGetValue(row.Label, out int n) ? n + 1 : 1;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            if (counts[rows[i].Label] < 2)
            {
                continue;
            }

            // La key contiene collectorId|metricId|istanza: il pezzo centrale e' cio' che
            // serve per ritrovare il descriptor e quindi l'unit'.
            string[] parts = rows[i].Key.Split('|');
            string symbol = parts.Length > 1 ? catalog.Find(parts[1])?.Unit.Symbol ?? string.Empty : string.Empty;
            string qualifier = string.IsNullOrEmpty(symbol) ? parts.ElementAtOrDefault(1) ?? "?" : symbol;

            rows[i] = rows[i] with { Label = rows[i].Label + " (" + qualifier + ")" };
        }
    }

    private static string TitleFor(string collectorId) =>
        collectorId is not null && KnownTitles.TryGetValue(collectorId, out string? title)
            ? title
            : collectorId ?? "unnamed source";

    private static string? NoteFor(MetricSnapshot collector)
    {
        if (collector.Status == CollectorStatus.Ok)
        {
            // Un collector Ok che non ha prodotto nulla non e' un caso normale: senza questa
            // row il riquadro resterebbe vuoto e muto.
            return collector.Points is null || collector.Points.Count == 0
                ? "The service reports this source as working but sent no values."
                : null;
        }

        return collector.Message ?? "The service didn't say why this source produced no values.";
    }

    private static MetricRowState RowFor(string collectorId, MetricPoint point, MetricCatalog catalog)
    {
        MetricDescriptor? descriptor = catalog.Find(point.MetricId);
        MetricUnit? unit = descriptor?.Unit;

        string label = descriptor?.DisplayName ?? point.MetricId;

        if (!string.IsNullOrWhiteSpace(point.Instance))
        {
            label = label + " (" + point.Instance + ")";
        }

        string key = collectorId + "|" + point.MetricId + "|" + (point.Instance ?? string.Empty);

        if (point.Status != CollectorStatus.Ok)
        {
            return new MetricRowState(
                key,
                label,
                point.Message ?? "no value available, no reason given",
                null,
                SeverityFor(point.Status));
        }

        if (point.Value is not MetricValue valueIndex)
        {
            // Ok senza valueIndex e' esattamente il caso che il commento in MetricPoint teme:
            // mostrare zero qui darebbe una macchina piena di zeri marcati "Ok".
            return new MetricRowState(
                key,
                label,
                "the service reported the reading succeeded but sent no value",
                null,
                MetricSeverity.Problem);
        }

        return new MetricRowState(
            key,
            label,
            MetricFormatting.Describe(valueIndex, unit),
            MetricFormatting.Fraction(valueIndex, unit),
            valueIndex.Kind == MetricValueKind.Unknown ? MetricSeverity.Problem : MetricSeverity.Ok);
    }

    private static MetricSeverity SeverityFor(CollectorStatus status) => status switch
    {
        CollectorStatus.Ok => MetricSeverity.Ok,
        CollectorStatus.Warmup => MetricSeverity.Warmup,
        CollectorStatus.Unsupported => MetricSeverity.Unsupported,
        _ => MetricSeverity.Problem,
    };
}