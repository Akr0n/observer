using Observer.App.Controls;

namespace Observer.App.Tests;

/// <summary>
/// La griglia dei quadranti: colonne piene, niente buchi, celle che crescono fino a un tetto.
/// </summary>
/// <remarks>
/// Il pannello non si puo' provare senza una finestra; la matematica si'. Un errore qui non
/// fa fallire nulla a runtime: lascia un buco a fine riga o un quadrante tagliato, che e'
/// esattamente cio' che si sta togliendo.
/// </remarks>
public class GaugeGridTests
{
    [Fact]
    public void SixGaugesInAMediumWindowFitTwoRowsOfThree()
    {
        // 600 px: alla misura minima ce ne stanno 3 (3x148 + 2x18 = 480, la quarta non entra),
        // e i tre si allargano a riempire la riga.
        (int columns, double cellWidth) = GaugeGridLayout.Plan(600d, 6);

        Assert.Equal(3, columns);
        Assert.Equal(188d, cellWidth, precision: 6);
    }

    [Fact]
    public void WithPlentyOfSpaceTheColumnsNeverOutnumberTheGauges()
    {
        // 1240 px ne conterrebbe sette, ma sono sei: sei colonne, una riga, e la cella cresce
        // fino a riempirla.
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
        // La proprieta' che rende la griglia "senza buchi": con N colonne, N celle alla misura
        // minima piu' gli spazi entrano nella larghezza, e N+1 no.
        for (double cellWidth = 150d; cellWidth <= 2000d; cellWidth += 37d)
        {
            (int columns, _) = GaugeGridLayout.Plan(cellWidth, 12);

            double used = (columns * GaugeGridLayout.MinCellWidth) + ((columns - 1) * GaugeGridLayout.ColumnGap);
            double withOneMore = used + GaugeGridLayout.MinCellWidth + GaugeGridLayout.ColumnGap;

            Assert.True(used <= cellWidth, $"a {cellWidth}: {columns} colonne non entrano");
            Assert.True(withOneMore > cellWidth || columns == 12, $"a {cellWidth}: ci stava una colonna in piu'");
        }
    }
}