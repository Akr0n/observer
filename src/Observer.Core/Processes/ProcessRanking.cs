namespace Observer.Core.Processes;

/// <summary>
/// Chi sta consumando cosa, adesso.
/// </summary>
/// <remarks>
/// La memoria si legge e si mostra; la CPU e l'I/O no. Sono TASSI — tempo di processore, o
/// byte trasferiti, fratto tempo passato — quindi serve un campione precedente, e va tenuto
/// <b>per PID</b>.
/// <para>
/// Il PID si riusa, ed e' la trappola di questo tipo. Quando un processo muore il sistema puo'
/// assegnare lo stesso numero a uno nuovo, e i suoi contatori ripartono da zero: il confronto
/// col campione vecchio darebbe una differenza negativa, oppure — se il processo nuovo ha gia'
/// lavorato — un numero enorme attribuito a un programma che non c'e' piu'. Per questo insieme
/// ai contatori si ricorda il NOME, e un PID che cambia nome e' un processo nuovo, non lo
/// stesso che ha rallentato.
/// </para>
/// </remarks>
public sealed class ProcessRanking
{
    private readonly IProcessLister lister;
    private readonly TimeProvider orologio;
    private readonly int core;
    private readonly Dictionary<int, Precedente> precedenti = [];

    private long istantePrecedente;
    private bool haUnPrecedente;

    /// <summary>Crea la classifica sopra la porta indicata.</summary>
    /// <param name="lister">Da dove si legge l'elenco dei processi.</param>
    /// <param name="timeProvider">L'orologio, o null per quello di sistema.</param>
    /// <param name="processorCount">Quanti core ha la macchina, o null per chiederlo.</param>
    public ProcessRanking(IProcessLister lister, TimeProvider? timeProvider = null, int? processorCount = null)
    {
        ArgumentNullException.ThrowIfNull(lister);

        this.lister = lister;
        orologio = timeProvider ?? TimeProvider.System;
        core = Math.Max(1, processorCount ?? Environment.ProcessorCount);
    }

    /// <summary>Legge i processi e calcola quanto stanno consumando.</summary>
    /// <param name="processi">L'elenco, con CPU e I/O valorizzati dal secondo giro in poi.</param>
    /// <returns>False quando l'elenco non si e' potuto leggere affatto.</returns>
    public bool TryLeggi(out IReadOnlyList<ProcessUsage> processi)
    {
        if (!lister.TryList(out IReadOnlyList<ProcessTimes> letture))
        {
            // La storia si azzera: dopo un buco il delta sarebbe diviso per un intervallo di
            // durata sconosciuta, che e' il modo di inventare una percentuale credibile.
            precedenti.Clear();
            haUnPrecedente = false;
            processi = [];

            return false;
        }

        long adesso = orologio.GetTimestamp();
        TimeSpan trascorso = haUnPrecedente
            ? orologio.GetElapsedTime(istantePrecedente, adesso)
            : TimeSpan.Zero;

        List<ProcessUsage> usi = new(letture.Count);

        foreach (ProcessTimes lettura in letture)
        {
            usi.Add(new ProcessUsage(
                lettura.Pid,
                lettura.Name,
                Percentuale(lettura, trascorso),
                lettura.WorkingSet,
                TassoDiIo(lettura, trascorso)));
        }

        precedenti.Clear();

        foreach (ProcessTimes lettura in letture)
        {
            precedenti[lettura.Pid] = new Precedente(lettura.Name, lettura.Cpu, lettura.IoBytes);
        }

        istantePrecedente = adesso;
        haUnPrecedente = true;
        processi = usi;

        return true;
    }

