using Observer.Core.Units;

namespace Observer.Core.Metrics.Disk;

/// <summary>
/// Quanto stanno lavorando i dischi: byte letti e scritti al secondo, e percentuale di tempo
/// occupato.
/// </summary>
/// <remarks>
/// E' il primo collector che misura una VELOCITA', e questo cambia tre cose rispetto a tutti
/// gli altri.
/// <para>
/// La prima: serve un orologio. La CPU calcola una percentuale come rapporto fra due delta
/// della stessa grandezza, e nel rapporto l'unita' si semplifica — nessun collector, finora,
/// ha mai avuto bisogno di sapere quanto tempo fosse passato. I byte al secondo si', e
/// l'orologio arriva da fuori (<see cref="TimeProvider"/>) perche' un test non deve aspettare
/// un secondo vero per provare una divisione.
/// </para>
/// <para>
/// La seconda: lo stato e' PER ISTANZA. I dischi compaiono e spariscono mentre il programma
/// gira — una chiavetta, un disco di rete — e un dispositivo appena comparso non ha un
/// campione precedente. Se ne rubasse uno altrui, o se il suo contatore assoluto venisse
/// diviso per un secondo, comparirebbe a schermo con un numero enorme e plausibile.
/// </para>
/// <para>
/// La terza: le istanze sono DISPOSITIVI, non volumi, e quindi non coincidono con quelle di
/// <see cref="DiskCollector"/>. Un disco puo' portare piu' volumi e un volume puo' stare su
/// piu' dischi: la corrispondenza non e' uno a uno, e fingere che lo sia per far combaciare
/// due elenchi a schermo significherebbe attribuire a <c>C:</c> traffico che non e' suo.
/// </para>
/// </remarks>
public sealed class DiskActivityCollector : IMetricCollector
{
    /// <summary>Byte letti al secondo.</summary>
    public const string ReadBytesPerSecondMetricId = "disk.read.bytespersecond";

    /// <summary>Byte scritti al secondo.</summary>
    public const string WriteBytesPerSecondMetricId = "disk.write.bytespersecond";

    /// <summary>Percentuale di tempo in cui il dispositivo aveva richieste in corso.</summary>
    public const string BusyPercentMetricId = "disk.busy.percent";

    // MetricUnit e' un tipo aperto e non un enum, apposta: una unita' nuova non tocca Core.
    private static readonly MetricUnit ByteAlSecondo = new("B/s");

    private static readonly IReadOnlyList<MetricDescriptor> DescriptorList =
    [
        new(ReadBytesPerSecondMetricId, "Disk read", ByteAlSecondo, IsPerInstance: true),
        new(WriteBytesPerSecondMetricId, "Disk write", ByteAlSecondo, IsPerInstance: true),
        new(BusyPercentMetricId, "Disk activity", MetricUnit.Percent, IsPerInstance: true),
    ];

    private readonly IDiskActivityProvider provider;
    private readonly TimeProvider orologio;

    private readonly Dictionary<string, DiskActivityReading> precedenti =
        new(StringComparer.Ordinal);

    private long istantePrecedente;
    private bool haUnPrecedente;

    /// <summary>Crea il collector sopra la porta indicata.</summary>
    /// <param name="provider">Da dove si leggono i contatori.</param>
    /// <param name="timeProvider">L'orologio, o null per quello di sistema.</param>
    public DiskActivityCollector(IDiskActivityProvider provider, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(provider);

        this.provider = provider;
        orologio = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Id => "disk.activity";

    /// <inheritdoc />
    public IReadOnlyList<MetricDescriptor> Descriptors => DescriptorList;

    /// <inheritdoc />
    public ValueTask<MetricSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(Collect());
    }

