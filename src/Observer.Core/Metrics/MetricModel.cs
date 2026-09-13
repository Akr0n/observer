using System.Text.Json.Serialization;

namespace Observer.Core.Metrics;

/// <summary>
/// Unit of measurement as an OPEN type, not as a closed enum. It is the choice that holds up the
/// "measure any parameter" requirement: the first sensor in rpm, in volts or in revolutions
/// must not force a change to Observer.Core.
/// </summary>
/// <param name="Symbol">Symbol of the unit, for example "%", "B", "rpm".</param>
public readonly record struct MetricUnit(string Symbol)
{
    /// <summary>Percentage points, from 0 to 100.</summary>
    public static MetricUnit Percent => new("%");

    /// <summary>Bytes.</summary>
    public static MetricUnit Bytes => new("B");

    /// <summary>Dimensionless quantity (counts, flags).</summary>
    public static MetricUnit None => new(string.Empty);
}

/// <summary>Which of <see cref="MetricValue"/>'s branches is set.</summary>
public enum MetricValueKind
{
    /// <summary>No branch. It is default(MetricValueKind) and must never stand for a real value.</summary>
    Unknown = 0,

    /// <summary>Numeric value.</summary>
    Number = 1,

    /// <summary>Text value (for example a disk's model).</summary>
    Text = 2,

    /// <summary>Boolean value (for example "SMART reported a fault").</summary>
    Flag = 3,
}

/// <summary>
/// The uniform payload that travels on the network. One single format for CPU, RAM, temperatures,
/// SMART and GPU: it is what allows adding a source without touching the transport
/// or the client.
/// </summary>
public readonly record struct MetricValue
{
    // [JsonConstructor] on a PRIVATE constructor: System.Text.Json honours it, and without
    // this the type serializes but does NOT deserialize. The properties are get-only,
    // so the deserializer would use the struct's implicit constructor and would
    // return default(MetricValue) — kind=Unknown, number=0 — without throwing anything:
    // the client would show a machine full of zeros marked "Ok" while curl on the same
    // endpoint returns the right numbers. The constructor stays private on purpose:
    // making it public would allow inconsistent states such as Kind=Number with Text set.
    [JsonConstructor]
    private MetricValue(MetricValueKind kind, double number, string? text, bool flag)
    {
        Kind = kind;
        Number = number;
        Text = text;
        Flag = flag;
    }

    /// <summary>The branch that is set.</summary>
    public MetricValueKind Kind { get; }

    /// <summary>Numeric value, meaningful only if <see cref="Kind"/> is Number.</summary>
    public double Number { get; }

    /// <summary>Text value, meaningful only if <see cref="Kind"/> is Text.</summary>
    public string? Text { get; }

    /// <summary>Boolean value, meaningful only if <see cref="Kind"/> is Flag.</summary>
    public bool Flag { get; }

    /// <summary>
    /// Builds a numeric value. Throws on non-finite values: a NaN accepted here
    /// does not lose one metric, it makes the serializer throw later and loses the ENTIRE
    /// HTTP response, all the other metrics included. Better a noisy error straight away,
    /// at the point where you can see which collector produced it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">If the value is NaN or infinite.</exception>
    public static MetricValue FromNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "A non-finite value can't be represented in JSON and would break the whole response.");
        }

        return new MetricValue(MetricValueKind.Number, value, null, false);
    }

    /// <summary>Builds a text value.</summary>
    public static MetricValue FromText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new MetricValue(MetricValueKind.Text, 0.0, value, false);
    }

    /// <summary>Builds a boolean value.</summary>
    public static MetricValue FromFlag(bool value) =>
        new(MetricValueKind.Flag, 0.0, null, value);
}

/// <summary>
/// A measured value, or the reason why that single value is missing.
/// </summary>
/// <remarks>
/// The diagnostics live HERE and not only on the snapshot, because otherwise a collector with
/// several instances could not express the normal case: three disks, one of them behind a USB
/// bridge that does not forward SMART commands. With a single status per collector only two
/// choices would be left, both wrong — declaring everything Ok and silently making the
/// problematic disk disappear, or declaring everything Unavailable and losing the healthy disks
/// too. It is built only from the factories: that way there is no "Ok" point without a value nor
/// a degraded point without an explanation.
/// </remarks>
public sealed record MetricPoint
{
    // As for MetricValue: private but annotated, otherwise the type serializes and does not
    // deserialize, and the client receives empty points with no error at all.
    [JsonConstructor]
    private MetricPoint(
        string metricId,
        string? instance,
        MetricValue? value,
        CollectorStatus status,
        string? message)
    {
        MetricId = metricId;
        Instance = instance;
        Value = value;
        Status = status;
        Message = message;
    }

    /// <summary>Identifier of the metric, for example "cpu.usage.total".</summary>
    public string MetricId { get; }

    /// <summary>
    /// Per-instance dimension: the core, the disk, the network interface. It is a string and
    /// not a hierarchy of types, and that is exactly what lets per-core, per-disk and
    /// per-process go through the same interface without changing it.
    /// Null when the metric is unique per machine.
    /// </summary>
    public string? Instance { get; }

    /// <summary>The value, or null when <see cref="Status"/> is not Ok.</summary>
    public MetricValue? Value { get; }

