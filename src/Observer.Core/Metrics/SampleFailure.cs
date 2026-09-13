namespace Observer.Core.Metrics;

/// <summary>
/// Why a sample did not produce a value. It exists so that the REASON is not lost:
/// simply returning "no value" would force whoever is looking at the dashboard to
/// guess whether the datum is missing, broken or not ready yet.
/// </summary>
public enum SampleFailure
{
    /// <summary>
    /// No diagnosis. It is the value of default(SampleFailure) and must never pass itself
    /// off as a real cause: a zero that meant "counters went backwards" would make a
    /// diagnosis that was never performed appear on the dashboard.
    /// </summary>
    Unknown = 0,

    /// <summary>First reading: a second sample is needed to compute a difference.</summary>
    FirstSample = 1,

    /// <summary>The counters decreased (suspend, resume, VM migration).</summary>
    CounterWentBackwards = 2,

    /// <summary>No measurable time passed between the two samples.</summary>
    NoElapsedTime = 3,

    /// <summary>The computation produced a non-finite value (NaN or infinity).</summary>
    NotFinite = 4,
}

/// <summary>
/// Turns a <see cref="SampleFailure"/> into a readable sentence to show in place of the
/// missing value.
/// </summary>
public static class SampleFailureText
{
    /// <summary>Explanation of why the sample has no value.</summary>
    public static string Describe(SampleFailure failure) => failure switch
    {
        SampleFailure.FirstSample =>
            "first reading: waiting for a second sample to measure the change",
        SampleFailure.CounterWentBackwards =>
            "counters went backwards (machine suspended, resumed or migrated)",
        SampleFailure.NoElapsedTime =>
            "no measurable time passed between the two samples",
        SampleFailure.NotFinite =>
            "the computed value wasn't finite and was discarded",
        _ => "cause unknown",
    };
}