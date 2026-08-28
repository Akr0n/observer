using Avalonia;

namespace Observer.App.Controls;

/// <summary>
/// La scala di un tachimetro: dove cade un valore sull'arco, e dove stanno le sue tacche.
/// </summary>
/// <remarks>
/// Separata dal controllo che disegna, e non per eleganza: e' l'unica parte che puo'
/// sbagliarsi <b>in silenzio</b>. Un errore nella matematica dell'arco non fa fallire niente
/// e non lancia niente — disegna una lancetta che punta nel posto sbagliato, e chi guarda non
/// ha modo di accorgersene, perche' l'unica cosa con cui potrebbe confrontarla e' la lancetta
/// stessa. Un controllo Avalonia non si puo' interrogare senza un ambiente grafico; questa
/// classe si', e infatti ha i suoi test.
/// <para>
/// Gli angoli sono in gradi e misurati come si misurano nella grafica: zero a ore 3, crescenti
/// in senso <b>orario</b>, perche' la Y cresce verso il basso. La scala parte da 135 gradi (in
/// basso a sinistra), apre 270 gradi e finisce a 405 (in basso a destra). E' la forma di un
/// contagiri d'automobile: il pezzo scoperto sta in basso, dove la lancetta non passa mai.
/// </para>
/// </remarks>
public static class GaugeScale
{
    /// <summary>L'angolo dello zero della scala, in gradi.</summary>
    public const double Partenza = 135d;

    /// <summary>Di quanto apre la scala, in gradi.</summary>
    public const double Apertura = 270d;

    /// <summary>L'angolo del fondo scala, in gradi.</summary>
    public const double Arrivo = Partenza + Apertura;

    /// <summary>Riporta un valore dentro la scala.</summary>
    /// <param name="frazione">Il valore, atteso fra 0 e 1.</param>
    /// <returns>Lo stesso valore, limitato a 0..1; zero se non e' un numero.</returns>
    /// <remarks>
    /// NaN diventa zero, e non e' pignoleria: una percentuale che non si e' potuta misurare
    /// arriva fin qui come NaN, e un NaN dentro un seno propaga NaN nelle coordinate. Avalonia
    /// non disegna una geometria con dentro un NaN, quindi il tachimetro sparirebbe del tutto
    /// — un guasto che si presenta come "il riquadro e' vuoto", senza nominare la sua causa.
    /// </remarks>
    public static double Frazione(double frazione) =>
        double.IsNaN(frazione) ? 0d : Math.Clamp(frazione, 0d, 1d);

    /// <summary>L'angolo a cui cade un valore.</summary>
    /// <param name="frazione">Il valore, fra 0 e 1.</param>
    /// <returns>L'angolo in gradi, fra <see cref="Partenza"/> e <see cref="Arrivo"/>.</returns>
    public static double Angolo(double frazione) =>
        Partenza + (Frazione(frazione) * Apertura);

    /// <summary>L'angolo di una tacca della scala.</summary>
    /// <param name="indice">Quale tacca, da 0 alla prima esclusa dopo l'ultima.</param>
    /// <param name="intervalli">In quanti intervalli e' divisa la scala.</param>
    /// <returns>L'angolo in gradi.</returns>
    public static double AngoloDellaTacca(int indice, int intervalli)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalli, 1);

        return Angolo((double)indice / intervalli);
    }

    /// <summary>Il punto che sta a un certo angolo e a una certa distanza dal centro.</summary>
    /// <param name="centro">Il centro della scala.</param>
    /// <param name="raggio">La distanza dal centro.</param>
    /// <param name="gradi">L'angolo, misurato come descritto nel tipo.</param>
    /// <returns>Il punto.</returns>
    public static Point Punto(Point centro, double raggio, double gradi)
    {
        double radianti = gradi * Math.PI / 180d;

        return new Point(
            centro.X + (raggio * Math.Cos(radianti)),
            centro.Y + (raggio * Math.Sin(radianti)));
    }
}