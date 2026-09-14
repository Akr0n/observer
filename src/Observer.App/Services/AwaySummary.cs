using System.Globalization;

namespace Observer.App.Services;

/// <summary>
/// The line that tells a person what they missed while they were not looking.
/// </summary>
/// <remarks>
/// Pure and needs no window, like <see cref="Downtime"/>: it is tested without drawing anything.
/// It is kept apart from <see cref="HistoryStrip.FindGaps"/> deliberately - that one produces the
/// facts, this one puts them into words.
/// </remarks>
public static class AwaySummary
{
    /// <summary>The line for one machine, empty when there is nothing to say.</summary>
    /// <param name="machineName">What the machine is called on screen.</param>
    /// <param name="gaps">The holes found in its history.</param>
    /// <param name="withDay">Whether the timestamps must carry the day of the week.</param>
    /// <returns>The sentence, or an empty string.</returns>
    /// <remarks>
    /// <para>
    /// Silence is a result, not a fault: no line means "I asked and there was nothing to say".
    /// It is the opposite of an alert that never shows up - there you cannot tell whether
    /// everything went well or the channel is broken - because being unable to ask is written by
    /// the caller as a different sentence, and that sentence does show up.
    /// </para>
    /// <para>
    /// A hole that touches the oldest edge does NOT count as an outage: retention deletes a
    /// prefix, and a missing prefix is indistinguishable from a machine switched on halfway
    /// through the window. Calling it a "three-hour outage" would be inventing something nobody
    /// saw, and a single invented sentence teaches you to distrust all the others.
    /// </para>
    /// </remarks>
    public static string LineFor(string machineName, IReadOnlyList<HistoryGap> gaps, bool withDay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);
        ArgumentNullException.ThrowIfNull(gaps);

        HistoryGap? edgeGap = gaps.FirstOrDefault(gap => gap.AtEdge);
        HistoryGap[] outages = [.. gaps.Where(gap => !gap.AtEdge)];

        string tail = edgeGap is null
            ? string.Empty
            : "nothing known before " + DescribeInstant(edgeGap.End, withDay);

        if (outages.Length == 0)
        {
            return tail.Length == 0 ? string.Empty : $"{machineName}: {tail}";
        }

        TimeSpan total = TimeSpan.Zero;
        HistoryGap longestGap = outages[0];

        foreach (HistoryGap gap in outages)
        {
            total += gap.Duration;

            if (gap.Duration > longestGap.Duration)
            {
                longestGap = gap;
            }
        }

        string longestRange = DescribeRange(longestGap, withDay);

        // With a single outage the total IS that outage: repeating "in 1 period" would be noise.
        // With more than one the total alone would lie by omission - three hours in one go and
        // three hours in ten hiccups are two different machines - so the count is stated and the
        // longest one is shown, because that is the one that decides whether you leave your chair.
        string body = outages.Length == 1
            ? $"not measured for {Downtime.Describe(total)} ({longestRange})"
            : $"not measured for {Downtime.Describe(total)} in {outages.Length.ToString(CultureInfo.InvariantCulture)} periods (longest {longestRange})";

        return tail.Length == 0 ? $"{machineName}: {body}" : $"{machineName}: {body}; {tail}";
    }

    /// <summary>The two ends of an outage, with the day when it is really needed.</summary>
    /// <remarks>
    /// The day is added even when <paramref name="withDay"/> is false but the two ends fall on
    /// two different DAYS, and this is not pedantry: what is printed here is the span of a whole
    /// outage, not the two sides of a bar. At twenty-four hours a gap can last almost the entire
    /// window, and without the day the line would read
    /// "not measured for 23 h 45 min (09:25 – 09:10)" - a duration of almost a day next to an
    /// interval that reads as a quarter of an hour backwards. It happens at one hour too, on a
    /// machine switched off across midnight. Decided per pair, not by a threshold, so it is right
    /// in every period instead of in the ones somebody thought to check.
    /// </remarks>
    private static string DescribeRange(HistoryGap gap, bool withDay)
    {
        bool differentDays = gap.Start.ToLocalTime().Date != gap.End.ToLocalTime().Date;
        bool showDay = withDay || differentDays;

        return DescribeInstant(gap.Start, showDay) + " – " + DescribeInstant(gap.End, showDay);
    }

    /// <remarks>
    /// Same format as <see cref="HistoryStrip.Describe"/>: InvariantCulture because what is on
    /// screen is in English.
    /// </remarks>
    private static string DescribeInstant(DateTimeOffset instant, bool withDay) =>
        instant.ToLocalTime().ToString(withDay ? "ddd HH:mm" : "HH:mm", CultureInfo.InvariantCulture);
}