    /// <summary>Outcome of THIS instance's measurement, independent of the others.</summary>
    public CollectorStatus Status { get; }

    /// <summary>
    /// Readable explanation when the value is missing. It is what the dashboard shows in
    /// place of the number, instead of leaving a mute hole.
    /// </summary>
    public string? Message { get; }

    /// <summary>A value read correctly.</summary>
    public static MetricPoint Measured(string metricId, string? instance, MetricValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricId);

        return new MetricPoint(metricId, instance, value, CollectorStatus.Ok, message: null);
    }

    /// <summary>This instance is not measurable here, and the reason has to be shown.</summary>
    public static MetricPoint Unsupported(string metricId, string? instance, string reason) =>
        Missing(metricId, instance, CollectorStatus.Unsupported, reason);

    /// <summary>This instance exists but is not readable now, and the reason has to be shown.</summary>
    public static MetricPoint Unavailable(string metricId, string? instance, string reason) =>
        Missing(metricId, instance, CollectorStatus.Unavailable, reason);

    private static MetricPoint Missing(string metricId, string? instance, CollectorStatus status, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricId);

        // The reason is mandatory: it must not be possible to declare a missing value
        // without writing the sentence that will end up in the dashboard in its place.
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new MetricPoint(metricId, instance, value: null, status, reason);
    }
}

/// <summary>
/// Metadata of a metric. It travels in the catalog once only, not with every sample, and it is
/// what lets the client draw a metric it was not compiled against: it knows what unit it has,
/// what it is called and whether it is per instance.
/// </summary>
/// <param name="MetricId">Identifier, must match the one of the emitted points.</param>
/// <param name="DisplayName">Readable name to show in the dashboard.</param>
/// <param name="Unit">Unit of measurement.</param>
/// <param name="IsPerInstance">True if the metric produces one point per instance.</param>
public sealed record MetricDescriptor(
    string MetricId,
    string DisplayName,
    MetricUnit Unit,
    bool IsPerInstance);

/// <summary>
/// Outcome of a collection. It is the backbone of graceful degradation: telling these cases
/// apart is what lets the dashboard say why a datum is missing, instead of showing a mute hole
/// or, worse, an invented zero.
/// </summary>
public enum CollectorStatus
{
    /// <summary>No outcome. It must never pass itself off as success.</summary>
    Unknown = 0,

    /// <summary>Collection succeeded.</summary>
    Ok = 1,

    /// <summary>
    /// Starting up: the second sample needed to compute a difference is still missing.
    /// It is legitimate and temporary, and has to be told apart from a fault.
    /// </summary>
    Warmup = 2,

    /// <summary>The source exists on this platform but is not readable now.</summary>
    Unavailable = 3,

    /// <summary>
    /// The metric is not measurable on this platform. Different from "forgotten": it stays
    /// in the catalog with its explanation.
    /// </summary>
    Unsupported = 4,

    /// <summary>The collection threw an exception. It degrades this metric, not the service.</summary>
    Faulted = 5,
}

/// <summary>
/// The result of a collection, in one single shape for all collectors.
/// </summary>
/// <param name="CollectorId">Who produced the snapshot.</param>
/// <param name="Status">Outcome.</param>
/// <param name="Message">
/// Readable explanation when <paramref name="Status"/> is not Ok. It is what ends up in the
/// dashboard in place of the missing value.
/// </param>
/// <param name="Points">
/// The measured values. Empty when the outcome is not Ok. An ABSENT point means "not
/// applicable here": emitting a zero in its place would be misleading.
/// </param>
public sealed record MetricSnapshot(
    string CollectorId,
    CollectorStatus Status,
    string? Message,
    IReadOnlyList<MetricPoint> Points);

/// <summary>
/// What the service publishes on the network at every sampling: the outcome of all the
/// collectors plus the instant at which they were read.
/// </summary>
/// <param name="SchemaVersion">
/// Version of the format. It travels on the wire because a client and a service built from
/// different commits would otherwise diverge silently, with fields at zero instead of a
/// readable message.
/// </param>
/// <param name="CapturedAt">Instant of the sampling, in UTC.</param>
/// <param name="Collectors">Outcome of every collector, the degraded ones included.</param>
public sealed record MachineSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    IReadOnlyList<MetricSnapshot> Collectors)
{
    /// <summary>Current version of the published format.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// The ONLY extension point for adding a metric source. No type here names a specific metric,
/// so this file must not change when a new one is added: it is the property that holds up the
/// "any parameter" requirement.
/// </summary>
public interface IMetricCollector
{
    /// <summary>Unique identifier of the collector, for example "cpu".</summary>
    string Id { get; }

    /// <summary>
    /// The metrics this collector can emit. What is not measurable today has to be declared
    /// too: it is the difference between "it can't be done here" and "I forgot it".
    /// </summary>
    IReadOnlyList<MetricDescriptor> Descriptors { get; }

    /// <summary>
    /// Runs a collection. It must not throw for an absent or unreadable source:
    /// a degraded snapshot with the reason has to be returned instead.
    /// </summary>
    ValueTask<MetricSnapshot> CollectAsync(CancellationToken cancellationToken);
}