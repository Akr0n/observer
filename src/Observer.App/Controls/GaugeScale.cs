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
/// Gli angoli sono in degrees e misurati come si misurano nella grafica: zero a ore 3, crescenti
/// in senso <b>orario</b>, perche' la Y cresce verso il basso. La scala parte da 135 degrees (in
/// basso a sinistra), apre 270 degrees e finisce a 405 (in basso a destra). E' la forma di un
/// contagiri d'automobile: il pezzo scoperto sta in basso, dove la lancetta non passa mai.
/// </para>
/// </remarks>
public static class GaugeScale
{
    /// <summary>L'angolo dello zero della scala, in degrees.</summary>
    public const double StartAngle = 135d;

    /// <summary>Di quanto apre la scala, in degrees.</summary>
    public const double SweepAngle = 270d;

    /// <summary>L'angolo del fondo scala, in degrees.</summary>
    public const double EndAngle = StartAngle + SweepAngle;

    /// <summary>Riporta un valore dentro la scala.</summary>
    /// <param name="fraction">Il valore, atteso fra 0 e 1.</param>
    /// <returns>Lo stesso valore, limitato a 0..1; zero se non e' un numero.</returns>
    /// <remarks>
    /// NaN diventa zero, e non e' pignoleria: una percentuale che non si e' potuta misurare
    /// arriva fin qui come NaN, e un NaN dentro un seno propaga NaN nelle coordinate. Avalonia
    /// non disegna una geometria con dentro un NaN, quindi il tachimetro sparirebbe del tutto
    /// — un guasto che si presenta come "il riquadro e' vuoto", senza nominare la sua causa.
    /// </remarks>
    public static double ClampFraction(double fraction) =>
        double.IsNaN(fraction) ? 0d : Math.Clamp(fraction, 0d, 1d);

    /// <summary>L'angolo a cui cade un valore.</summary>
    /// <param name="fraction">Il valore, fra 0 e 1.</param>
    /// <returns>L'angolo in degrees, fra <see cref="StartAngle"/> e <see cref="EndAngle"/>.</returns>
    public static double AngleFor(double fraction) =>
        StartAngle + (ClampFraction(fraction) * SweepAngle);

    /// <summary>L'angolo di una tacca della scala.</summary>
    /// <param name="index">Quale tacca, da 0 alla prima esclusa dopo l'ultima.</param>
    /// <param name="intervals">In quanti intervals e' divisa la scala.</param>
    /// <returns>L'angolo in degrees.</returns>
    public static double TickAngle(int index, int intervals)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intervals, 1);

        return AngleFor((double)index / intervals);
    }

    /// <summary>Il punto che sta a un certo angolo e a una certa distanza dal center.</summary>
    /// <param name="center">Il center della scala.</param>
    /// <param name="radius">La distanza dal center.</param>
    /// <param name="degrees">L'angolo, misurato come descritto nel tipo.</param>
    /// <returns>Il punto.</returns>
    public static Point PointAt(Point center, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180d;

        return new Point(
            center.X + (radius * Math.Cos(radians)),
            center.Y + (radius * Math.Sin(radians)));
    }
}