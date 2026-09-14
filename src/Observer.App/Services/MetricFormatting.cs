using System.Globalization;
using Observer.Core.Metrics;

namespace Observer.App.Services;

/// <summary>
/// Turns a measured value into the string that ends up on screen.
/// </summary>
/// <remarks>
/// Everything in <see cref="CultureInfo.InvariantCulture"/>, so with the DOT as the decimal
/// separator in Italian too. This is not an oversight: the project's executables run with
/// <c>System.Globalization.Invariant</c> on (see runtimeconfig.template.json), which on Linux
/// avoids having to install ICU; in that mode an Italian culture would not exist anyway and the
/// result would be identical, but with CA1305 complaining.
/// </remarks>
public static class MetricFormatting
{
    /// <summary>How a yes is written on screen.</summary>
    /// <remarks>
    /// A constant and not a literal because <c>SnapshotProjection</c> compares against it: the
    /// two words have to stay the same, and if one day they change it must be the compiler that
    /// changes them, not whoever happens to remember.
    /// </remarks>
    public const string Yes = "Yes";

    /// <summary>How a no is written on screen.</summary>
    public const string No = "No";

    private static readonly string[] BinaryPrefixes = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    /// <summary>
    /// Describes a value, using the catalog's unit when there is one.
    /// </summary>
    /// <param name="value">The measured value.</param>
    /// <param name="unit">The unit the catalog declares, or null when it is unknown.</param>
    public static string Describe(MetricValue value, MetricUnit? unit)
    {
        switch (value.Kind)
        {
            case MetricValueKind.Number:
                return DescribeNumber(value.Number, unit);

            case MetricValueKind.Text:
                return value.Text ?? string.Empty;

            case MetricValueKind.Flag:
                return value.Flag ? Yes : No;

            default:
                // An unknown Kind almost always means deserialization did not hook up the
                // constructor: the value would be zero and would look like a valid measurement.
                // Better to say so than to show an invented zero.
                return "unrecognized value type: service and client disagree on the data format";
        }
    }

    /// <summary>
    /// The 0..1 fraction to hand to a bar, or null if the metric is not a percentage.
    /// </summary>
    public static double? Fraction(MetricValue value, MetricUnit? unit)
    {
        if (value.Kind != MetricValueKind.Number || unit?.Symbol != "%")
        {
            return null;
        }

        return Math.Clamp(value.Number / 100d, 0d, 1d);
    }

    /// <summary>Formats a quantity of bytes with the binary prefixes.</summary>
    public static string DescribeBytes(double bytes)
    {
        if (!double.IsFinite(bytes))
        {
            return "value isn't a finite number";
        }

        double sign = bytes < 0d ? -1d : 1d;
        double remainder = Math.Abs(bytes);
        int prefixIndex = 0;

        while (remainder >= 1024d && prefixIndex < BinaryPrefixes.Length - 1)
        {
            remainder /= 1024d;
            prefixIndex++;
        }

        string formatted = prefixIndex == 0
            ? (sign * remainder).ToString("F0", CultureInfo.InvariantCulture)
            : (sign * remainder).ToString("F1", CultureInfo.InvariantCulture);

        return formatted + " " + BinaryPrefixes[prefixIndex];
    }

    private static string DescribeNumber(double number, MetricUnit? unit)
    {
        string? symbol = unit?.Symbol;

        if (symbol == "%")
        {
            return number.ToString("F1", CultureInfo.InvariantCulture) + " %";
        }

        if (symbol == "B")
        {
            return DescribeBytes(number);
        }

        // A rate is still a quantity of bytes: without this branch it would fall into the generic
        // format and would read "449852 B/s", with the binary prefixes already written two lines
        // above.
        if (symbol == "B/s")
        {
            return DescribeBytes(number) + "/s";
        }

        string text = number == Math.Floor(number) && Math.Abs(number) < 1e15d
            ? number.ToString("F0", CultureInfo.InvariantCulture)
            : number.ToString("F2", CultureInfo.InvariantCulture);

        return string.IsNullOrEmpty(symbol) ? text : text + " " + symbol;
    }
}