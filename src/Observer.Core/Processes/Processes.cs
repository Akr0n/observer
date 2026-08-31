using System.ComponentModel;
using System.Diagnostics;
using Observer.Core.Units;

namespace Observer.Core.Processes;

/// <summary>I contatori grezzi di UN processo, come li da' il sistema operativo.</summary>
/// <param name="Pid">Identificatore del processo.</param>
/// <param name="Name">Nome dell'eseguibile, senza percorso.</param>
/// <param name="Cpu">Tempo di processore consumato da quando il processo e' partito.</param>
/// <param name="WorkingSet">Memoria fisica occupata adesso.</param>
public readonly record struct ProcessTimes(int Pid, string Name, TimeSpan Cpu, ByteSize WorkingSet);

/// <summary>Quanto sta consumando un processo, pronto da mostrare.</summary>
/// <param name="Pid">Identificatore del processo.</param>
/// <param name="Name">Nome dell'eseguibile.</param>
/// <param name="CpuPercent">
/// Percentuale di CPU sull'INTERA macchina, non su un core: 100 vuol dire tutti i core
/// occupati. Null quando non si sa ancora — al primo giro, o per un processo appena nato —
/// che e' diverso da zero e non va confuso con "sta fermo".
/// </param>
/// <param name="WorkingSet">Memoria fisica occupata.</param>
public readonly record struct ProcessUsage(int Pid, string Name, double? CpuPercent, ByteSize WorkingSet);

/// <summary>Porta di lettura dell'elenco dei processi.</summary>
public interface IProcessLister
{
    /// <summary>Legge i processi. False quando l'elenco non si riesce a ottenere affatto.</summary>
    /// <param name="processes">I processi letti.</param>
    /// <returns>True se la lettura e' riuscita.</returns>
    bool TryList(out IReadOnlyList<ProcessTimes> processes);
}

/// <summary>
/// Adattatore reale, sopra <see cref="Process"/>.
/// </summary>
/// <remarks>
/// Uno solo per tutte e due le piattaforme, e non e' pigrizia: nome, memoria occupata e tempo
/// di processore sono gia' portabili nella libreria standard. Un provider per sistema
/// operativo servira' quando si vorra' l'I/O per processo, che portabile non e'.
/// <para>
/// Un processo che sparisce fra l'elenco e la lettura dei suoi contatori NON fa fallire gli
/// altri: sparisce e basta. E' la norma, non l'eccezione — su una macchina viva qualcosa
/// muore in continuazione, e un elenco che si rifiuta di rispondere per quello sarebbe
/// inutilizzabile proprio quando serve.
/// </para>
/// </remarks>
public sealed class SystemProcessLister : IProcessLister
{
    /// <inheritdoc />
    public bool TryList(out IReadOnlyList<ProcessTimes> processes)
    {
        List<ProcessTimes> trovati = [];

        foreach (Process processo in Process.GetProcesses())
        {
            using (processo)
            {
                if (TryLeggi(processo, out ProcessTimes lettura))
                {
                    trovati.Add(lettura);
                }
            }
        }

        processes = trovati;

        return true;
    }

    private static bool TryLeggi(Process processo, out ProcessTimes lettura)
    {
        lettura = default;

        try
        {
            lettura = new ProcessTimes(
                processo.Id,
                processo.ProcessName,
                processo.TotalProcessorTime,
                ByteSize.FromBytes(processo.WorkingSet64));

            return true;
        }
        catch (InvalidOperationException)
        {
            // Finito fra l'enumerazione e la lettura dei suoi contatori.
            return false;
        }
        catch (Win32Exception)
        {
            // Su Windows i processi protetti rifiutano il tempo di processore anche a
            // LocalSystem; su Linux capita per quelli di altri utenti. Fuori dall'elenco:
            // meglio una riga in meno che un elenco che non arriva.
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
