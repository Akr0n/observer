namespace Observer.Service.Persistence;

/// <summary>
/// Chooses the resolution to answer a history query with.
/// </summary>
/// <remarks>
/// It is needed because the client cannot know it: asking for a month at one-second
/// resolution is not a user error, it is simply a question to be answered with the right
/// aggregate instead of with two and a half million points.
/// </remarks>
public static class HistoryResolution
{
    /// <summary>Chooses the finest resolution that fits within the point limit.</summary>
    /// <param name="from">Start of the requested window.</param>
    /// <param name="toExclusive">End of the requested window.</param>
    /// <param name="maxPoints">How many points at most are wanted in the answer.</param>
    /// <param name="rawAvailableFrom">
    /// From when on the raw data still exists: anything earlier has already been deleted.
    /// </param>
    /// <returns>The width in seconds: 1 for the raw data, 60 or 300 for the aggregates.</returns>
    /// <exception cref="ArgumentException">If the window is empty or reversed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">If the point limit is not positive.</exception>
    public static int Choose(
        DateTimeOffset from,
        DateTimeOffset toExclusive,
        int maxPoints,
        DateTimeOffset rawAvailableFrom)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoints, 1);

        if (toExclusive <= from)
        {
            throw new ArgumentException(
                "The end of the window must come after its start.",
                nameof(toExclusive));
        }

        double windowSeconds = (toExclusive - from).TotalSeconds;

        foreach (int width in Candidates)
        {
            // The raw data can only be asked for where it still exists. Returning it for a
            // window already deleted would give an empty chart instead of an aggregated one,
            // and "empty" reads as "the machine was not being monitored".
            if (width == BucketWidths.RawSeconds && from < rawAvailableFrom)
            {
                continue;
            }

            if (windowSeconds / width <= maxPoints)
            {
                return width;
            }
        }

        // Not even the coarsest level fits. There is no coarser one, so it answers with that
        // one and the row limit truncates: an incomplete chart is more useful than an
        // error.
        return BucketWidths.FiveMinuteSeconds;
    }

    /// <summary>From the finest to the coarsest: the first one that fits is chosen.</summary>
    private static readonly int[] Candidates =
    [
        BucketWidths.RawSeconds,
        BucketWidths.MinuteSeconds,
        BucketWidths.FiveMinuteSeconds,
    ];
}
