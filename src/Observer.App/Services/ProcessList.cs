using System.Globalization;

namespace Observer.App.Services;

/// <summary>Una riga dell'elenco dei processi, come arriva dal servizio.</summary>
/// <param name="Pid">Identificatore del processo.</param>
/// <param name="Name">Nome dell'eseguibile.</param>
/// <param name="CpuPercent">Percentuale sull'intera macchina, oppure null se non ancora nota.</param>
/// <param name="WorkingSetBytes">Memoria fisica occupata.</param>
public sealed record ProcessWire(int Pid, string Name, double? CpuPercent, long WorkingSetBytes);

/// <summary>La risposta di <c>/processes</c>.</summary>
/// <param name="CapturedAt">Quando e' stato letto l'elenco.</param>
/// <param name="Processes">I processi, gia' ordinati dal servizio.</param>
public sealed record ProcessListWire(DateTimeOffset CapturedAt, IReadOnlyList<ProcessWire> Processes);

/// <summary>Una riga pronta per lo schermo.</summary>
/// <param name="Pid">Identificatore del processo, che serve per terminarlo.</param>
/// <param name="Nome">Nome dell'eseguibile.</param>
/// <param name="Cpu">La CPU gia' formattata, oppure un trattino se non si sa ancora.</param>
/// <param name="Memoria">La memoria gia' formattata coi prefissi binari.</param>
public sealed record ProcessoMostrato(int Pid, string Nome, string Cpu, string Memoria)
{
    /// <summary>Traduce una riga arrivata dal filo in una riga da mostrare.</summary>
    /// <param name="riga">La riga arrivata.</param>
    /// <returns>La riga da mostrare.</returns>
    /// <remarks>
    /// Una CPU sconosciuta diventa un TRATTINO e non uno zero. Sono due affermazioni diverse -
    /// "non lo so ancora" contro "questo processo e' fermo" - e la seconda, su un elenco
    /// ordinato per consumo, sposterebbe l'attenzione sul programma sbagliato.
    /// </remarks>
    public static ProcessoMostrato Da(ProcessWire riga)
    {
        ArgumentNullException.ThrowIfNull(riga);

        return new ProcessoMostrato(
            riga.Pid,
            riga.Name,
            riga.CpuPercent is { } quota
                ? quota.ToString("F1", CultureInfo.InvariantCulture) + " %"
                : "—",
            MetricFormatting.DescribeBytes(riga.WorkingSetBytes));
    }
}

/// <summary>Esito della lettura dell'elenco dei processi.</summary>
/// <param name="Outcome">Come e' andata.</param>
/// <param name="Problem">Frase pronta per lo schermo, vuota quando l'esito e' Ok.</param>
/// <param name="Processi">Le righe, vuote quando l'esito non e' Ok.</param>
public sealed record ProcessFetch(
    ServiceOutcome Outcome,
    string Problem,
    IReadOnlyList<ProcessoMostrato> Processi);

/// <summary>Esito di un tentativo di terminare un processo.</summary>
/// <param name="Outcome">Come e' andata.</param>
/// <param name="Problem">Frase pronta per lo schermo, vuota quando ha funzionato.</param>
/// <remarks>
/// Un esito proprio e non un semplice booleano: "il processo non c'e' piu'" e "il sistema si
/// e' rifiutato di terminarlo" chiedono due frasi diverse. Il primo capita spesso e non e' un
/// guasto — un processo puo' finire da solo fra l'elenco e il clic — mentre il secondo vuol
/// dire che quel programma non si tocca da qui.
/// </remarks>
public sealed record KillFetch(ServiceOutcome Outcome, string Problem);

/// <summary>Quale risorsa sta dietro un quadrante.</summary>
public static class ProcessResource
{
    /// <summary>La risorsa da chiedere al servizio per la riga indicata, oppure null.</summary>
    /// <param name="chiave">La chiave della riga, nella forma <c>collector|metrica|istanza</c>.</param>
    /// <returns><c>cpu</c>, <c>memory</c>, oppure null se per quella risorsa non si sa rispondere.</returns>
    /// <remarks>
    /// Null per i dischi, e non e' una dimenticanza. Lo spazio occupato su un volume non e'
    /// attribuibile a un processo <i>in esecuzione</i> — chi ha scritto quei file magari non
    /// c'e' piu' da mesi — e il traffico per processo richiede contatori che non sono
    /// portabili e che non sono stati ancora scritti. Un pannello che si aprisse vuoto, o
    /// peggio con l'elenco della CPU sotto il titolo di un disco, direbbe una cosa falsa: e'
    /// meglio che quel quadrante non si apra affatto.
    /// </remarks>
    public static string? Da(string? chiave)
    {
        if (string.IsNullOrEmpty(chiave))
        {
            return null;
        }

        int barra = chiave.IndexOf('|', StringComparison.Ordinal);
        string collector = barra < 0 ? chiave : chiave[..barra];

        return collector switch
        {
            "cpu" => "cpu",
            "memory" => "memory",
            _ => null,
        };
    }
}
