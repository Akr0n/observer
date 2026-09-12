using System.Globalization;
using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Memory;

namespace Observer.App.Services;

/// <summary>
/// Quanto sta lavorando una macchina, in due numeri soli.
/// </summary>
/// <param name="Cpu">Uso della CPU in percentuale, null se non si sa.</param>
/// <param name="Memoria">Memoria usata in percentuale, null se non si sa.</param>
/// <remarks>
/// <para>
/// Pura e senza finestra, come <see cref="Downtime"/> e <see cref="StatusEscalation"/>: e' una
/// regola di lettura, e le regole di lettura si provano senza disegnare niente.
/// </para>
/// <para>
/// Due metriche fisse e non il catalogo, ed e' una scelta, non una dimenticanza. I quadranti
/// prendono nomi e unita' da <c>/metrics/catalog</c> proprio per non avere costanti compilate
/// dentro, e quella regola vale ancora per loro: un quadrante deve saper mostrare una metrica
/// che non esisteva quando il client e' stato compilato. Qui la domanda e' un'altra. Non e'
/// "cosa misura quella macchina" ma "quale macchina e' in affanno", e a quella rispondono due
/// numeri sempre gli stessi. Gli identificativi arrivano da
/// <see cref="CpuCollector.TotalUsageMetricId"/> e
/// <see cref="MemoryCollector.UsedPercentMetricId"/>, che stanno in <c>Observer.Core</c>: sono
/// il contratto che le due parti condividono gia', non una stringa ricopiata a mano.
/// </para>
/// </remarks>
public sealed record Carico(double? Cpu, double? Memoria)
{
    /// <summary>Non si sa niente: macchina giu', o campionamento mai arrivato.</summary>
    public static readonly Carico Nessuno = new(null, null);

    /// <summary>Legge i due numeri da un campionamento completo.</summary>
    /// <param name="campionamento">Cio' che <c>/metrics/latest</c> ha risposto, o null.</param>
    /// <returns>I due valori, ciascuno null se quel punto non c'e' o non e' misurato.</returns>
    /// <remarks>
    /// I due numeri si leggono uno per uno, e uno puo' mancare mentre l'altro c'e': su una
    /// piattaforma dove la CPU non e' leggibile la memoria lo e' lo stesso, e mostrare cio' che
    /// si sa e' meglio che non mostrare niente. Un punto con <c>Status</c> diverso da Ok ha
    /// <c>Value</c> null per costruzione, ma si guarda comunque il <c>Kind</c>: un valore
    /// testuale letto come numero darebbe zero, e uno zero inventato accanto al nome di una
    /// macchina si legge come "ferma", che e' l'opposto di "non si sa".
    /// </remarks>
    public static Carico Da(MachineSnapshot? campionamento) =>
        campionamento is null
            ? Nessuno
            : new Carico(
                Numero(campionamento, CpuCollector.TotalUsageMetricId),
                Numero(campionamento, MemoryCollector.UsedPercentMetricId));

    /// <summary>La frase da mettere sotto il nome, vuota quando non si sa niente.</summary>
    /// <remarks>
    /// Le etichette sono scritte qui e non lette dal catalogo, di proposito: sono due, non
    /// cambiano, e devono stare in una colonna larga poco piu' di cento pixel. "CPU usage" e
    /// "Memory usage", che sono i nomi veri sotto i quadranti, non ci starebbero - e accanto a
    /// un numero in percentuale non aggiungono niente.
    /// </remarks>
    public string Frase => (Cpu, Memoria) switch
    {
        (null, null) => string.Empty,
        (not null, null) => "CPU " + Percento(Cpu.Value),
        (null, not null) => "RAM " + Percento(Memoria.Value),
        _ => "CPU " + Percento(Cpu.Value) + " · RAM " + Percento(Memoria.Value),
    };

    /// <summary>
    /// Interi e non decimali: la barra laterale risponde a "quale macchina e' in affanno", e a
    /// quella domanda un decimo di punto non aggiunge niente. Ne toglie: una cifra che cambia a
    /// ogni lettura attira l'occhio di continuo su una colonna che si guarda proprio per non
    /// doverla guardare.
    /// </summary>
    private static string Percento(double valore) =>
        Math.Round(Math.Clamp(valore, 0d, 100d)).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <remarks>
    /// Si cerca per identificativo di metrica su TUTTI i collector, senza filtrare prima per
    /// collector: l'identificativo e' unico per costruzione - lo dice il contratto di
    /// <c>MetricDescriptor</c>, "deve coincidere con quello dei punti emessi" - quindi il nome
    /// del collector sarebbe una seconda costante da tenere allineata in cambio di niente.
    /// </remarks>
    private static double? Numero(MachineSnapshot campionamento, string metrica)
    {
        foreach (MetricSnapshot gruppo in campionamento.Collectors)
        {
            foreach (MetricPoint punto in gruppo.Points)
            {
                // Instance null: quella per MACCHINA. Il per-core e il per-disco passano di qui
                // con lo stesso identificativo e un'istanza valorizzata, e prendere il primo che
                // capita darebbe il carico di UN core spacciandolo per quello della macchina.
                if (string.Equals(punto.MetricId, metrica, StringComparison.Ordinal)
                    && punto.Instance is null
                    && punto.Status == CollectorStatus.Ok
                    && punto.Value is { Kind: MetricValueKind.Number } valore)
                {
                    return valore.Number;
                }
            }
        }

        return null;
    }
}