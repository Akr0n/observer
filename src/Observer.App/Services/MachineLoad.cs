using System.Globalization;
using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Memory;

namespace Observer.App.Services;

/// <summary>
/// Quanto sta lavorando una macchina, in due numeri soli.
/// </summary>
/// <param name="Cpu">Uso della CPU in percentuale, null se non si sa.</param>
/// <param name="Memory">Memory usata in percentuale, null se non si sa.</param>
/// <remarks>
/// <para>
/// Pura e senza finestra, come <see cref="Downtime"/> e <see cref="StatusEscalation"/>: e' una
/// regola di lettura, e le regole di lettura si provano senza disegnare niente.
/// </para>
/// <para>
/// Due metriche fisse e non il catalogo, ed e' una scelta, non una dimenticanza. I quadranti
/// prendono nomi e unita' da <c>/metrics/catalog</c> proprio per non avere costanti compilate
/// dentro, e quella regola vale ancora per loro: un quadrante deve saper mostrare una metricId
/// che non esisteva quando il client e' stato compilato. Qui la domanda e' un'altra. Non e'
/// "cosa misura quella macchina" ma "quale macchina e' in affanno", e a quella rispondono due
/// numeri sempre gli stessi. Gli identificativi arrivano da
/// <see cref="CpuCollector.TotalUsageMetricId"/> e
/// <see cref="MemoryCollector.UsedPercentMetricId"/>, che stanno in <c>Observer.Core</c>: sono
/// il contratto che le due parti condividono gia', non una stringa ricopiata a mano.
/// </para>
/// </remarks>
public sealed record MachineLoad(double? Cpu, double? Memory)
{
    /// <summary>Non si sa niente: macchina giu', o snapshot mai arrivato.</summary>
    public static readonly MachineLoad None = new(null, null);

    /// <summary>Legge i due numeri da un snapshot completo.</summary>
    /// <param name="snapshot">Cio' che <c>/metrics/latest</c> ha risposto, o null.</param>
    /// <returns>I due valori, ciascuno null se quel point non c'e' o non e' misurato.</returns>
    /// <remarks>
    /// I due numeri si leggono uno per uno, e uno puo' mancare mentre l'altro c'e': su una
    /// piattaforma dove la CPU non e' leggibile la memoria lo e' lo stesso, e mostrare cio' che
    /// si sa e' meglio che non mostrare niente. Un point con <c>Status</c> diverso da Ok ha
    /// <c>Value</c> null per costruzione, ma si guarda comunque il <c>Kind</c>: un value
    /// testuale letto come numero darebbe zero, e uno zero inventato accanto al nome di una
    /// macchina si legge come "ferma", che e' l'opposto di "non si sa".
    /// </remarks>
    public static MachineLoad From(MachineSnapshot? snapshot) =>
        snapshot is null
            ? None
            : new MachineLoad(
                NumberFor(snapshot, CpuCollector.TotalUsageMetricId),
                NumberFor(snapshot, MemoryCollector.UsedPercentMetricId));

    /// <summary>La frase da mettere sotto il nome, vuota quando non si sa niente.</summary>
    /// <remarks>
    /// Le etichette sono scritte qui e non lette dal catalogo, di proposito: sono due, non
    /// cambiano, e devono stare in una colonna larga poco piu' di cento pixel. "CPU usage" e
    /// "Memory usage", che sono i nomi veri sotto i quadranti, non ci starebbero - e accanto a
    /// un numero in percentuale non aggiungono niente.
    /// </remarks>
    public string Caption => (Cpu, Memory) switch
    {
        (null, null) => string.Empty,
        (not null, null) => "CPU " + FormatPercent(Cpu.Value),
        (null, not null) => "RAM " + FormatPercent(Memory.Value),
        _ => "CPU " + FormatPercent(Cpu.Value) + " · RAM " + FormatPercent(Memory.Value),
    };

    /// <summary>
    /// Interi e non decimali: la barra laterale risponde a "quale macchina e' in affanno", e a
    /// quella domanda un decimo di point non aggiunge niente. Ne toglie: una cifra che cambia a
    /// ogni lettura attira l'occhio di continuo su una colonna che si guarda proprio per non
    /// doverla guardare.
    /// </summary>
    private static string FormatPercent(double value) =>
        Math.Round(Math.Clamp(value, 0d, 100d)).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <remarks>
    /// Si cerca per identificativo di metricId su TUTTI i collector, senza filtrare prima per
    /// collector: l'identificativo e' unico per costruzione - lo dice il contratto di
    /// <c>MetricDescriptor</c>, "deve coincidere con quello dei punti emessi" - quindi il nome
    /// del collector sarebbe una seconda costante da tenere allineata in cambio di niente.
    /// </remarks>
    private static double? NumberFor(MachineSnapshot snapshot, string metricId)
    {
        foreach (MetricSnapshot collector in snapshot.Collectors)
        {
            foreach (MetricPoint point in collector.Points)
            {
                // Instance null: quella per MACCHINA. Il per-core e il per-disco passano di qui
                // con lo stesso identificativo e un'istanza valorizzata, e prendere il primo che
                // capita darebbe il carico di UN core spacciandolo per quello della macchina.
                if (string.Equals(point.MetricId, metricId, StringComparison.Ordinal)
                    && point.Instance is null
                    && point.Status == CollectorStatus.Ok
                    && point.Value is { Kind: MetricValueKind.Number } value)
                {
                    return value.Number;
                }
            }
        }

        return null;
    }
}