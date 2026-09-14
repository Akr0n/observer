using System.Globalization;
using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Memory;

namespace Observer.App.Services;

/// <summary>
/// How hard a machine is working, in two numbers and nothing else.
/// </summary>
/// <param name="Cpu">CPU usage as a percentage, null when it is not known.</param>
/// <param name="Memory">Memory used as a percentage, null when it is not known.</param>
/// <remarks>
/// <para>
/// Pure and needing no window, like <see cref="Downtime"/> and <see cref="StatusEscalation"/>:
/// it is a reading rule, and reading rules are tested without drawing anything.
/// </para>
/// <para>
/// Two fixed metrics and not the catalog, and that is a choice, not an oversight. The gauges
/// take names and units from <c>/metrics/catalog</c> precisely so that they carry no compiled-in
/// constants, and that rule still holds for them: a gauge must be able to show a metric that did
/// not exist when the client was compiled. Here the question is a different one. It is not
/// "what does that machine measure" but "which machine is under strain", and two numbers, always
/// the same two, answer it. The identifiers come from
/// <see cref="CpuCollector.TotalUsageMetricId"/> and
/// <see cref="MemoryCollector.UsedPercentMetricId"/>, which live in <c>Observer.Core</c>: they
/// are the contract the two sides already share, not a string copied out by hand.
/// </para>
/// </remarks>
public sealed record MachineLoad(double? Cpu, double? Memory)
{
    /// <summary>Nothing is known: machine down, or the snapshot never arrived.</summary>
    public static readonly MachineLoad None = new(null, null);

    /// <summary>Reads the two numbers out of a full snapshot.</summary>
    /// <param name="snapshot">What <c>/metrics/latest</c> answered, or null.</param>
    /// <returns>The two values, each null if that point is missing or is not measured.</returns>
    /// <remarks>
    /// The two numbers are read one at a time, and one can be missing while the other is there:
    /// on a platform where the CPU cannot be read, memory still can be, and showing what is
    /// known is better than showing nothing. A point whose <c>Status</c> is not Ok has a null
    /// <c>Value</c> by construction, but the <c>Kind</c> is checked all the same: a text value
    /// read as a number would give zero, and an invented zero next to a machine's name reads as
    /// "idle", which is the opposite of "not known".
    /// </remarks>
    public static MachineLoad From(MachineSnapshot? snapshot) =>
        snapshot is null
            ? None
            : new MachineLoad(
                NumberFor(snapshot, CpuCollector.TotalUsageMetricId),
                NumberFor(snapshot, MemoryCollector.UsedPercentMetricId));

    /// <summary>The line to put under the name, empty when nothing is known.</summary>
    /// <remarks>
    /// The labels are written here and not read from the catalog, deliberately: there are two of
    /// them, they do not change, and they have to fit a column a little over a hundred pixels
    /// wide. "CPU usage" and "Memory usage", which are the real names under the gauges, would
    /// not fit - and next to a percentage they add nothing.
    /// </remarks>
    public string Caption => (Cpu, Memory) switch
    {
        (null, null) => string.Empty,
        (not null, null) => "CPU " + FormatPercent(Cpu.Value),
        (null, not null) => "RAM " + FormatPercent(Memory.Value),
        _ => "CPU " + FormatPercent(Cpu.Value) + " · RAM " + FormatPercent(Memory.Value),
    };

    /// <summary>
    /// Whole numbers and not decimals: the sidebar answers "which machine is under strain", and
    /// a tenth of a point adds nothing to that question. It takes something away: a digit that
    /// changes at every reading keeps pulling the eye back to a column you look at precisely so
    /// that you do not have to keep looking at it.
    /// </summary>
    private static string FormatPercent(double value) =>
        Math.Round(Math.Clamp(value, 0d, 100d)).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <remarks>
    /// The search is by metric identifier over ALL the collectors, without filtering by collector
    /// first: the identifier is unique by construction - the <c>MetricDescriptor</c> contract
    /// says so, "must match the one of the emitted points" - so the collector name would be a
    /// second constant to keep aligned in exchange for nothing.
    /// </remarks>
    private static double? NumberFor(MachineSnapshot snapshot, string metricId)
    {
        foreach (MetricSnapshot collector in snapshot.Collectors)
        {
            foreach (MetricPoint point in collector.Points)
            {
                // Instance null: the MACHINE-wide one. The per-core and per-disk points come
                // through here with the same identifier and an instance set, and taking the first
                // one that turns up would pass the load of ONE core off as the machine's.
                if (string.Equals(point.MetricId, metricId, StringComparison.Ordinal)
                    && point.Instance is null
                    && point.Status == CollectorStatus.Ok
                    && point.Value is { Kind: MetricValueKind.Number } value)
                {
                    return value.Number;
                }
            }
        }

        return null;
    }
}