    /// <summary>I processi che consumano piu' CPU, in ordine.</summary>
    /// <param name="tutti">L'elenco completo.</param>
    /// <param name="quanti">Quanti restituirne.</param>
    /// <returns>I primi, dal piu' affamato.</returns>
    /// <remarks>
    /// Chi non ha ancora una percentuale finisce in fondo, non a zero: sono processi di cui non
    /// si sa niente, e metterli fra quelli fermi sarebbe un'affermazione che non si puo' fare.
    /// </remarks>
    public static IReadOnlyList<ProcessUsage> PiuAffamatiDiCpu(
        IReadOnlyList<ProcessUsage> tutti, int quanti)
    {
        ArgumentNullException.ThrowIfNull(tutti);

        return
        [
            .. tutti
                .OrderByDescending(processo => processo.CpuPercent ?? -1d)
                .ThenBy(processo => processo.Name, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, quanti)),
        ];
    }

    /// <summary>I processi che occupano piu' memoria, in ordine.</summary>
    /// <param name="tutti">L'elenco completo.</param>
    /// <param name="quanti">Quanti restituirne.</param>
    /// <returns>I primi, dal piu' ingombrante.</returns>
    public static IReadOnlyList<ProcessUsage> PiuAffamatiDiMemoria(
        IReadOnlyList<ProcessUsage> tutti, int quanti)
    {
        ArgumentNullException.ThrowIfNull(tutti);

        return
        [
            .. tutti
                .OrderByDescending(processo => processo.WorkingSet.Bytes)
                .ThenBy(processo => processo.Name, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, quanti)),
        ];
    }

    /// <summary>I processi che trasferiscono piu' byte, in ordine.</summary>
    /// <param name="tutti">L'elenco completo.</param>
    /// <param name="quanti">Quanti restituirne.</param>
    /// <returns>I primi, dal piu' indaffarato.</returns>
    /// <remarks>Stessa regola della CPU: chi non ha ancora un tasso va in fondo, non a zero.</remarks>
    public static IReadOnlyList<ProcessUsage> PiuAffamatiDiIo(
        IReadOnlyList<ProcessUsage> tutti, int quanti)
    {
        ArgumentNullException.ThrowIfNull(tutti);

        return
        [
            .. tutti
                .OrderByDescending(processo => processo.IoBytesPerSecond ?? -1d)
                .ThenBy(processo => processo.Name, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, quanti)),
        ];
    }

    private double? Percentuale(ProcessTimes lettura, TimeSpan trascorso)
    {
        if (!HaUnPrecedente(lettura, trascorso, out Precedente prima) || lettura.Cpu < prima.Cpu)
        {
            return null;
        }

        double quota = (lettura.Cpu - prima.Cpu) / (trascorso * core);

        // Sull'INTERA macchina: 100 vuol dire tutti i core occupati, non uno solo. Il limite
        // superiore serve perche' i due orologi non sono lo stesso orologio, esattamente come
        // per l'occupazione dei dischi.
        return double.IsFinite(quota) ? Math.Clamp(quota, 0d, 1d) * 100d : null;
    }

    private double? TassoDiIo(ProcessTimes lettura, TimeSpan trascorso)
    {
        if (!HaUnPrecedente(lettura, trascorso, out Precedente prima)
            || lettura.IoBytes is not { } adesso
            || prima.Io is not { } primaIo
            || adesso < primaIo)
        {
            return null;
        }

        // Nessun limite superiore, a differenza della CPU: non c'e' un massimo fisico noto per i
        // byte trasferiti in un secondo, e un picco di lettura dalla cache e' un dato vero.
        double tasso = (adesso - primaIo) / trascorso.TotalSeconds;

        return double.IsFinite(tasso) ? tasso : null;
    }

    private bool HaUnPrecedente(ProcessTimes lettura, TimeSpan trascorso, out Precedente prima)
    {
        prima = default;

        // Stesso numero, altro programma: il PID e' stato riusato, e il confronto non si fa.
        return trascorso > TimeSpan.Zero
            && precedenti.TryGetValue(lettura.Pid, out prima)
            && string.Equals(prima.Nome, lettura.Name, StringComparison.Ordinal);
    }

    /// <summary>Cio' che si ricorda di un processo fra un giro e l'altro.</summary>
    private readonly record struct Precedente(string Nome, TimeSpan Cpu, ulong? Io);
}