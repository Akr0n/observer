using Observer.Core.Metrics;

namespace Observer.App.Services;

/// <summary>
/// How a call to the service went. Every branch corresponds to a DIFFERENT sentence to show on
/// screen: whoever is looking at the window does not read the logs, so "I cannot connect" and
/// "the token is wrong" must already be distinguishable at this level.
/// </summary>
public enum ServiceOutcome
{
    /// <summary>No outcome. It must never pass itself off as success.</summary>
    Unknown = 0,

    /// <summary>A valid response.</summary>
    Ok = 1,

    /// <summary>The service does not answer: stopped, wrong port, no network.</summary>
    Unreachable = 2,

    /// <summary>The service answers but rejects the token (401 or 403).</summary>
    TokenRejected = 3,

    /// <summary>The service has started but has not produced the first sample yet (503).</summary>
    NotReadyYet = 4,

    /// <summary>The response arrived but is not a readable sample.</summary>
    UnreadableResponse = 5,

    /// <summary>The service speaks a version of the format this client does not know.</summary>
    IncompatibleVersion = 6,

    /// <summary>Unexpected HTTP status code.</summary>
    UnexpectedResponse = 7,

    /// <summary>The certificate presented is not the expected one, or its fingerprint is missing.</summary>
    /// <remarks>
    /// Kept separate from <see cref="Unreachable"/> on purpose. On the wire it looks like the
    /// same fault - the connection is not established - but the two causes call for opposite
    /// moves: the first one you wait out, this one you do NOT. A fingerprint that changes means
    /// the service was reinstalled or there is a man in the middle, and neither case calls for
    /// a retry.
    /// </remarks>
    FingerprintMismatch = 8,

    /// <summary>The connection was REFUSED: the machine answers, the service does not.</summary>
    /// <remarks>
    /// Kept separate from <see cref="Unreachable"/> because it says far more: the packet
    /// reached the machine, which answered "there is nobody on that port". The fix is to start
    /// a service, not to open a port.
    /// </remarks>
    ConnectionRefused = 9,

    /// <summary>Nobody answered within the time limit.</summary>
    /// <remarks>
    /// The mirror image of <see cref="ConnectionRefused"/>: there a refusal comes back, here
    /// nothing comes back. A stopped service REFUSES, so silence does not speak of a stopped
    /// service: it speaks of a machine that is off, or of something dropping the packets
    /// without saying so. The fix is to open a port, not to start a service.
    /// </remarks>
    TimedOut = 10,
}

/// <summary>
/// Outcome of reading <c>/metrics/latest</c>.
/// </summary>
/// <param name="Outcome">How it went.</param>
/// <param name="Problem">Ready-made sentence for the screen, empty when the outcome is Ok.</param>
/// <param name="Snapshot">The sample, set only when the outcome is Ok.</param>
public sealed record SnapshotFetch(ServiceOutcome Outcome, string Problem, MachineSnapshot? Snapshot)
{
    /// <summary>True when there really is a sample to show.</summary>
    public bool IsOk => Outcome == ServiceOutcome.Ok && Snapshot is not null;
}

/// <summary>
/// Outcome of reading <c>/metrics/catalog</c>.
/// </summary>
/// <param name="Outcome">How it went.</param>
/// <param name="Problem">Ready-made sentence for the screen, empty when the outcome is Ok.</param>
/// <param name="Catalog">The catalog, set only when the outcome is Ok.</param>
public sealed record CatalogFetch(ServiceOutcome Outcome, string Problem, MetricCatalog? Catalog)
{
    /// <summary>True when there really is a usable catalog.</summary>
    public bool IsOk => Outcome == ServiceOutcome.Ok && Catalog is not null;
}
