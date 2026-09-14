using System.Globalization;

namespace Observer.App.Services;

/// <summary>What is known about one interval of the strip.</summary>
public enum BarKind
{
    /// <summary>No samples: in that interval the machine was not measuring.</summary>
    Missing = 0,

    /// <summary>Some samples, but not all: covered only in part.</summary>
    Partial = 1,

    /// <summary>Interval covered in full.</summary>
    Measured = 2,
}

/// <summary>One interval of the history strip.</summary>
/// <param name="Start">The instant the interval starts at.</param>
/// <param name="Kind">How much is known about it.</param>
/// <param name="Average">The average of the samples, from 0 to 1. Zero when there are none.</param>
/// <param name="Max">The highest value reached, from 0 to 1.</param>
/// <param name="Min">The lowest value touched, from 0 to 1.</param>
/// <param name="Samples">How many samples fell in the interval.</param>
/// <param name="Expected">How many would have fallen had it been covered in full.</param>
public sealed record HistoryBar(
    DateTimeOffset Start,
    BarKind Kind,
    double Average,
    double Max,
    double Min,
    int Samples,
    int Expected);

/// <summary>An interval in which a machine was not measuring.</summary>
/// <param name="Start">When it stopped, in THAT machine's clock.</param>
/// <param name="End">When it resumed, in that machine's clock.</param>
/// <param name="AtEdge">
/// True when the gap touches the oldest edge of the window examined, that is, when there is no
/// way to tell whether it is an outage or simply the end of what the service keeps.
/// </param>
public sealed record HistoryGap(DateTimeOffset Start, DateTimeOffset End, bool AtEdge)
{
    /// <summary>How long it lasted.</summary>
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// From what the service sends to what gets drawn: the grid of intervals.
/// </summary>
/// <remarks>
/// <b>The service does not send the gaps.</b> An interval in which nothing was sampled simply
/// does not appear in the array of points — it does not arrive with zero samples, it does not
/// arrive at all. Measured by killing the service for 95 seconds: at the one-minute level the
/// bucket for that minute does not exist, and the array jumps straight to the next one.
/// <para>
/// Hence the rule this class exists to enforce: <b>the grid is built from the expected times,
/// and the points are looked up inside it</b>, never the other way round. Walking the array and
/// drawing one small bar per point would give a continuous, full strip even for a machine that
/// was off half the day: the gaps would close up and vanish, and whoever looks would read a
/// machine that was always on. It is the easiest way to tell a lie with true
/// data.
/// </para>
/// <para>
/// An interval covered only in part exists and is a third case: it arrives with a reduced
/// sample count (53 and 31 out of 60, measured) and with an average computed over those alone.
/// It is a plausible number over half a minute, and the strip has to say that it is half a minute.
/// </para>
/// </remarks>
public static class HistoryStrip
{
    /// <summary>How many samples to expect in one interval, at one sample a second.</summary>
    /// <param name="step">How long the interval lasts.</param>
    /// <returns>The number of expected samples, at least one.</returns>
    /// <remarks>
    /// The service samples at 1 Hz, so the expected samples coincide with the seconds. It is
    /// measured: full intervals arrive with 60 samples a minute and 300 at five minutes.
    /// </remarks>
    public static int ExpectedSamplesIn(TimeSpan step) => Math.Max(1, (int)Math.Round(step.TotalSeconds));

    /// <summary>Builds the strip, gaps included.</summary>
    /// <param name="points">The points that arrived from the service, in any order.</param>
    /// <param name="end">The end of the window: the last interval is the one that contains it.</param>
    /// <param name="barCount">How many intervals to show.</param>
    /// <param name="step">How long each interval lasts.</param>
    /// <returns>The intervals from the oldest to the most recent, one per position.</returns>
    public static IReadOnlyList<HistoryBar> Build(
        IReadOnlyList<HistoryPoint> points,
        DateTimeOffset end,
        int barCount,
        TimeSpan step)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfLessThan(barCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);

        int expectedSamples = ExpectedSamplesIn(step);

        // Bucketing comes FIRST, and it is not an extra: when the bar's step is wider than
        // the points' one - a quarter-hour bar over five-minute points - three of them fall in
        // the same interval, and indexing them by timestamp would keep ONE, the last iterated,
        // throwing the other two away. The bar would show the last sample and pass it off as
        // the average of all of them, and the count would say 1 out of 900. Bucket recomputes
        // the average from the SUM, which is the only way to stop intervals with a different
        // number of samples from weighing the same.
        points = Bucket(points, step);

