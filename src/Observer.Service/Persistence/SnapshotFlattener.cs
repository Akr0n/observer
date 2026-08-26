using Observer.Core.Metrics;

namespace Observer.Service.Persistence;

/// <summary>
/// Trasforma uno snapshot in righe di serie temporali. E' il punto in cui si decide COSA
/// finisce nello storico e cosa no.
/// </summary>
public static class SnapshotFlattener
{
    /// <summary>Estrae dallo snapshot i soli valori che hanno senso come serie nel tempo.</summary>
    /// <param name="snapshot">Lo snapshot appena campionato.</param>
    /// <returns>Un campione per ogni valore numerico misurato correttamente.</returns>
    /// <remarks>
    /// Scarta e non converte: un punto mancante NON diventa uno zero. Nel grafico uno zero
    /// e' un dato e un buco e' un buco, e confonderli e' esattamente il modo in cui una
    /// dashboard mente senza che nulla fallisca.
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
                // Serve sia lo stato Ok sia il valore presente: uno solo dei due non basta,
                // perche' un punto degradato non ha valore e un valore senza stato Ok non e'
                // una misura.
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

                // MetricValue.FromNumber rifiuta i non finiti, ma un valore arrivato da JSON
                // no. Un NaN che entrasse nel rollup farebbe lanciare il servizio di
                // scrittura a ogni giro: lo storico si fermerebbe in silenzio mentre gli
                // endpoint continuano a rispondere normalmente.
                return double.IsFinite(number);

            case MetricValueKind.Flag:
                // Conservato come 0/1: cosi' la media dell'intervallo resta leggibile
                // ("vero per meta' del minuto") invece di sparire dallo storico.
                number = value.Flag ? 1d : 0d;
                return true;

            case MetricValueKind.Unknown:
            case MetricValueKind.Text:
            default:
                // Il testo e' una costante ripetuta una volta al secondo, non una serie; lo
                // sconosciuto e' una deserializzazione parziale che vale zero e sembra una
                // misura. Nessuno dei due entra nello storico.
                number = 0d;
                return false;
        }
    }
}
