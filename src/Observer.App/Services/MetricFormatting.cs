using System.Globalization;
using Observer.Core.Metrics;

namespace Observer.App.Services;

/// <summary>
/// Trasforma un valore misurato nella stringa che finisce a schermo.
/// </summary>
/// <remarks>
/// Tutto in <see cref="CultureInfo.InvariantCulture"/>, quindi con il PUNTO come separatore
/// decimale anche in italiano. Non e' una svista: gli eseguibili del progetto girano con
/// <c>System.Globalization.Invariant</c> attivo (vedi runtimeconfig.template.json), che su
/// Linux evita di dover installare ICU; in quella modalita' una cultura italiana non
/// esisterebbe comunque e il risultato sarebbe identico, ma con CA1305 a lamentarsi.
/// </remarks>
public static class MetricFormatting
{
    private static readonly string[] PrefissiBinari = ["byte", "KiB", "MiB", "GiB", "TiB", "PiB"];

    /// <summary>
    /// Descrive un valore, usando l'unita' del catalogo quando c'e'.
    /// </summary>
    /// <param name="value">Il valore misurato.</param>
    /// <param name="unit">L'unita' dichiarata dal catalogo, oppure null se sconosciuta.</param>
    public static string Describe(MetricValue value, MetricUnit? unit)
    {
        switch (value.Kind)
        {
            case MetricValueKind.Number:
                return DescribeNumber(value.Number, unit);

            case MetricValueKind.Text:
                return value.Text ?? string.Empty;

            case MetricValueKind.Flag:
                return value.Flag ? "si'" : "no";

            default:
                // Kind sconosciuto significa quasi sempre che la deserializzazione non ha
                // agganciato il costruttore: il numero sarebbe zero e sembrerebbe una misura
                // valida. Meglio dirlo che mostrare uno zero inventato.
                return "valore di tipo non riconosciuto: servizio e client non parlano lo stesso formato";
        }
    }

    /// <summary>
    /// La frazione 0..1 da dare a una barra, oppure null se la metrica non e' una percentuale.
    /// </summary>
    public static double? Fraction(MetricValue value, MetricUnit? unit)
    {
        if (value.Kind != MetricValueKind.Number || unit?.Symbol != "%")
        {
            return null;
        }

        return Math.Clamp(value.Number / 100d, 0d, 1d);
    }

    /// <summary>Formatta una quantita' di byte con i prefissi binari.</summary>
    public static string DescribeBytes(double bytes)
    {
        if (!double.IsFinite(bytes))
        {
            return "valore non rappresentabile";
        }

        double segno = bytes < 0d ? -1d : 1d;
        double resto = Math.Abs(bytes);
        int prefisso = 0;

        while (resto >= 1024d && prefisso < PrefissiBinari.Length - 1)
        {
            resto /= 1024d;
            prefisso++;
        }

        string numero = prefisso == 0
            ? (segno * resto).ToString("F0", CultureInfo.InvariantCulture)
            : (segno * resto).ToString("F1", CultureInfo.InvariantCulture);

        return numero + " " + PrefissiBinari[prefisso];
    }

    private static string DescribeNumber(double numero, MetricUnit? unit)
    {
        string? simbolo = unit?.Symbol;

        if (simbolo == "%")
        {
            return numero.ToString("F1", CultureInfo.InvariantCulture) + " %";
        }

        if (simbolo == "byte")
        {
            return DescribeBytes(numero);
        }

        string testo = numero == Math.Floor(numero) && Math.Abs(numero) < 1e15d
            ? numero.ToString("F0", CultureInfo.InvariantCulture)
            : numero.ToString("F2", CultureInfo.InvariantCulture);

        return string.IsNullOrEmpty(simbolo) ? testo : testo + " " + simbolo;
    }
}