    private MetricSnapshot Collect()
    {
        if (!provider.IsSupported)
        {
            return Degradato(
                CollectorStatus.Unsupported,
                provider.UnsupportedReason ?? "source not supported on this platform");
        }

        if (!provider.TryRead(out IReadOnlyList<DiskActivityReading> letture))
        {
            // La storia si azzera: riprendendo dopo il buco, la differenza sarebbe divisa per
            // un intervallo di cui non si conosce la durata. Un giro di riscaldamento in piu'
            // costa un secondo; una media su un tempo sconosciuto non si riconosce piu'.
            Dimentica();

            return Degradato(CollectorStatus.Unavailable, "the disk activity counters could not be read");
        }

        long adesso = orologio.GetTimestamp();

        if (!haUnPrecedente)
        {
            Ricorda(letture, adesso);

            return Degradato(CollectorStatus.Warmup, SampleFailureText.Describe(SampleFailure.FirstSample));
        }

        TimeSpan trascorso = orologio.GetElapsedTime(istantePrecedente, adesso);
        List<MetricPoint> punti = new(letture.Count * 3);

        foreach (DiskActivityReading lettura in letture)
        {
            if (precedenti.TryGetValue(lettura.Instance, out DiskActivityReading prima))
            {
                Misura(punti, prima, lettura, trascorso);
            }
            else
            {
                // Dispositivo comparso ora: esiste, e va detto che esiste, ma non ha ancora
                // un valore. Tacerlo lo farebbe sembrare assente; pubblicarne uno lo farebbe
                // sembrare misurato.
                Mancante(punti, lettura.Instance, SampleFailure.FirstSample);
            }
        }

        // Solo i dispositivi visti ADESSO restano in memoria: uno staccato sparisce dai punti
        // invece di mostrare per sempre il suo ultimo numero.
        Ricorda(letture, adesso);

        return letture.Count == 0
            ? new MetricSnapshot(Id, CollectorStatus.Ok, "no disk device to report on this machine", [])
            : new MetricSnapshot(Id, CollectorStatus.Ok, null, punti);
    }

    private static void Misura(
        List<MetricPoint> punti,
        DiskActivityReading prima,
        DiskActivityReading adesso,
        TimeSpan trascorso)
    {
        punti.Add(Tasso(
            ReadBytesPerSecondMetricId, adesso.Instance, prima.BytesRead, adesso.BytesRead, trascorso));

        punti.Add(Tasso(
            WriteBytesPerSecondMetricId,
            adesso.Instance,
            prima.BytesWritten,
            adesso.BytesWritten,
            trascorso));

        punti.Add(
            DiskActivityRates.TryComputeBusy(
                prima, adesso, trascorso, out Percent occupato, out SampleFailure guasto)
                ? MetricPoint.Measured(
                    BusyPercentMetricId, adesso.Instance, MetricValue.FromNumber(occupato.Points))
                : MetricPoint.Unavailable(
                    BusyPercentMetricId, adesso.Instance, SampleFailureText.Describe(guasto)));
    }

    private static MetricPoint Tasso(
        string metrica,
        string istanza,
        ulong prima,
        ulong adesso,
        TimeSpan trascorso) =>
        DiskActivityRates.TryComputeBytesPerSecond(
            prima, adesso, trascorso, out double tasso, out SampleFailure guasto)
            ? MetricPoint.Measured(metrica, istanza, MetricValue.FromNumber(tasso))
            : MetricPoint.Unavailable(metrica, istanza, SampleFailureText.Describe(guasto));

    private static void Mancante(List<MetricPoint> punti, string istanza, SampleFailure guasto)
    {
        string motivo = SampleFailureText.Describe(guasto);

        punti.Add(MetricPoint.Unavailable(ReadBytesPerSecondMetricId, istanza, motivo));
        punti.Add(MetricPoint.Unavailable(WriteBytesPerSecondMetricId, istanza, motivo));
        punti.Add(MetricPoint.Unavailable(BusyPercentMetricId, istanza, motivo));
    }

    private void Ricorda(IReadOnlyList<DiskActivityReading> letture, long adesso)
    {
        precedenti.Clear();

        foreach (DiskActivityReading lettura in letture)
        {
            precedenti[lettura.Instance] = lettura;
        }

        istantePrecedente = adesso;
        haUnPrecedente = true;
    }

    private void Dimentica()
    {
        precedenti.Clear();
        haUnPrecedente = false;
    }

    private MetricSnapshot Degradato(CollectorStatus stato, string motivo) =>
        new(Id, stato, motivo, []);
}