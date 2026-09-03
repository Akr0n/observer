using System.ComponentModel;
using System.Diagnostics;
using Observer.Core.Units;

namespace Observer.Core.Processes;

/// <summary>I contatori grezzi di UN processo, come li da' il sistema operativo.</summary>
/// <param name="Pid">Identificatore del processo.</param>
/// <param name="Name">Nome dell'eseguibile, senza percorso.</param>
/// <param name="Cpu">Tempo di processore consumato da quando il processo e' partito.</param>
/// <param name="WorkingSet">Memoria fisica occupata adesso.</param>
/// <param name="IoBytes">
/// Byte letti e scritti dal processo da quando e' partito, attraverso le chiamate di I/O: file,
/// pipe e socket insieme, letture servite dalla cache comprese. Null quando il sistema non li ha
/// voluti dire — su Linux e' la norma per i processi di un altro utente.
/// </param>
public readonly record struct ProcessTimes(
    int Pid, string Name, TimeSpan Cpu, ByteSize WorkingSet, ulong? IoBytes = null);

/// <summary>Quanto sta consumando un processo, pronto da mostrare.</summary>
/// <param name="Pid">Identificatore del processo.</param>
/// <param name="Name">Nome dell'eseguibile.</param>
/// <param name="CpuPercent">
/// Percentuale di CPU sull'INTERA macchina, non su un core: 100 vuol dire tutti i core
/// occupati. Null quando non si sa ancora — al primo giro, o per un processo appena nato —
/// che e' diverso da zero e non va confuso con "sta fermo".
/// </param>
/// <param name="WorkingSet">Memoria fisica occupata.</param>
/// <param name="IoBytesPerSecond">
/// Byte al secondo letti e scritti dal processo, sull'ultimo intervallo. Null per le stesse
/// ragioni della CPU — primo giro, processo appena nato — e in piu' quando il sistema non
/// fornisce il contatore.
/// </param>
public readonly record struct ProcessUsage(
    int Pid, string Name, double? CpuPercent, ByteSize WorkingSet, double? IoBytesPerSecond = null);

/// <summary>Porta di lettura dell'elenco dei processi.</summary>
public interface IProcessLister
{
    /// <summary>Legge i processi. False quando l'elenco non si riesce a ottenere affatto.</summary>
    /// <param name="processes">I processi letti.</param>
    /// <returns>True se la lettura e' riuscita.</returns>
    bool TryList(out IReadOnlyList<ProcessTimes> processes);
}

/// <summary>Porta di lettura del contatore di I/O di UN processo.</summary>
/// <remarks>
/// Separata dall'elenco perche' e' l'unica parte che non e' portabile: nome, memoria e tempo di
/// processore li da' la libreria standard su entrambi i sistemi, i byte trasferiti no.
/// </remarks>
public interface IProcessIoReader
{
    /// <summary>Legge i byte letti e scritti dal processo da quando e' partito.</summary>
    /// <param name="pid">Identificatore del processo.</param>
    /// <param name="bytes">Il totale, letture piu' scritture.</param>
    /// <returns>False quando il sistema non lo dice, per quel processo.</returns>
    bool TryRead(int pid, out ulong bytes);
}

/// <summary>
/// Adattatore reale, sopra <see cref="Process"/>.
/// </summary>
/// <remarks>
/// Uno solo per tutte e due le piattaforme, e non e' pigrizia: nome, memoria occupata e tempo
/// di processore sono gia' portabili nella libreria standard. L'I/O per processo invece non lo
/// e', e arriva da un <see cref="IProcessIoReader"/> per sistema operativo, facoltativo: senza,
/// quella colonna resta sconosciuta e il resto dell'elenco non ne risente.
/// <para>
/// Un processo che sparisce fra l'elenco e la lettura dei suoi contatori NON fa fallire gli
/// altri: sparisce e basta. E' la norma, non l'eccezione — su una macchina viva qualcosa
/// muore in continuazione, e un elenco che si rifiuta di rispondere per quello sarebbe
/// inutilizzabile proprio quando serve.
/// </para>
/// </remarks>
public sealed class SystemProcessLister : IProcessLister
{
    private readonly IProcessIoReader? io;

    /// <summary>Crea l'adattatore, con o senza il lettore dell'I/O.</summary>
    /// <param name="ioReader">Da dove leggere i byte trasferiti, o null per non leggerli.</param>
    public SystemProcessLister(IProcessIoReader? ioReader = null)
    {
        io = ioReader;
    }

    /// <inheritdoc />
    public bool TryList(out IReadOnlyList<ProcessTimes> processes)
    {
        List<ProcessTimes> trovati = [];

        foreach (Process processo in Process.GetProcesses())
        {
            using (processo)
            {
                if (TryLeggi(processo, io, out ProcessTimes lettura))
                {
                    trovati.Add(lettura);
                }
            }
        }

        processes = trovati;

        return true;
    }

    private static bool TryLeggi(Process processo, IProcessIoReader? io, out ProcessTimes lettura)
    {
        lettura = default;

        try
        {
            // L'I/O si legge per ultimo e non fa fallire la riga: un processo di cui si sa la
            // CPU ma non i byte trasferiti e' ancora un processo da mostrare.
            ulong? trasferiti = io is not null && io.TryRead(processo.Id, out ulong byteIo)
                ? byteIo
                : null;

            lettura = new ProcessTimes(
                processo.Id,
                processo.ProcessName,
                processo.TotalProcessorTime,
                ByteSize.FromBytes(processo.WorkingSet64),
                trasferiti);

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