        // The points are indexed by the start of their own interval, rounded to the step: that
        // way a timestamp that arrives a few milliseconds off still falls into the right cell
        // instead of vanishing.
        Dictionary<DateTimeOffset, HistoryPoint> byStart = [];

        foreach (HistoryPoint point in points)
        {
            byStart[AlignTo(point.Timestamp, step)] = point;
        }

        DateTimeOffset newest = AlignTo(end, step);
        List<HistoryBar> strip = new(barCount);

        for (int i = barCount - 1; i >= 0; i--)
        {
            DateTimeOffset start = newest - (step * i);

            strip.Add(byStart.TryGetValue(start, out HistoryPoint? point)
                ? From(point, start, expectedSamples)
                : new HistoryBar(start, BarKind.Missing, 0d, 0d, 0d, 0, expectedSamples));
        }

        return strip;
    }

    /// <summary>When that machine was NOT measuring, within the given window.</summary>
    /// <param name="points">The points that arrived from that machine's service.</param>
    /// <param name="window">How far back to look.</param>
    /// <param name="step">The resolution at which to look for the gaps.</param>
    /// <returns>The gaps from the oldest to the most recent, empty when there are none.</returns>
    /// <remarks>
    /// <para>
    /// Rests on the invariant that <see cref="Build"/> exists to enforce — <b>the service does
    /// not send the gaps</b>, so the grid is built from the expected times and the points are
    /// looked up inside it. Nothing is drawn here: the cells left empty are counted.
    /// </para>
    /// <para>
    /// <b>Everything is in the MACHINE's clock, never in the client's.</b> The window anchors
    /// to the most recent point that machine sent, not to the "now" of whoever is looking. That
    /// is the difference between a summary and a false-alarm generator: two clocks twenty
    /// minutes apart - a virtual machine, a Windows box outside the domain - produce no gap at
    /// all, because an offset shifts the whole series and opens no holes in the middle. And the
    /// consolidation lag excludes itself: after the last point there is no cell left to fill,
    /// so a gap running up to the present is never reported.
    /// </para>
    /// <para>
    /// The step is the SOURCE's and not that of the strip's bar, and it is the first thing to
    /// get wrong: at seven days a bar covers two hours, and a forty-minute outage lands inside
    /// it as a <i>partial</i> interval, which means it would not be seen at
    /// all.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<HistoryGap> FindGaps(
        IReadOnlyList<HistoryPoint> points,
        TimeSpan window,
        TimeSpan step)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, step);

        if (points.Count == 0)
        {
            // No points does not mean "absent the whole time": it means nothing is known, and
            // the caller says so with a different sentence. Returning a gap as long as the
            // window would be inventing an outage nobody ever observed.
            return [];
        }

        DateTimeOffset newest = points[0].Timestamp;

        foreach (HistoryPoint point in points)
        {
            if (point.Timestamp > newest)
            {
                newest = point.Timestamp;
            }
        }

        IReadOnlyList<HistoryBar> bars = Build(points, newest, (int)(window / step), step);
        List<HistoryGap> gaps = [];
        int gapStartIndex = -1;

        for (int i = 0; i < bars.Count; i++)
        {
            if (bars[i].Kind == BarKind.Missing)
            {
                if (gapStartIndex < 0)
                {
                    gapStartIndex = i;
                }

                continue;
            }

            if (gapStartIndex >= 0)
            {
                gaps.Add(new HistoryGap(bars[gapStartIndex].Start, bars[i].Start, AtEdge: gapStartIndex == 0));
                gapStartIndex = -1;
            }
        }

        // A run that reaches the end cannot exist: by construction the last bar contains the
        // most recent point, so it is measured. If the anchoring ever changed, this line would
        // close it anyway instead of losing it in silence.
        if (gapStartIndex >= 0)
        {
            gaps.Add(new HistoryGap(bars[gapStartIndex].Start, bars[^1].Start + step, AtEdge: gapStartIndex == 0));
        }

        return gaps;
    }

    /// <summary>How wide a bar really is, in proportion to how much it covered.</summary>
    /// <param name="bar">The bar to draw.</param>
    /// <param name="width">The full width of the column.</param>
    /// <returns>The width to draw, never below one pixel.</returns>
    /// <remarks>
    /// It is there for the LAST bar of the strip, which is always the interval <b>in
    /// progress</b>: at a one-minute step it holds between zero and sixty seconds of readings
    /// and the difference does not show, but at a two-hour step it can hold five minutes of
    /// them and draw itself identical to a full bar — exactly where the eye reads "now". A bar
    /// that covered a twelfth of its interval is drawn a twelfth wide.
    /// <para>
    /// How much you see it grow depends on whoever re-reads, not on this code, and it is the
    /// reason <c>MainViewModel.HistoryReadDelay</c> does not re-read every step: re-reading at
    /// the step would mean looking at a brand-new bar every time, always at the same fraction,
    /// and the right edge of the strip would stay frozen at that width for the whole session.
    /// </para>
    /// <para>
    /// It holds for every partial bar, not only for the last one: halfway along the strip too,
    /// an interval covered halfway knows less than one covered in full, and the width says so
    /// without needing a third colour. Full bars and gaps are left alone: a gap already has
    /// its own mark, and narrowing a full bar would be a lie the other way round.
    /// </para>
    /// </remarks>
    public static double WidthOf(HistoryBar bar, double width)
    {
        ArgumentNullException.ThrowIfNull(bar);

        if (bar.Kind != BarKind.Partial || bar.Expected <= 0)
        {
            return width;
        }

        double coverage = Math.Clamp((double)bar.Samples / bar.Expected, 0d, 1d);

        // At least one pixel: a bar that exists must not disappear altogether, or it would
        // read as a gap, which means something else.
        return Math.Max(1d, width * coverage);
    }

    /// <summary>Groups dense samples into wider intervals.</summary>
    /// <param name="points">The points to group.</param>
    /// <param name="step">How long the destination interval lasts.</param>
    /// <returns>One point per interval that contains at least one sample.</returns>
    /// <remarks>
    /// It is there for the tail of the strip, which is read from the raw samples. <b>The
    /// average is recomputed from the sum, not as an average of averages</b>: intervals with a
    /// different number of samples would weigh the same, and the result would be a credible,
    /// false number.
    /// </remarks>
    public static IReadOnlyList<HistoryPoint> Bucket(
        IReadOnlyList<HistoryPoint> points,
        TimeSpan step)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);

        Dictionary<DateTimeOffset, (double Sum, double Min, double Max, int Count)> buckets = [];

        foreach (HistoryPoint point in points)
        {
            DateTimeOffset bucketStart = AlignTo(point.Timestamp, step);
            int sampleCount = Math.Max(1, point.Count);

            if (buckets.TryGetValue(bucketStart, out (double Sum, double Min, double Max, int Count) existing))
            {
                buckets[bucketStart] = (
                    existing.Sum + (point.Avg * sampleCount),
                    Math.Min(existing.Min, point.Min),
                    Math.Max(existing.Max, point.Max),
                    existing.Count + sampleCount);
            }
            else
            {
                buckets[bucketStart] = (point.Avg * sampleCount, point.Min, point.Max, sampleCount);
            }
        }

        return [.. buckets
            .OrderBy(bucket => bucket.Key)
            .Select(bucket => new HistoryPoint(
                bucket.Key,
                bucket.Value.Count,
                bucket.Value.Sum / bucket.Value.Count,
                bucket.Value.Min,
                bucket.Value.Max,
                bucket.Value.Sum / bucket.Value.Count))];
    }

    /// <summary>
    /// Merges two readings of the same series, keeping the one with MORE samples where they overlap.
    /// </summary>
    /// <param name="aggregates">The reading that covers the whole window, but is behind.</param>
    /// <param name="tail">The fresh reading of the last few intervals.</param>
    /// <returns>The merged points.</returns>
    /// <remarks>
    /// The strip is built from TWO readings, and the reason is measured: the consolidation of
    /// the aggregates has a four-minute grace, so the one-minute level lags the present moment
    /// by five or six minutes. With the aggregate reading alone the last few bars would
    /// <b>always</b> be empty, and the strip would say "not measured" right at the present
    /// moment — while the gauges above show live values. The tail comes from the raw level,
    /// which is up to date to the second.
    /// </remarks>
    public static IReadOnlyList<HistoryPoint> Merge(
        IReadOnlyList<HistoryPoint> aggregates,
        IReadOnlyList<HistoryPoint> tail)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        ArgumentNullException.ThrowIfNull(tail);

        Dictionary<DateTimeOffset, HistoryPoint> merged = [];

        foreach (HistoryPoint point in aggregates)
        {
            merged[point.Timestamp] = point;
        }

        // Where the two overlap the one with MORE samples wins, not the fresher one. Almost
        // always that is the tail, and it is the reason this function exists: on a half
        // consolidated interval the aggregate has fewer samples and would lie. But there is
        // one interval where it loses, and it is always the same one: the OLDEST of the tail.
        // The raw level is asked for from an arbitrary timestamp - "ten minutes ago" - which
        // does not fall on an interval boundary, so that first interval arrives truncated,
        // with thirty samples out of sixty, while the aggregate has them all. Letting it win,
        // a bar measured in full was drawn half as wide (it is partial), the tooltip said
        // "30 of 60 samples", and average, minimum and maximum skipped the first half of the
        // minute: a spike in there disappeared. The boundary moves at every reading, so the
        // wrong bar was always at the same position in the strip.
        foreach (HistoryPoint point in tail)
        {
            merged[point.Timestamp] =
                merged.TryGetValue(point.Timestamp, out HistoryPoint? existing) && existing.Count > point.Count
                    ? existing
                    : point;
        }

        return [.. merged.Values.OrderBy(point => point.Timestamp)];
    }

    private static HistoryBar From(HistoryPoint point, DateTimeOffset start, int expectedSamples) =>
        new(
            start,
            point.Count >= expectedSamples ? BarKind.Measured : BarKind.Partial,
            point.Avg,
            point.Max,
            point.Min,
            point.Count,
            expectedSamples);

    private static DateTimeOffset AlignTo(DateTimeOffset timestamp, TimeSpan step) =>
        new(timestamp.UtcTicks - (timestamp.UtcTicks % step.Ticks), TimeSpan.Zero);

    /// <summary>Which bar sits under a given x coordinate.</summary>
    /// <param name="x">X coordinate of the pointer, in pixels from the left edge of the strip.</param>
    /// <param name="width">Width of the whole strip.</param>
    /// <param name="barCount">How many bars there are.</param>
    /// <returns>The index, or -1 when the pointer is outside.</returns>
    /// <remarks>
    /// It lives here and not in the control for the same reason as the arc arithmetic: an
    /// off-by-one index breaks nothing, it only shows the time of the bar next door. And the
    /// right edge gets it wrong all by itself — with x equal to the width the division gives
    /// exactly <c>barCount</c>, that is, an index that does not exist.
    /// </remarks>
    public static int IndexAt(double x, double width, int barCount)
    {
        if (barCount <= 0 || width <= 0d || double.IsNaN(x) || x < 0d || x >= width)
        {
            return -1;
        }

        return Math.Clamp((int)(x / (width / barCount)), 0, barCount - 1);
    }

    /// <summary>What to say about a bar to whoever hovers the mouse over it.</summary>
    /// <param name="bars">The bars of the strip.</param>
    /// <param name="index">Which bar.</param>
    /// <returns>The text to show, empty when the index does not exist.</returns>
    /// <remarks>
    /// It says the INTERVAL, not the instant: a bar covers from one minute to two hours
    /// depending on the period chosen, and showing only its start would leave its width to
    /// guesswork. The step is derived from the bars themselves instead of being a constant, so
    /// it stays true whatever period the strip is showing.
    /// <para>
    /// On an empty bar it says so: "not measured" is not "zero", and it is the same distinction
    /// the drawing already makes with the hatching.
    /// </para>
    /// </remarks>
    public static string Describe(IReadOnlyList<HistoryBar> bars, int index)
    {
        ArgumentNullException.ThrowIfNull(bars);

        if (index < 0 || index >= bars.Count)
        {
            return string.Empty;
        }

        HistoryBar bar = bars[index];

        if (bars.Count < 2)
        {
            return FormatTime(bar.Start, includeDay: false);
        }

        TimeSpan step = bars[1].Start - bars[0].Start;

        // Past twenty-four hours the time alone no longer pins anything down: at seven days the
        // same text - "04:00 – 06:00" - appears on SEVEN bars, one per day, and whoever sees a
        // spike (which is the reason you look at a week) has no way to know which day it
        // belongs to. The name of the day is enough, the date is not: between two bars of the
        // same strip at most 83 x 2 h = 166 hours pass, less than a week, so the pair
        // (day, time) cannot repeat. The threshold is strict on purpose: at twenty-four hours
        // the span is exactly one day, the two endpoints do not coincide, and the text stays
        // short where there is no need to lengthen it.
        bool includeDay = (step * bars.Count) > TimeSpan.FromHours(24);

        string rangeText = FormatTime(bar.Start, includeDay) + " – " + FormatTime(bar.Start + step, includeDay);

        return bar.Kind switch
        {
            BarKind.Missing => rangeText + " · not measured",
            BarKind.Partial => rangeText + $" · {bar.Samples} of {bar.Expected} samples",
            _ => rangeText,
        };
    }

    private static string FormatTime(DateTimeOffset timestamp, bool includeDay) =>
        timestamp.ToLocalTime().ToString(includeDay ? "ddd HH:mm" : "HH:mm", CultureInfo.InvariantCulture);
}