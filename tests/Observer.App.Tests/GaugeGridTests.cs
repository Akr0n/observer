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
    public void SeiQuadrantiInUnaFinestraMediaStannoSuDueRigheDaTre()
    {
        // 600 px: alla misura minima ce ne stanno 3 (3x148 + 2x18 = 480, la quarta non entra),
        // e i tre si allargano a riempire la riga.
        (int colonne, double larghezza) = GaugeGridLayout.Calcola(600d, 6);

        Assert.Equal(3, colonne);
        Assert.Equal(188d, larghezza, precision: 6);
    }

    [Fact]
    public void ConTantoSpazioLeColonneNonSuperanoIQuadranti()
    {
        // 1240 px ne conterrebbe sette, ma sono sei: sei colonne, una riga, e la cella cresce
        // fino a riempirla.
        (int colonne, double larghezza) = GaugeGridLayout.Calcola(1240d, 6);

        Assert.Equal(6, colonne);
        Assert.Equal((1240d - (5 * 18d)) / 6d, larghezza, precision: 6);
    }

    [Fact]
    public void DueQuadrantiInUnaFinestraLargaNonDiventanoManifesti()
    {
        (int colonne, double larghezza) = GaugeGridLayout.Calcola(1240d, 2);

        Assert.Equal(2, colonne);
        Assert.Equal(GaugeGridLayout.CellaMassima, larghezza);
    }

    [Fact]
    public void SottoLaMisuraMinimaUnaColonnaSolaEUnQuadrantePiccoloInveceCheTagliato()
    {
        (int colonne, double larghezza) = GaugeGridLayout.Calcola(100d, 3);

        Assert.Equal(1, colonne);
        Assert.Equal(100d, larghezza);
    }

    [Fact]
    public void SenzaUnLimiteTuttiInFilaAllaMisuraMinima()
    {
        (int colonne, double larghezza) = GaugeGridLayout.Calcola(double.PositiveInfinity, 3);

        Assert.Equal(3, colonne);
        Assert.Equal(GaugeGridLayout.CellaMinima, larghezza);
    }

    [Fact]
    public void SenzaQuadrantiNonCEGriglia() =>
        Assert.Equal((0, 0d), GaugeGridLayout.Calcola(900d, 0));

    [Fact]
    public void LeColonneSonoSempreQuanteNeEntranoAllaMisuraMinima()
    {
        // La proprieta' che rende la griglia "senza buchi": con N colonne, N celle alla misura
        // minima piu' gli spazi entrano nella larghezza, e N+1 no.
        for (double larghezza = 150d; larghezza <= 2000d; larghezza += 37d)
        {
            (int colonne, _) = GaugeGridLayout.Calcola(larghezza, 12);

            double occupato = (colonne * GaugeGridLayout.CellaMinima) + ((colonne - 1) * GaugeGridLayout.Spazio);
            double conUnaInPiu = occupato + GaugeGridLayout.CellaMinima + GaugeGridLayout.Spazio;

            Assert.True(occupato <= larghezza, $"a {larghezza}: {colonne} colonne non entrano");
            Assert.True(conUnaInPiu > larghezza || colonne == 12, $"a {larghezza}: ci stava una colonna in piu'");
        }
    }
}