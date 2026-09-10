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
    private readonly Lock serratura = new();

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
    /// <remarks>
    /// <b>Sotto serratura</b>, e non per prudenza generica. Questo oggetto e' UNO SOLO per
    /// processo — registrato singleton — e l'endpoint <c>/processes</c> lo chiama dentro la
    /// richiesta HTTP, senza niente in mezzo. La finestra lo interroga una volta al secondo
    /// finche' il pannello dei processi resta aperto, quindi bastano due dashboard sulla
    /// stessa macchina perche' due letture partano insieme. E ogni lettura SVUOTA e riscrive
    /// <see cref="precedenti"/>: due scritture contemporanee su un
    /// <see cref="Dictionary{TKey, TValue}"/> non lanciano in modo affidabile, e nel caso
    /// peggiore avvitano un thread dentro Insert — un core al 100% per sempre, la richiesta
    /// che non torna, e nessun errore da nessuna parte. E' lo stesso pericolo da cui
    /// <c>MetricSnapshotCache</c> difende i collector — due letture della stessa sorgente che
    /// si sovrappongono — sull'unico percorso che non l'aveva; li' pero' bastava una scrittura
    /// atomica, qui no, perche' qui lo stato non e' un riferimento solo ma un dizionario
    /// svuotato e riempito.
    /// <para>
    /// Il prezzo, misurato su questa macchina con circa 200 processi: la sezione critica e'
    /// l'INTERA lettura del sistema, da 14 a 66 ms, e con otto chiamanti insieme l'ultimo ha
    /// aspettato fra 300 e 650 ms. Il servizio non impone una scadenza alle richieste; il
    /// tetto e' il <c>RequestTimeout</c> di 8 s del client, cioe' due ordini di grandezza piu'
    /// in la'. Per questo non c'e' un tentativo con scadenza: sarebbe complessita' su un
    /// numero che non si avvicina. Il prezzo vero e' un altro, ed e' dichiarato: una lettura
    /// che si piantasse adesso fermerebbe <c>/processes</c> per tutti, non solo per chi l'ha
    /// chiesta.
    /// </para>
    /// </remarks>
    public bool TryLeggi(out IReadOnlyList<ProcessUsage> processi)
    {
        lock (serratura)
        {
            return Leggi(out processi);
        }
    }

    private bool Leggi(out IReadOnlyList<ProcessUsage> processi)
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