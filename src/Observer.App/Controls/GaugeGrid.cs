using Avalonia;
using Avalonia.Controls;

namespace Observer.App.Controls;

/// <summary>La matematica della griglia dei quadranti, separata dal pannello che la applica.</summary>
/// <remarks>
/// Un <c>WrapPanel</c> con quadranti a misura fissa lasciava buchi a fine row e non cresceva
/// mai: a 1240 px di finestra sei quadranti da 148 stavano su una row con un terzo dello
/// spazio vuoto, e a 720 ne stavano tre e mezzo, cioe' tre e un buco. Qui le columns sono
/// quante ne entrano alla misura minima, e poi ogni cell si allarga fino a un massimo per
/// riempire la row: a columns piene, senza buchi, e i quadranti diventano grandi quanto c'e'
/// posto.
/// </remarks>
public static class GaugeGridLayout
{
    /// <summary>Sotto questa cellWidth un quadrante non si legge piu'.</summary>
    public const double MinCellWidth = 148d;

    /// <summary>Sopra questa cellWidth un quadrante e' un poster.</summary>
    public const double MaxCellWidth = 224d;

    /// <summary>Altezza della cell in rapporto alla cellWidth: il quadrante piu' le due scritte sotto.</summary>
    public const double CellHeightRatio = 212d / 148d;

    /// <summary>ColumnGap fra due columns.</summary>
    public const double ColumnGap = 18d;

    /// <summary>ColumnGap fra due rows.</summary>
    public const double RowGap = 8d;

    /// <summary>Quante columns, e quanto larga ogni cell, per lo spazio disponibile.</summary>
    /// <param name="availableWidth">Quanto spazio c'e' in orizzontale; puo' essere infinito.</param>
    /// <param name="count">Quanti quadranti ci sono.</param>
    /// <returns>Columns e cellWidth della cell; zero columns se non c'e' niente da disporre.</returns>
    public static (int Columns, double CellWidth) Plan(double availableWidth, int count)
    {
        if (count <= 0)
        {
            return (0, 0d);
        }

        // Senza un limite - misura dentro un contenitore che non ne da' uno - tutti in fila,
        // alla misura minima: e' il caso in cui non c'e' niente da riempire.
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth))
        {
            return (count, MinCellWidth);
        }

        int columns = (int)Math.Floor((availableWidth + ColumnGap) / (MinCellWidth + ColumnGap));
        columns = Math.Clamp(columns, 1, count);

        double cellWidth = (availableWidth - (ColumnGap * (columns - 1))) / columns;

        // Verso l'alto si ferma al massimo; verso il basso no: in una finestra piu' stretta della
        // cell minima un quadrante piccolo e' meglio di un quadrante tagliato.
        return (columns, Math.Max(1d, Math.Min(cellWidth, MaxCellWidth)));
    }
}

/// <summary>Il pannello che dispone i quadranti secondo <see cref="GaugeGridLayout"/>.</summary>
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