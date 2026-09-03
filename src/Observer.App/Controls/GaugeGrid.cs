using Avalonia;
using Avalonia.Controls;

namespace Observer.App.Controls;

/// <summary>La matematica della griglia dei quadranti, separata dal pannello che la applica.</summary>
/// <remarks>
/// Un <c>WrapPanel</c> con quadranti a misura fissa lasciava buchi a fine riga e non cresceva
/// mai: a 1240 px di finestra sei quadranti da 148 stavano su una riga con un terzo dello
/// spazio vuoto, e a 720 ne stavano tre e mezzo, cioe' tre e un buco. Qui le colonne sono
/// quante ne entrano alla misura minima, e poi ogni cella si allarga fino a un massimo per
/// riempire la riga: a colonne piene, senza buchi, e i quadranti diventano grandi quanto c'e'
/// posto.
/// </remarks>
public static class GaugeGridLayout
{
    /// <summary>Sotto questa larghezza un quadrante non si legge piu'.</summary>
    public const double CellaMinima = 148d;

    /// <summary>Sopra questa larghezza un quadrante e' un poster.</summary>
    public const double CellaMassima = 224d;

    /// <summary>Altezza della cella in rapporto alla larghezza: il quadrante piu' le due scritte sotto.</summary>
    public const double Rapporto = 212d / 148d;

    /// <summary>Spazio fra due colonne.</summary>
    public const double Spazio = 18d;

    /// <summary>Spazio fra due righe.</summary>
    public const double SpazioVerticale = 8d;

    /// <summary>Quante colonne, e quanto larga ogni cella, per lo spazio disponibile.</summary>
    /// <param name="larghezzaDisponibile">Quanto spazio c'e' in orizzontale; puo' essere infinito.</param>
    /// <param name="quanti">Quanti quadranti ci sono.</param>
    /// <returns>Colonne e larghezza della cella; zero colonne se non c'e' niente da disporre.</returns>
    public static (int Colonne, double Larghezza) Calcola(double larghezzaDisponibile, int quanti)
    {
        if (quanti <= 0)
        {
            return (0, 0d);
        }

        // Senza un limite - misura dentro un contenitore che non ne da' uno - tutti in fila,
        // alla misura minima: e' il caso in cui non c'e' niente da riempire.
        if (double.IsInfinity(larghezzaDisponibile) || double.IsNaN(larghezzaDisponibile))
        {
            return (quanti, CellaMinima);
        }

        int colonne = (int)Math.Floor((larghezzaDisponibile + Spazio) / (CellaMinima + Spazio));
        colonne = Math.Clamp(colonne, 1, quanti);

        double larghezza = (larghezzaDisponibile - (Spazio * (colonne - 1))) / colonne;

        // Verso l'alto si ferma al massimo; verso il basso no: in una finestra piu' stretta della
        // cella minima un quadrante piccolo e' meglio di un quadrante tagliato.
        return (colonne, Math.Max(1d, Math.Min(larghezza, CellaMassima)));
    }
}

/// <summary>Il pannello che dispone i quadranti secondo <see cref="GaugeGridLayout"/>.</summary>
public sealed class GaugeGrid : Panel
{
    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        List<Control> visibili = [.. Children.Where(figlio => figlio.IsVisible)];

        (int colonne, double larghezza) = GaugeGridLayout.Calcola(availableSize.Width, visibili.Count);

        if (colonne == 0)
        {
            return default;
        }

        Size cella = new(larghezza, larghezza * GaugeGridLayout.Rapporto);

        foreach (Control figlio in visibili)
        {
            figlio.Measure(cella);
        }

        int righe = (visibili.Count + colonne - 1) / colonne;

        return new Size(
            (colonne * cella.Width) + ((colonne - 1) * GaugeGridLayout.Spazio),
            (righe * cella.Height) + ((righe - 1) * GaugeGridLayout.SpazioVerticale));
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        List<Control> visibili = [.. Children.Where(figlio => figlio.IsVisible)];

        (int colonne, double larghezza) = GaugeGridLayout.Calcola(finalSize.Width, visibili.Count);

        if (colonne == 0)
        {
            return finalSize;
        }

        Size cella = new(larghezza, larghezza * GaugeGridLayout.Rapporto);

        for (int i = 0; i < visibili.Count; i++)
        {
            int colonna = i % colonne;
            int riga = i / colonne;

            visibili[i].Arrange(new Rect(
                colonna * (cella.Width + GaugeGridLayout.Spazio),
                riga * (cella.Height + GaugeGridLayout.SpazioVerticale),
                cella.Width,
                cella.Height));
        }

        return finalSize;
    }
}