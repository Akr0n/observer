using Avalonia;
using Avalonia.Controls;

namespace Observer.App.Controls;

/// <summary>The gauge grid's arithmetic, separate from the panel that applies it.</summary>
/// <remarks>
/// A <c>WrapPanel</c> with fixed-size gauges left holes at the end of a row and never grew:
/// in a 1240 px window six 148 px gauges sat on one row with a third of the space empty,
/// and at 720 three and a half fitted, that is three and a hole. Here there are as many
/// columns as fit at the minimum size, and then each cell widens up to a maximum to fill
/// the row: full columns, no holes, and the gauges become as big as there is room for
/// them.
/// </remarks>
public static class GaugeGridLayout
{
    /// <summary>Below this width a gauge is no longer readable.</summary>
    public const double MinCellWidth = 148d;

    /// <summary>Above this width a gauge is a poster.</summary>
    public const double MaxCellWidth = 224d;

    /// <summary>Cell height relative to its width: the gauge plus the two captions below it.</summary>
    public const double CellHeightRatio = 212d / 148d;

    /// <summary>Gap between two columns.</summary>
    public const double ColumnGap = 18d;

    /// <summary>Gap between two rows.</summary>
    public const double RowGap = 8d;

    /// <summary>How many columns, and how wide each cell, for the available space.</summary>
    /// <param name="availableWidth">How much horizontal space there is; may be infinite.</param>
    /// <param name="count">How many gauges there are.</param>
    /// <returns>Columns and cell width; zero columns if there is nothing to lay out.</returns>
    public static (int Columns, double CellWidth) Plan(double availableWidth, int count)
    {
        if (count <= 0)
        {
            return (0, 0d);
        }

        // With no limit - measuring inside a container that does not give one - all in a row,
        // at the minimum size: this is the case where there is nothing to fill.
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth))
        {
            return (count, MinCellWidth);
        }

        int columns = (int)Math.Floor((availableWidth + ColumnGap) / (MinCellWidth + ColumnGap));
        columns = Math.Clamp(columns, 1, count);

        double cellWidth = (availableWidth - (ColumnGap * (columns - 1))) / columns;

        // The cell width is capped at the maximum, but not floored at the minimum: in a window
        // narrower than the minimum cell a small gauge is better than a clipped one.
        return (columns, Math.Max(1d, Math.Min(cellWidth, MaxCellWidth)));
    }
}

/// <summary>The panel that lays the gauges out according to <see cref="GaugeGridLayout"/>.</summary>
public sealed class GaugeGrid : Panel
{
    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        List<Control> visibleChildren = [.. Children.Where(child => child.IsVisible)];

        (int columns, double cellWidth) = GaugeGridLayout.Plan(availableSize.Width, visibleChildren.Count);

        if (columns == 0)
        {
            return default;
        }

        Size cell = new(cellWidth, cellWidth * GaugeGridLayout.CellHeightRatio);

        foreach (Control child in visibleChildren)
        {
            child.Measure(cell);
        }

        int rows = (visibleChildren.Count + columns - 1) / columns;

        return new Size(
            (columns * cell.Width) + ((columns - 1) * GaugeGridLayout.ColumnGap),
            (rows * cell.Height) + ((rows - 1) * GaugeGridLayout.RowGap));
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        List<Control> visibleChildren = [.. Children.Where(child => child.IsVisible)];

        (int columns, double cellWidth) = GaugeGridLayout.Plan(finalSize.Width, visibleChildren.Count);

        if (columns == 0)
        {
            return finalSize;
        }

        Size cell = new(cellWidth, cellWidth * GaugeGridLayout.CellHeightRatio);

        for (int i = 0; i < visibleChildren.Count; i++)
        {
            int column = i % columns;
            int row = i / columns;

            visibleChildren[i].Arrange(new Rect(
                column * (cell.Width + GaugeGridLayout.ColumnGap),
                row * (cell.Height + GaugeGridLayout.RowGap),
                cell.Width,
                cell.Height));
        }

        return finalSize;
    }
}