namespace Observer.App.Services;

/// <summary>
/// How serious the status bar's message is. It governs the colour only.
/// </summary>
/// <remarks>
/// Its own enum instead of FluentAvalonia's, for the same reason as
/// <see cref="MetricSeverity"/>: the decision is pure presentation logic and must be testable
/// without dragging in a control library. The view model does the translation into a colour.
/// </remarks>
public enum StatusTone
{
    /// <summary>Something normal is happening. Neutral.</summary>
    Informational = 0,

    /// <summary>Something is off, but the service is still answering.</summary>
    Warning = 1,

    /// <summary>A real error: the one that deserves red.</summary>
    Error = 2,
}

/// <summary>
/// What to show when a reading did not succeed.
/// </summary>
/// <param name="Tone">Severity, that is, the colour of the bar.</param>
/// <param name="Title">The bar's title.</param>
/// <param name="Text">The bar's text.</param>
/// <param name="Subheading">The line below the window title.</param>
public sealed record StatusMessage(StatusTone Tone, string Title, string Text, string Subheading);

/// <summary>
/// Decides whether a fault is still normal or has become an error.
/// </summary>
/// <remarks>
/// The rule: <b>severity depends on how LONG the fault lasts, not on the single attempt that
/// went wrong.</b> Without it, the window opened red on every freshly installed machine,
/// because the first attempt landed while the service was still starting — and the error
/// cleared itself a moment later. An alarm that turns itself off teaches you to ignore the
/// real ones too.
/// <para>
/// The wait applies only where waiting can change the outcome: a service that is not
/// answering yet, a service that has not sampled yet. A wrong token or an incompatible
/// version will be identical in a minute, so they are reported immediately.
/// </para>
/// </remarks>
public static class StatusEscalation
{
    /// <summary>
    /// How long to wait before treating a service that is not answering as faulted.
    /// </summary>
    /// <remarks>
    /// Measured on this machine, with the service started by hand and already warm: from
    /// process start to the first 200 on <c>/metrics/latest</c> it takes 0.9-1.4 seconds over
    /// three runs. On a freshly installed machine the cost is higher — cold file cache,
    /// antivirus scanning the just-written binaries, start-up mediated by the service manager
    /// — and ten seconds leave a wide margin without making the window look stuck to whoever
    /// opens the dashboard on a machine where the service is not there.
    /// </remarks>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Translates an outcome into what has to be written on screen.
    /// </summary>
    /// <param name="outcome">How the last reading went.</param>
    /// <param name="problem">The ready-made sentence produced by the client.</param>
    /// <param name="failingFor">How long the readings have been failing in a row.</param>
    /// <param name="endpoint">The service being queried.</param>
    /// <param name="hasValuesOnScreen">
    /// True if there are already values on screen, which stay there but are frozen.
    /// </param>
    /// <param name="hasMeasured">
    /// True if THIS MACHINE has ever answered with a reading. Not the same question as
    /// <paramref name="hasValuesOnScreen"/>, and conflating them put two different sentences
    /// about one machine on screen at the same moment: switching to a machine whose sampler had
    /// died cleared the panels, so the bar said it had never produced a reading while its own
    /// row in the sidebar - which had watched it produce them - said it had stopped. One is
    /// about the SCREEN, and decides whether to warn that the numbers are frozen; the other is
    /// about the MACHINE, and decides which of the two faults is being reported.
    /// </param>
    /// <returns>Title, text, severity and the line below the window title.</returns>
    public static StatusMessage MessageFor(
        ServiceOutcome outcome,
        string problem,
        TimeSpan failingFor,
        ObserverEndpoint endpoint,
        bool hasValuesOnScreen,
        bool hasMeasured = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        bool withinGrace = failingFor < GracePeriod;

        return outcome switch
        {
            // The three ways of getting no answer deserve the same wait: right after boot, a
            // machine REFUSES the connection because the port is not open yet, and accepts it
            // shortly after. Telling them apart matters when the fault lasts, not during start-up.
            ServiceOutcome.Unreachable or ServiceOutcome.ConnectionRefused
                or ServiceOutcome.TimedOut when withinGrace => new StatusMessage(
                StatusTone.Informational,
                "Connecting",
                // For a REMOTE machine there is no way to know whether it is starting up: that
                // is a claim you cannot make from here. Say what is being done, and no more.
                endpoint.Kind == EndpointKind.Local
                    ? "Waiting for the Observer service on this machine to answer. It may still be starting up."
                    : $"Contacting {endpoint.Description}…",
                WaitingSubheading(hasValuesOnScreen)),

            // A machine with readings already drawn is a machine that WAS measuring, and the
            // client's sentence for this outcome promises the opposite twice over: that the
            // service has not produced its FIRST reading, and that it usually sorts itself out
            // in a second or two. Both are about a service that is starting. This one is not.
            ServiceOutcome.NotReadyYet when withinGrace && hasMeasured => new StatusMessage(
                StatusTone.Informational,
                "Readings paused",
                $"The service on {endpoint.Description} is answering but has not produced a new " +
                "reading in the last few seconds.",
                MeasuringStoppedSubheading(hasValuesOnScreen)),

            ServiceOutcome.NotReadyYet when withinGrace => new StatusMessage(
                StatusTone.Informational,
                "Service is starting",
                problem,
                WaitingSubheading(hasValuesOnScreen)),

            // The same outcome, past the grace, and still two different things. With values on
            // screen the machine measured until a moment ago and has stopped: saying it never
            // produced a reading would be contradicted by every number below the message, and
            // "Not connected" would be contradicted by the fact that it is ANSWERING - which is
            // the whole peculiarity of this fault and the reason it went unnoticed for so long.
            ServiceOutcome.NotReadyYet when hasMeasured => new StatusMessage(
                StatusTone.Warning,
                "Not measuring",
                $"The service on {endpoint.Description} is answering, but it has stopped producing " +
                "readings. " +
                (hasValuesOnScreen
                    ? "The values below are the last ones it measured, and they are not moving. "
                    : string.Empty) +
                "Run \"observer doctor\" on that machine to see what it reports.",
                MeasuringStoppedSubheading(hasValuesOnScreen)),

            // The service answers: it is not unreachable, but it is not sampling either.
            // Staying on "Service is starting" for ever, with text promising it will sort
            // itself out, would be a lie nobody ever corrects.
            ServiceOutcome.NotReadyYet => new StatusMessage(
                StatusTone.Warning,
                "No readings yet",
                $"The service on {endpoint.Description} is answering, but it still hasn't produced a " +
                "reading. Sampling is not working there: run \"observer doctor\" on that machine to " +
                "see what it reports.",
                DisconnectedSubheading(hasValuesOnScreen)),

            // Refusal and silence are not synonyms for "unreachable", and that is the whole
            // point: the first is answered by starting a service, the second by opening a port.
            // One title for both forced whoever was looking to guess which of the two it was.
            ServiceOutcome.ConnectionRefused => ErrorMessage("Service not running", problem, hasValuesOnScreen),
            ServiceOutcome.TimedOut => ErrorMessage("No answer", problem, hasValuesOnScreen),

            ServiceOutcome.Unreachable => ErrorMessage("Service unreachable", problem, hasValuesOnScreen),
            ServiceOutcome.TokenRejected => ErrorMessage("Token rejected", problem, hasValuesOnScreen),
            ServiceOutcome.IncompatibleVersion => ErrorMessage("Version mismatch", problem, hasValuesOnScreen),
            ServiceOutcome.UnreadableResponse => ErrorMessage("Unrecognized response", problem, hasValuesOnScreen),

            // These two used to end up under the generic title, and not by decision: they had
            // simply slipped into the fallback arm. A changed certificate in particular
            // deserves to be named, because it is the only fault in here that should NOT be
            // answered by retrying.
            ServiceOutcome.UnexpectedResponse => ErrorMessage("Unexpected reply", problem, hasValuesOnScreen),
            ServiceOutcome.FingerprintMismatch => ErrorMessage("Certificate changed", problem, hasValuesOnScreen),

            _ => ErrorMessage("Reading failed", problem, hasValuesOnScreen),
        };
    }

    private static StatusMessage ErrorMessage(string title, string problem, bool hasValuesOnScreen) =>
        new(StatusTone.Error, title, problem, DisconnectedSubheading(hasValuesOnScreen));

    // Its own line, and neither of the two below would do. "Reconnecting" and "Not connected"
    // both describe a machine that is not answering, and this one is: it answers, promptly, and
    // hands back the same reading every time. That is what has to be said, because it is the
    // only fault in here where everything on screen looks right.
    private static string MeasuringStoppedSubheading(bool hasValuesOnScreen) =>
        hasValuesOnScreen
            ? "Not measuring: the values shown are the last reading that machine produced."
            : "Not measuring.";

    private static string WaitingSubheading(bool hasValuesOnScreen) =>
        hasValuesOnScreen
            ? "Reconnecting: the values shown are the last successful reading."
            : "Connecting…";

    // The values stay on screen on purpose: clearing them would suggest the machine has stopped
    // having a CPU. This line is what stops them from being read as current.
    private static string DisconnectedSubheading(bool hasValuesOnScreen) =>
        hasValuesOnScreen
            ? "Not connected: the values shown are the last successful reading."
            : "Not connected.";
}