namespace Observer.Core.Units;

/// <summary>
/// A percentage in points, guaranteed finite and non-negative. It exists to keep the 0..1
/// ratio visibly apart from the 0..100 percentage points, which is the most frequent
/// confusion, and to stop a NaN from reaching the JSON serializer.
/// </summary>
/// <remarks>
/// The LOWER bound is enforced, the upper one is not, and that is deliberate: a negative
/// usage percentage means nothing and passes for noise on a chart, while a value above 100
/// is legitimate — a per-process collector on a multi-core machine must be able to say 350%.
/// </remarks>
public readonly record struct Percent
{
    private Percent(double points) => Points = points;

    /// <summary>Value in percentage points, from 0 to 100.</summary>
    public double Points { get; }

    /// <summary>
    /// Converts a 0..1 ratio into percentage points. Returns false if the value is not
    /// finite — a serialized NaN would make Utf8JsonWriter throw, wiping out the entire
    /// HTTP response because of a single metric — or if it is negative.
    /// </summary>
    public static bool TryFromRatio(double ratio, out Percent result)
    {
        if (!double.IsFinite(ratio) || ratio < 0.0)
        {
            result = default;
            return false;
        }

        result = new Percent(ratio * 100.0);
        return true;
    }
}