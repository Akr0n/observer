using Observer.Core.Metrics;

namespace Observer.Service.Persistence;

/// <summary>
/// Turns a snapshot into time-series rows. This is where it is decided WHAT ends up in the
/// history and what does not.
/// </summary>
public static class SnapshotFlattener
{
    /// <summary>Extracts from the snapshot only the values that make sense as a time series.</summary>
    /// <param name="snapshot">The snapshot just sampled.</param>
    /// <returns>One sample for every numeric value that was measured correctly.</returns>
    /// <remarks>
    /// It discards, it does not convert: a missing point does NOT become a zero. On the chart
    /// a zero is data and a gap is a gap, and confusing the two is exactly how a dashboard
    /// lies without anything failing.
    /// </remarks>
    public static IReadOnlyList<SeriesSample> Flatten(MachineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        long timestampMs = snapshot.CapturedAt.ToUnixTimeMilliseconds();
        List<SeriesSample> samples = [];

        foreach (MetricSnapshot collector in snapshot.Collectors)
        {
            foreach (MetricPoint point in collector.Points)
            {
                // Both the Ok status and a present value are needed: either one alone is
                // not enough, because a degraded point has no value and a value without an
                // Ok status is not a measurement.
                if (point.Status != CollectorStatus.Ok || point.Value is not { } value)
                {
                    continue;
                }

                if (!TryAsNumber(value, out double number))
                {
                    continue;
                }

                samples.Add(new SeriesSample(
                    new SeriesKey(collector.CollectorId, point.MetricId, point.Instance ?? string.Empty),
                    value.Kind,
                    timestampMs,
                    number));
            }
        }

        return samples;
    }

    private static bool TryAsNumber(MetricValue value, out double number)
    {
        switch (value.Kind)
        {
            case MetricValueKind.Number:
                number = value.Number;

                // MetricValue.FromNumber rejects non-finite values, but a value that came
                // from JSON does not. A NaN getting into the rollup would make the writing
                // service throw on every pass: the history would stop silently while the
                // endpoints kept answering normally.
                return double.IsFinite(number);

            case MetricValueKind.Flag:
                // Kept as 0/1: this way the interval's average stays readable
                // ("true for half the minute") instead of vanishing from the history.
                number = value.Flag ? 1d : 0d;
                return true;

            case MetricValueKind.Unknown:
            case MetricValueKind.Text:
            default:
                // Text is a constant repeated once a second, not a series; unknown is a
                // partial deserialization that reads as zero and looks like a
                // measurement. Neither of the two goes into the history.
                number = 0d;
                return false;
        }
    }
}
