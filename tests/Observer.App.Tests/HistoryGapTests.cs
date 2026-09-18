using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// When a machine was NOT measuring, read from its history once you are back.
/// </summary>
/// <remarks>
/// This is the answer to "what did I miss while the window was closed", and the data to give it
/// already exists on the remote machine's disk. The rules here all serve one single thing: never
/// declare an outage nobody observed. A summary that cries wolf at every open is one you learn
/// to dismiss without reading, which is worse than not having it.
/// </remarks>
public class HistoryGapTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>Points one step apart, starting at <paramref name="start"/>.</summary>
    private static IEnumerable<HistoryPoint> Series(DateTimeOffset start, int count, TimeSpan? step = null) =>
        Enumerable.Range(0, count)
            .Select(i => new HistoryPoint(start + ((step ?? Minute) * i), 60, 0.5d, 0.5d, 0.5d, 0.5d));

    [Fact]
    public void AMachineThatAlwaysMeasuredHasNothingToReport()
    {
        IReadOnlyList<HistoryGap> gaps = HistoryStrip.FindGaps(
            [.. Series(Noon, 30)],
            TimeSpan.FromMinutes(30),
            Minute);

        Assert.Empty(gaps);
    }

    [Fact]
    public void AnOutageInTheMiddleIsReportedWithItsStartAndEnd()
    {
        // Ten minutes of measurements, twenty of nothing, ten of measurements: this is the
        // case the summary exists for.
        List<HistoryPoint> points = [.. Series(Noon, 10), .. Series(Noon + TimeSpan.FromMinutes(30), 10)];

        HistoryGap candidate = Assert.Single(HistoryStrip.FindGaps(points, TimeSpan.FromMinutes(40), Minute));

        Assert.Equal(Noon + TimeSpan.FromMinutes(10), candidate.Start);
        Assert.Equal(Noon + TimeSpan.FromMinutes(30), candidate.End);
        Assert.Equal(TimeSpan.FromMinutes(20), candidate.Duration);
        Assert.False(candidate.AtEdge);
    }

    [Fact]
    public void TwoOutagesStayTwoOldestFirst()
    {
        List<HistoryPoint> points =
        [
            .. Series(Noon, 5),
            .. Series(Noon + TimeSpan.FromMinutes(10), 5),
            .. Series(Noon + TimeSpan.FromMinutes(25), 5),
        ];

        IReadOnlyList<HistoryGap> gaps = HistoryStrip.FindGaps(points, TimeSpan.FromMinutes(30), Minute);

        Assert.Equal(2, gaps.Count);
        Assert.Equal(TimeSpan.FromMinutes(5), gaps[0].Duration);
        Assert.Equal(TimeSpan.FromMinutes(10), gaps[1].Duration);

        // In order, oldest first: that is the order in which you tell the story of a day.
        Assert.True(gaps[0].Start < gaps[1].Start);
    }

    [Fact]
    public void AGapTouchingTheOldestEdgeIsFlaggedAtEdge()
    {
        // On the left there is no telling whether this is an outage or the end of what the
        // service keeps: retention deletes a PREFIX, which is indistinguishable from a machine
        // switched on halfway through the window. Calling it a "40-minute outage" would be
        // making it up.
        IReadOnlyList<HistoryGap> gaps = HistoryStrip.FindGaps(
            [.. Series(Noon + TimeSpan.FromMinutes(40), 20)],
            TimeSpan.FromHours(1),
            Minute);

        Assert.True(Assert.Single(gaps).AtEdge);
    }

    [Fact]
    public void TheREALShapeOfAResponseProducesNoGap()
    {
        // The real shape, which is the only one that counts and the one the other tests here
        // do NOT have: the aggregate level lags by a few minutes because of consolidation, so
        // the last point is NOT now; and the caller asks for more than the window it examines,
        // precisely because the grid anchors to that last point and overruns on the left. With
        // the two together no gap must come out.
        //
        // Built the way the service would send it: a 60-minute window, a ten-minute margin in
        // front (TailFor at one hour), and the series ending five minutes before now. Without
        // the margin in the request an AtEdge gap comes out here and the row becomes "nothing
        // known before" on a machine that measured the whole time.
        TimeSpan window = TimeSpan.FromHours(1);
        TimeSpan margin = TimeSpan.FromMinutes(10);
        DateTimeOffset now = Noon;

        IReadOnlyList<HistoryPoint> response =
            [.. Series(now - window - margin, (int)((window + margin - TimeSpan.FromMinutes(5)) / Minute))];

        Assert.Empty(HistoryStrip.FindGaps(response, window, Minute));

        // And the counter-check, which is what makes this a test and not a ritual: the same
        // series WITHOUT the margin - which is what the code used to do - does produce a gap.
        IReadOnlyList<HistoryPoint> withoutMargin =
            [.. Series(now - window, (int)((window - TimeSpan.FromMinutes(5)) / Minute))];

        Assert.True(Assert.Single(HistoryStrip.FindGaps(withoutMargin, window, Minute)).AtEdge);
    }

    [Fact]
    public void AnOffsetBetweenTwoClocksDoesNotInventOutages()
    {
        // Everything is read in the MACHINE's clock. Two identical series, one shifted by
        // twenty minutes - a virtual machine, a Windows box off the domain - must say the same
        // thing: an offset shifts the series, it does not open gaps in the middle. If the
        // window anchored to the CLIENT's clock, the second would say something else.
        List<HistoryPoint> here = [.. Series(Noon, 10), .. Series(Noon + TimeSpan.FromMinutes(25), 10)];
        List<HistoryPoint> ahead =
        [
            .. Series(Noon + TimeSpan.FromMinutes(20), 10),
            .. Series(Noon + TimeSpan.FromMinutes(45), 10),
        ];

        IReadOnlyList<HistoryGap> a = HistoryStrip.FindGaps(here, TimeSpan.FromMinutes(35), Minute);
        IReadOnlyList<HistoryGap> b = HistoryStrip.FindGaps(ahead, TimeSpan.FromMinutes(35), Minute);

        Assert.Equal(TimeSpan.FromMinutes(15), Assert.Single(a).Duration);
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a[0].Duration, b[0].Duration);
        Assert.Equal(a[0].AtEdge, b[0].AtEdge);

        // And its ends are shifted by exactly the offset, no more and no less.
        Assert.Equal(a[0].Start + TimeSpan.FromMinutes(20), b[0].Start);
    }

    [Fact]
    public void AnEmptySeriesDoesNotInventAWindowLongOutage()
    {
        // "Nothing is known" is not "it was down the whole time". The caller states that
        // difference with another sentence; returning a whole window of gap here would be
        // declaring an outage nobody observed.
        Assert.Empty(HistoryStrip.FindGaps([], TimeSpan.FromHours(1), Minute));
    }

    [Fact]
    public void GapsAreSearchedAtTheSourceStepNotTheBarStep()
    {
        // At seven days the strip's bar covers TWO HOURS: a forty-minute outage ends up inside
        // it as a partial interval and would not be seen at all. Searching at the source step,
        // five minutes, finds it.
        TimeSpan fiveMinutes = TimeSpan.FromMinutes(5);
        List<HistoryPoint> points =
        [
            .. Series(Noon, 2, fiveMinutes),
            .. Series(Noon + TimeSpan.FromMinutes(50), 1, fiveMinutes),
        ];

        HistoryGap gap = Assert.Single(HistoryStrip.FindGaps(points, TimeSpan.FromMinutes(55), fiveMinutes));

        Assert.Equal(TimeSpan.FromMinutes(40), gap.Duration);
        Assert.False(gap.AtEdge);

        // The same data, at the two-hour bar step: those forty minutes no longer appear
        // anywhere.
        Assert.DoesNotContain(
            HistoryStrip.FindGaps(points, TimeSpan.FromDays(7), TimeSpan.FromHours(2)),
            candidate => candidate.Duration == TimeSpan.FromMinutes(40));
    }
}