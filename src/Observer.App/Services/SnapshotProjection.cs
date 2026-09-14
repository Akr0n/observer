using Observer.Core.Metrics;
using Observer.Core.Metrics.Memory;

namespace Observer.App.Services;

/// <summary>
/// How serious a row's or a group's message is. It governs the colour only.
/// </summary>
public enum MetricSeverity
{
    /// <summary>Valid value.</summary>
    Ok = 0,

    /// <summary>Starting up: the second sample is missing. Normal, not a fault.</summary>
    Warmup = 1,

    /// <summary>Not measurable on this platform. That is information, not an error.</summary>
    Unsupported = 2,

    /// <summary>There should have been a value and there isn't.</summary>
    Problem = 3,
}

/// <summary>
/// A row on the screen.
/// </summary>
/// <param name="Key">Stable identity of the row, so it can be updated without recreating it.</param>
/// <param name="Label">Readable name, with the instance in parentheses when there is one.</param>
/// <param name="Display">The formatted value, or the reason it is missing.</param>
/// <param name="Fraction">Fraction 0..1 for the bar, null when it is not a percentage.</param>
/// <param name="Severity">Severity of what the row is saying.</param>
public sealed record MetricRowState(
    string Key,
    string Label,
    string Display,
    double? Fraction,
    MetricSeverity Severity);

/// <summary>
/// A panel on the screen: a collector with its rows.
/// </summary>
/// <param name="CollectorId">Identifier of the collector.</param>
/// <param name="Title">Readable title of the panel.</param>
/// <param name="Note">
/// Reason the collector is degraded, or null. It is what fills the panel when
/// <paramref name="Rows"/> is empty, because an empty panel cannot be diagnosed.
/// </param>
/// <param name="Severity">Severity of the collector's status.</param>
/// <param name="Rows">The measured rows.</param>
public sealed record MetricGroupState(
    string CollectorId,
    string Title,
    string? Note,
    MetricSeverity Severity,
    IReadOnlyList<MetricRowState> Rows);

/// <summary>
/// Translates a snapshot into the rows to draw.
/// </summary>
/// <remarks>
/// It is a pure function: a snapshot and a catalog go in, rows come out. It is the part of
/// the application that can be checked with tests instead of by eye, and it is also the one
/// where defects are silent — a degraded status turned into a zero looks too much like a
/// real measurement.
/// </remarks>
public static class SnapshotProjection
{
    // The service declares no readable name for the COLLECTOR, only for the metrics. This
    // little table is here so a panel is not titled "memory": someone who does not program
    // reads "Memory". An unknown collector keeps its own identifier, so adding a new one to
    // the service does not require touching this file.
    private static readonly Dictionary<string, string> KnownTitles = new(StringComparer.Ordinal)
    {
        ["cpu"] = "CPU",
        ["memory"] = "Memory",
        ["disk"] = "Disks",
    };

    /// <summary>Builds the panels to show.</summary>
    /// <param name="snapshot">The last snapshot received.</param>
    /// <param name="catalog">The catalog, or <see cref="MetricCatalog.Empty"/>.</param>
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
    /// Removes the "Available memory is an estimate" row and, when the answer is yes, attaches
    /// it to the number it qualifies.
    /// </summary>
    /// <remarks>
    /// That row answered a question nobody had asked, and on Windows it always answered "No":
    /// available memory there is exposed by the system, so the flag is hard-wired to false and
    /// that row would never have said anything else. A row that repeats the same answer for
    /// ever teaches you to skip it, and you would skip it on the day it did say something too.
    /// <para>
    /// The intention was right and it stands: an available memory that is RECONSTRUCTED - on
    /// Linux, when the kernel does not expose MemAvailable and it is summed from free memory,
    /// buffers, cache and reclaimable memory - is not a measurement, and passing it off as one
    /// would be a silent lie. But it is declared where that matters: attached to the value, and
    /// only when there is something to declare.
    /// </para>
    /// <para>
    /// If the point is NEITHER yes nor no, the row stays where it is: it means that reading
    /// failed, and a fault that disappears from the screen is worse than one row too many.
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
    /// Adds the unit in parentheses to the rows that, inside the same panel, would end up with
    /// the same name.
    /// </summary>
    /// <remarks>
    /// It is genuinely needed: the memory collector declares "Used memory" both for the bytes
    /// and for the percentage, and two rows with the same name and different numbers look like
    /// a contradiction. The rule is generic, so it also covers a future collector that gives
    /// two things the same name.
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

            // The key holds collectorId|metricId|instance: the middle piece is what is needed
            // to find the descriptor again, and so the unit.
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
            // An Ok collector that produced nothing is not a normal case: without this row the
            // panel would stay empty and silent.
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

        if (point.Value is not MetricValue value)
        {
            // Ok with no value is exactly the case the comment in MetricPoint fears: showing
            // zero here would give a machine full of zeros marked "Ok".
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
            MetricFormatting.Describe(value, unit),
            MetricFormatting.Fraction(value, unit),
            value.Kind == MetricValueKind.Unknown ? MetricSeverity.Problem : MetricSeverity.Ok);
    }

    private static MetricSeverity SeverityFor(CollectorStatus status) => status switch
    {
        CollectorStatus.Ok => MetricSeverity.Ok,
        CollectorStatus.Warmup => MetricSeverity.Warmup,
        CollectorStatus.Unsupported => MetricSeverity.Unsupported,
        _ => MetricSeverity.Problem,
    };
}