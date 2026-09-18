using Observer.App.Controls;

namespace Observer.App.Tests;

/// <summary>
/// The gauge grid: full columns, no holes, cells that grow up to a ceiling.
/// </summary>
/// <remarks>
/// The panel cannot be tested without a window; the arithmetic can. A mistake here breaks
/// nothing at runtime: it leaves a hole at the end of a row, or a clipped gauge — exactly what
/// this grid exists to remove.
/// </remarks>
public class GaugeGridTests
{
    [Fact]
    public void SixGaugesInAMediumWindowFitTwoRowsOfThree()
    {
        // 600 px: at the minimum width 3 fit (3x148 + 2x18 = 480, the fourth does not), and
        // the three widen to fill the row.
        (int columns, double cellWidth) = GaugeGridLayout.Plan(600d, 6);

        Assert.Equal(3, columns);
        Assert.Equal(188d, cellWidth, precision: 6);
    }

    [Fact]
    public void WithPlentyOfSpaceTheColumnsNeverOutnumberTheGauges()
    {
        // 1240 px would hold seven, but there are six: six columns, one row, and the cell
        // grows until it fills the row.
        (int columns, double cellWidth) = GaugeGridLayout.Plan(1240d, 6);

        Assert.Equal(6, columns);
        Assert.Equal((1240d - (5 * 18d)) / 6d, cellWidth, precision: 6);
    }

    [Fact]
    public void TwoGaugesInAWideWindowDoNotGrowIntoPosters()
    {
        (int columns, double cellWidth) = GaugeGridLayout.Plan(1240d, 2);

        Assert.Equal(2, columns);
        Assert.Equal(GaugeGridLayout.MaxCellWidth, cellWidth);
    }

    [Fact]
    public void BelowTheMinimumWidthOneColumnAndASmallGaugeRatherThanAClippedOne()
    {
        (int columns, double cellWidth) = GaugeGridLayout.Plan(100d, 3);

        Assert.Equal(1, columns);
        Assert.Equal(100d, cellWidth);
    }

    [Fact]
    public void WithNoWidthLimitAllInOneRowAtTheMinimumWidth()
    {
        (int columns, double cellWidth) = GaugeGridLayout.Plan(double.PositiveInfinity, 3);

        Assert.Equal(3, columns);
        Assert.Equal(GaugeGridLayout.MinCellWidth, cellWidth);
    }

    [Fact]
    public void WithNoGaugesThereIsNoGrid() =>
        Assert.Equal((0, 0d), GaugeGridLayout.Plan(900d, 0));

    [Fact]
    public void TheColumnsAreAlwaysAsManyAsFitAtTheMinimumWidth()
    {
        // The property that makes the grid "without holes": with N columns, N cells at the
        // minimum width plus the gaps fit inside the width, and N+1 do not.
        for (double availableWidth = 150d; availableWidth <= 2000d; availableWidth += 37d)
        {
            (int columns, _) = GaugeGridLayout.Plan(availableWidth, 12);

            double used = (columns * GaugeGridLayout.MinCellWidth) + ((columns - 1) * GaugeGridLayout.ColumnGap);
            double withOneMore = used + GaugeGridLayout.MinCellWidth + GaugeGridLayout.ColumnGap;

            Assert.True(used <= availableWidth, $"at {availableWidth}: {columns} columns do not fit");
            Assert.True(withOneMore > availableWidth || columns == 12, $"at {availableWidth}: one more column would have fitted");
        }
    }
}