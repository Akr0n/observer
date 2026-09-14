using Observer.Core.Units;

namespace Observer.Core.Tests;

/// <summary>
/// The units of measure are the foundation: every RAM metric goes through ByteSize and every
/// percentage through Percent. An error here produces believable but wrong numbers, which is
/// the most expensive category of bug in a dashboard.
/// </summary>
public class UnitTypeTests
{
    [Fact]
    public void ByteSize_FromKibibytes_MultipliesBy1024Not1000()
    {
        // /proc/meminfo says "kB" but means KiB (1024 bytes). Anyone who reads "kB" and
        // multiplies by 1000 gets plausible numbers that are wrong by 2.4%: no crash, no
        // alarm, just a dashboard that lies.
        ByteSize fourKiB = ByteSize.FromKibibytes(4);

        Assert.Equal(4096L, fourKiB.Bytes);
    }

    [Fact]
    public void ByteSize_SaturatingSubtract_NeverGoesNegative()
    {
        // On some VMs "available" momentarily exceeds "total". Without saturation the used
        // figure would go negative and the chart would go haywire with no error at all.
        ByteSize ten = ByteSize.FromBytes(10);
        ByteSize ninetyNine = ByteSize.FromBytes(99);

        ByteSize result = ten.SaturatingSubtract(ninetyNine);

        Assert.Equal(0L, result.Bytes);
    }

    [Fact]
    public void Percent_TryFromRatio_RejectsNaN()
    {
        // A serialized NaN makes Utf8JsonWriter throw and wipes out the ENTIRE HTTP response
        // over a single metric. It has to be refused at the source, not downstream.
        bool succeeded = Percent.TryFromRatio(double.NaN, out Percent _);

        Assert.False(succeeded);
    }

    [Fact]
    public void Percent_TryFromRatio_RejectsNegativeRatios()
    {
        // A negative usage percentage means nothing, and on a chart it passes for noise.
        // The type has to enforce its own contract, otherwise every future collector
        // inherits the same trap: closing it here closes it for all of them.
        bool succeeded = Percent.TryFromRatio(-0.5, out Percent _);

        Assert.False(succeeded);
    }

    [Fact]
    public void Percent_TryFromRatio_AcceptsAbove100()
    {
        // The upper bound must NOT be enforced, and that is a deliberate choice: a future
        // per-process collector on a multi-core machine must be able to say 350%, which is a
        // legitimate value and not an error. Constrain only the lower bound.
        bool succeeded = Percent.TryFromRatio(3.5, out Percent aboveOneHundred);

        Assert.True(succeeded);
        Assert.Equal(350.0, aboveOneHundred.Points);
    }

    [Fact]
    public void Percent_TryFromRatio_ConvertsTheRatioToPercentagePoints()
    {
        // The 0..1 ratio and the 0..100 points are the most common confusion: 0.5 must
        // become 50, not 0.5.
        bool succeeded = Percent.TryFromRatio(0.5, out Percent half);

        Assert.True(succeeded);
        Assert.Equal(50.0, half.Points);
    }
}