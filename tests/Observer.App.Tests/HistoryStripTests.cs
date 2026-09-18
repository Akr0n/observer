using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// The strip's grid, and the lie it exists to prevent.
/// </summary>
/// <remarks>
/// The service does not send the gaps: an interval with no samples does not arrive with zero
/// samples, it does not arrive at all. Drawing one bar per point received would give a strip
/// that is continuous and full even from a machine that was down half the day — the gaps
/// would close up and vanish, and anyone looking at it would read it as a machine that was never
/// down. It is a lie told with real data: nothing fails, and no other test would see it.
/// </remarks>
public class HistoryStripTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static HistoryPoint PointAt(int minutesAgo, double average, int samples = 60) =>
        new(Now - TimeSpan.FromMinutes(minutesAgo), samples, average, average, average, average);

    [Fact]
    public void APartialBarIsDrawnInProportionToItsCoverage()
    {
        // The last bar of the strip is always the interval IN PROGRESS. At a one-minute step
        // the difference does not show; at two hours, five minutes in, a full bar would say
        // "two hours like this" exactly where the eye reads "now".
        HistoryBar oneTwelfth = new(Now, BarKind.Partial, 0.5d, 0.5d, 0.5d, 600, 7200);

        Assert.Equal(10d, HistoryStrip.WidthOf(oneTwelfth, 120d), 9);
    }

    [Fact]
    public void APartialBarNeverShrinksBelowOnePixel()
    {
        // Below one pixel it would read as a gap, which means something else: there nothing
        // was measured, here little was measured.
        HistoryBar newborn = new(Now, BarKind.Partial, 0.5d, 0.5d, 0.5d, 1, 7200);

        Assert.Equal(1d, HistoryStrip.WidthOf(newborn, 6d), 9);
    }

    [Fact]
    public void FullBarsAndGapsAreDrawnAtFullWidth()
    {
        // Narrowing a full bar would be the same lie the other way round, and a gap already
        // has its own mark: the width speaks only of how much of an interval was covered.
        HistoryBar full = new(Now, BarKind.Measured, 0.5d, 0.5d, 0.5d, 60, 60);
        HistoryBar gap = new(Now, BarKind.Missing, 0d, 0d, 0d, 0, 60);

        Assert.Equal(6d, HistoryStrip.WidthOf(full, 6d), 9);
        Assert.Equal(6d, HistoryStrip.WidthOf(gap, 6d), 9);
    }

    [Fact]
    public void SeveralPointsInOneBarAreAveragedInsteadOfLost()
    {
        // The case that appears as soon as the bar's step exceeds the points' step: a quarter
        // of an hour of bar over five-minute points. Without the bucketing inside Build only
        // ONE of them survived — the last one iterated — and the bar showed that sample,
        // passing it off as the average of all three. With three points at 0.2, 0.5 and 0.8
        // the difference between the true average and the last value is the whole scale.
        List<HistoryPoint> points =
        [
            PointAt(14, 0.2d, samples: 300),
            PointAt(9, 0.5d, samples: 300),
            PointAt(4, 0.8d, samples: 300),
        ];

        // Two bars: all three points fall in the quarter of an hour BEFORE the one in
        // progress, because Now is aligned to exactly 12:00.
        IReadOnlyList<HistoryBar> strip =
            HistoryStrip.Build(points, Now, barCount: 2, TimeSpan.FromMinutes(15));

        HistoryBar full = strip[0];

        Assert.Equal(0.5d, full.Average, 9);
        Assert.Equal(0.2d, full.Min, 9);
        Assert.Equal(0.8d, full.Max, 9);

        // And the samples add up: 900 out of 900, that is a quarter of an hour covered in full.
        Assert.Equal(900, full.Samples);
        Assert.Equal(BarKind.Measured, full.Kind);
    }

    [Fact]
    public void AGapStaysAGapAndKeepsItsPlaceInTime()
    {
        // THE test. Three points over ten intervals must give TEN bars, not three: seven are
        // gaps and must stay in their own place in time. If this one fails, the strip tells
        // whoever turned the machine off that it was never down.
        IReadOnlyList<HistoryBar> strip = HistoryStrip.Build(
            [PointAt(9, 0.5d), PointAt(5, 0.6d), PointAt(0, 0.7d)],
            Now,
            barCount: 10,
            Minute);

        Assert.Equal(10, strip.Count);
        Assert.Equal(7, strip.Count(bar => bar.Kind == BarKind.Missing));

        // And they sit exactly where they must: the first, the fifth and the last.
        Assert.Equal(BarKind.Measured, strip[0].Kind);
        Assert.Equal(BarKind.Measured, strip[4].Kind);
        Assert.Equal(BarKind.Measured, strip[9].Kind);
        Assert.Equal(BarKind.Missing, strip[1].Kind);
    }

    [Fact]
    public void AGapHasNoValueToShow()
    {
        // A missing interval does not carry a zero: a zero is a measurement, and drawing it
        // would say "the machine was idle here" instead of "nothing is known here".
        HistoryBar gap = Assert.Single(HistoryStrip.Build([], Now, barCount: 1, Minute));

        Assert.Equal(BarKind.Missing, gap.Kind);
        Assert.Equal(0, gap.Samples);
    }

    [Fact]
    public void APartlyCoveredIntervalIsNotPassedOffAsComplete()
    {
        // Measured on the real service: stop it halfway through a minute and that minute
        // still arrives, but with 53 samples out of 60, and with an average computed
        // over those alone. It is a plausible number for half a minute, and the strip has to
        // say that it is half a minute.
        IReadOnlyList<HistoryBar> strip = HistoryStrip.Build(
            [PointAt(0, 0.42d, samples: 53)],
            Now,
            barCount: 1,
            Minute);

        Assert.Equal(BarKind.Partial, strip[0].Kind);
        Assert.Equal(53, strip[0].Samples);
        Assert.Equal(60, strip[0].Expected);
    }

    [Fact]
    public void AnOffGridInstantStillFallsInTheRightInterval()
    {
        // The timestamps arrive already aligned, but a few milliseconds of offset are enough
        // for an equality comparison to make the bar disappear. And a bar that disappears
        // reads as "not measured", which is the worst case.
        HistoryPoint offGrid = new(
            Now - TimeSpan.FromMinutes(1) + TimeSpan.FromMilliseconds(37),
            60,
            0.33d,
            0.3d,
            0.4d,
            0.35d);

        IReadOnlyList<HistoryBar> strip = HistoryStrip.Build(
            [offGrid],
            Now,
            barCount: 2,
            Minute);

        Assert.Equal(BarKind.Measured, strip[0].Kind);
        Assert.Equal(0.33d, strip[0].Average);
    }

    [Fact]
    public void TheStripRunsFromOldestToMostRecent()
    {
        IReadOnlyList<HistoryBar> strip = HistoryStrip.Build([], Now, barCount: 3, Minute);

        Assert.True(strip[0].Start < strip[1].Start);
        Assert.True(strip[1].Start < strip[2].Start);
    }

    [Fact]
    public void BucketingDoesNotAverageTheAverages()
    {
        // Two intervals with different coverage: 50 samples at 0.20 and 10 samples at 0.90.
        // The true average is (50*0.20 + 10*0.90) / 60 = 0.3166..., not (0.20+0.90)/2 = 0.55.
        // The average of the averages is a believable, false number, and the easiest mistake.
        IReadOnlyList<HistoryPoint> bucketed = HistoryStrip.Bucket(
            [
                new(Now, 50, 0.20d, 0.10d, 0.30d, 0.20d),
                new(Now + TimeSpan.FromSeconds(50), 10, 0.90d, 0.80d, 0.95d, 0.90d),
            ],
            Minute);

        HistoryPoint merged = Assert.Single(bucketed);

        Assert.Equal(60, merged.Count);
        Assert.Equal(0.31666d, merged.Avg, 4);
        Assert.Equal(0.10d, merged.Min);
        Assert.Equal(0.95d, merged.Max);
    }

    [Fact]
    public void ATailWithMoreSamplesBeatsTheLaggingAggregate()
    {
        // Aggregate consolidation has a four-minute grace, so on the most recent intervals
        // the aggregate is incomplete. Where the two readings overlap the one with more samples
        // has to win (here that is the tail), or it would be the aggregate doing the lying.
        IReadOnlyList<HistoryPoint> merged = HistoryStrip.Merge(
            [new(Now, 12, 0.10d, 0.10d, 0.10d, 0.10d)],
            [new(Now, 60, 0.80d, 0.70d, 0.90d, 0.85d)]);

        HistoryPoint point = Assert.Single(merged);

        Assert.Equal(60, point.Count);
        Assert.Equal(0.80d, point.Avg);
    }

    [Fact]
    public void ATruncatedIntervalDoesNotReplaceACompleteOne()
    {
        // The raw data is asked for from an arbitrary instant - "ten minutes ago" - which does
        // not fall on an interval boundary, so the FIRST interval of the tail always arrives
        // truncated. If it won for the sole reason of being fresher, a bar measured in full
        // would be drawn half as wide (it is partial), the tooltip would say
        // "30 of 60 samples", and average, minimum and maximum would skip half a minute of
        // measurements: a peak in there would vanish. More samples wins, not later arrival.
        IReadOnlyList<HistoryPoint> merged = HistoryStrip.Merge(
            [new(Now, 60, 0.30d, 0.05d, 0.95d, 0.30d)],
            [new(Now, 30, 0.30d, 0.28d, 0.32d, 0.30d)]);

        HistoryPoint point = Assert.Single(merged);

        Assert.Equal(60, point.Count);
        Assert.Equal(0.95d, point.Max);
    }

    [Fact]
    public void MergingKeepsTheIntervalsOnlyOneReadingHas()
    {
        IReadOnlyList<HistoryPoint> merged = HistoryStrip.Merge(
            [new(Now - TimeSpan.FromMinutes(30), 60, 0.10d, 0.1d, 0.1d, 0.1d)],
            [new(Now, 60, 0.80d, 0.8d, 0.8d, 0.8d)]);

        Assert.Equal(2, merged.Count);
        Assert.True(merged[0].Timestamp < merged[1].Timestamp);
    }

    [Fact]
    public void ExpectedSamplesFollowTheIntervalLength()
    {
        // The service samples once a second: that is what makes "how many samples arrived" a
        // measure of coverage, and not a detail.
        Assert.Equal(60, HistoryStrip.ExpectedSamplesIn(TimeSpan.FromMinutes(1)));
        Assert.Equal(300, HistoryStrip.ExpectedSamplesIn(TimeSpan.FromMinutes(5)));
        Assert.Equal(1, HistoryStrip.ExpectedSamplesIn(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void AStripWithNoBarsOrNoStepIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => HistoryStrip.Build([], Now, barCount: 0, Minute));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => HistoryStrip.Build([], Now, barCount: 5, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(0d, 0)]
    [InlineData(9.9d, 0)]
    [InlineData(10d, 1)]
    [InlineData(99.9d, 9)]
    public void ThePointerFallsInTheRightBar(double x, int expected) =>
        Assert.Equal(expected, HistoryStrip.IndexAt(x, width: 100d, barCount: 10));

    [Theory]
    [InlineData(100d)]
    [InlineData(101d)]
    [InlineData(-1d)]
    public void OutsideTheStripThereIsNoBar(double x)
    {
        // The right edge goes wrong all by itself: with x exactly equal to the width the
        // division gives ten, an index that does not exist, and without the check the tooltip
        // would read past the end of the list.
        Assert.Equal(-1, HistoryStrip.IndexAt(x, width: 100d, barCount: 10));
    }

    [Fact]
    public void TheTooltipSaysTheIntervalNotTheInstant()
    {
        // A bar covers anything from one minute to two hours depending on the period: showing
        // only its start would leave how wide it is to guesswork. The step is derived from the
        // bars themselves, not from a constant.
        IReadOnlyList<HistoryBar> strip =
            HistoryStrip.Build([PointAt(1, 0.5d)], Now, barCount: 3, Minute);

        Assert.Matches(@"^\d{2}:\d{2} – \d{2}:\d{2}$", HistoryStrip.Describe(strip, 1));
    }

    [Fact]
    public void PastADayTheTooltipAlsoSaysTheDay()
    {
        // At seven days the strip covers 168 hours in 84 bars and has neither axes nor labels:
        // the tooltip is the only way to place a bar in time, and "04:00 – 06:00" on its own
        // appears on SEVEN bars, one per day. Anyone who sees a peak - which is the reason for
        // looking at a week - would not know which day it belongs to. The weekday name is
        // enough: at most 166 hours separate two bars, so the pair never repeats.
        IReadOnlyList<HistoryBar> week =
            HistoryStrip.Build([], Now, barCount: 84, TimeSpan.FromHours(2));

        Assert.Matches(@"^[A-Za-z]{3} \d{2}:\d{2} – [A-Za-z]{3} \d{2}:\d{2} · ", HistoryStrip.Describe(week, 40));

        // At twenty-four hours the span is exactly one day: the threshold is strict, and the
        // text stays short where there is no need to lengthen it.
        IReadOnlyList<HistoryBar> day =
            HistoryStrip.Build([], Now, barCount: 96, TimeSpan.FromMinutes(15));

        Assert.Matches(@"^\d{2}:\d{2} – \d{2}:\d{2} · ", HistoryStrip.Describe(day, 40));
    }

    [Fact]
    public void OnAGapTheTooltipSaysNothingWasMeasured()
    {
        // "Not measured" is not "zero", and it is the same distinction the drawing already
        // makes with the hatching: here it is said in words, for whoever hovers over it to
        // check.
        IReadOnlyList<HistoryBar> strip = HistoryStrip.Build([], Now, barCount: 3, Minute);

        Assert.EndsWith("not measured", HistoryStrip.Describe(strip, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void OnAHalfCoveredIntervalTheTooltipSaysHowManySamples()
    {
        IReadOnlyList<HistoryBar> strip =
            HistoryStrip.Build([PointAt(1, 0.5d, samples: 31)], Now, barCount: 3, Minute);

        Assert.EndsWith(
            "31 of 60 samples", HistoryStrip.Describe(strip, 1), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void AnIndexThatDoesNotExistProducesNoTooltip(int index)
    {
        IReadOnlyList<HistoryBar> strip = HistoryStrip.Build([], Now, barCount: 3, Minute);

        Assert.Empty(HistoryStrip.Describe(strip, index));
    }
}