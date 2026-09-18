using Observer.App.Controls;
using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>
/// The rules the band of gauges at the top of the window rests on.
/// </summary>
/// <remarks>
/// The band collects the percentage rows of every source and holds <b>the same
/// instances</b> that are in the panels below, not copies. If they ever became copies, the
/// needles would stop at the first value read: a fault that on screen looks like a quiet
/// machine, which is the worst way this program can be wrong.
/// </remarks>
public class MetricGroupTests
{
    private static MetricGroupState GroupState(string collector, params MetricRowState[] rows) =>
        new(collector, collector.ToUpperInvariant(), null, MetricSeverity.Ok, rows);

    private static MetricRowState Fraction(string key, string text, double value) =>
        new(key, key, text, value, MetricSeverity.Ok);

    private static MetricRowState TextRow(string key, string text) =>
        new(key, key, text, null, MetricSeverity.Ok);

    [Fact]
    public void UpdatingAPanelKeepsTheSameRowObjects()
    {
        // This is the band's precondition: it collects the references once and expects them to
        // stay valid. Rebuilding the rows on every round would fail nothing here, but it
        // would make every gauge flicker once a second.
        MetricGroup panel = new(GroupState("cpu", Fraction("cpu.usage.total", "12.0 %", 0.12d)));

        MetricRow before = panel.Rows[0];

        panel.Update(GroupState("cpu", Fraction("cpu.usage.total", "88.0 %", 0.88d)));

        Assert.Same(before, panel.Rows[0]);
        Assert.Equal("88.0 %", before.Display);
        Assert.Equal(0.88d, before.Fraction);
    }

    [Fact]
    public void WhenTheMetricListChangesTheRowsAreRebuilt()
    {
        // The mandatory twin: keeping the objects must not mean keeping them once they are no
        // longer the same ones. A metric that appears or disappears has to rebuild the list,
        // or a gauge would show the value of something else.
        MetricGroup panel = new(GroupState("memory", Fraction("memory.used.percent", "40.0 %", 0.4d)));

        MetricRow before = panel.Rows[0];

        panel.Update(GroupState(
            "memory",
            Fraction("memory.used.percent", "41.0 %", 0.41d),
            TextRow("memory.total.bytes", "16.0 GiB")));

        Assert.Equal(2, panel.Rows.Count);
        Assert.NotSame(before, panel.Rows[0]);
    }

    [Fact]
    public void AGroupOfFractionsOnlyLeavesNoTitleHangingOverNothing()
    {
        // Fractions are read off the gauge and are not repeated below. A collector that emits
        // only those must not appear in the text section: its name would be there and, under
        // it, nothing.
        MetricGroup gaugesOnly = new(GroupState("cpu", Fraction("cpu.usage.total", "12.0 %", 0.12d)));

        Assert.False(gaugesOnly.ShowRows);

        MetricGroup mixed = new(GroupState(
            "memory",
            Fraction("memory.used.percent", "40.0 %", 0.4d),
            TextRow("memory.total.bytes", "16.0 GiB")));

        Assert.True(mixed.ShowRows);
    }

    [Fact]
    public void AMetricThatStopsBeingMeasurableLosesItsGaugeAndGoesBackToText()
    {
        // The case that decides whether the band updates itself: the source degrades, the
        // percentage is gone, and that row must stop having a gauge. If the gauge stayed, it
        // would show the last good value as if it were a reading from now.
        MetricGroup panel = new(GroupState("cpu", Fraction("cpu.usage.total", "12.0 %", 0.12d)));

        Assert.True(panel.Rows[0].HasGauge);
        Assert.False(panel.ShowRows);

        panel.Update(GroupState("cpu", TextRow("cpu.usage.total", "not measurable")));

        Assert.False(panel.Rows[0].HasGauge);
        Assert.True(panel.ShowRows);

        // And the fraction stays the last one measured instead of being zeroed. The point is
        // not to preserve it - the gauge disappears anyway - but the needle animates: a zero
        // would give it a target, and for a few tenths of a second it would be seen racing to
        // the bottom of the scale before vanishing, as if the machine had emptied rather than
        // stopped answering.
        Assert.Equal(0.12d, panel.Rows[0].Fraction);
    }

    [Fact]
    public void TheNeedleTravelEndsWellWithinOneSample()
    {
        // The invariant that ties together two numbers written in two different files. A travel
        // as long as the interval would NEVER finish: every sample would restart it from an
        // interpolated position, and the gauge would not sit still on a measured value for even
        // an instant - it would only ever show something in between.
        Assert.True(
            Gauge.NeedleTravelTime < MainViewModel.Interval,
            $"The needle travel ({Gauge.NeedleTravelTime.TotalMilliseconds} ms) must stay shorter "
                + $"than the sampling interval ({MainViewModel.Interval.TotalMilliseconds} ms).");

        // And with a real margin: right at the limit the needle would arrive just as the next
        // sample starts, and would stand still for no time at all.
        Assert.True(Gauge.NeedleTravelTime <= MainViewModel.Interval / 2d);
    }
}