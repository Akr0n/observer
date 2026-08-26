using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Memory;
using Observer.Core.Units;

namespace Observer.Core.Tests;

/// <summary>
/// Comportamento dei collector con porte finte: nessun accesso a /proc, al registro o alle
/// API di sistema. Verificano la degradazione graziosa, che e' la proprieta' su cui poggia
/// il requisito "misurare qualsiasi parametro": una sorgente che non c'e' o che esplode
/// deve degradare una piastrella, non abbattere il servizio.
/// </summary>
public class CollectorBehaviourTests
{
    [Fact]
    public async Task Cpu_PrimoCampione_EWarmupENonZeroPerCento()
    {
        // Senza uno stato "in avvio" il primo giro pubblicherebbe uno 0% inventato, che a
        // grafico sembra una macchina ferma: un numero falso e perfettamente plausibile.
        // L'assenza del dato dev'essere DICHIARATA, non silenziosa.
        ScriptedCpuProvider provider = new(
            new CpuTimes(Idle: 1000L, Total: 2000L),
            new CpuTimes(Idle: 1500L, Total: 3000L));
        CpuCollector collector = new(provider);

        MetricSnapshot primo = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Warmup, primo.Status);
        Assert.Empty(primo.Points);
        Assert.False(string.IsNullOrWhiteSpace(primo.Message));
    }

    [Fact]
    public async Task Cpu_SecondoCampione_PubblicaLaPercentuale()
    {
        ScriptedCpuProvider provider = new(
            new CpuTimes(Idle: 1000L, Total: 2000L),
            new CpuTimes(Idle: 1500L, Total: 3000L));
        CpuCollector collector = new(provider);

        await collector.CollectAsync(CancellationToken.None);
        MetricSnapshot secondo = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Ok, secondo.Status);
        MetricPoint punto = Assert.Single(secondo.Points, p => p.MetricId == CpuCollector.TotalUsageMetricId);
        Assert.Equal(50.0, punto.Value!.Value.Number);
    }

    [Fact]
    public async Task Cpu_LetturaFallita_EUnavailableENonWarmup()
    {
        // "Non ho ancora due letture" e "non riesco a leggere" sono due cose diverse e
        // vanno mostrate diversamente. Confonderle nasconde un guasto dietro un'attesa.
        ScriptedCpuProvider provider = new();
        CpuCollector collector = new(provider);

        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Unavailable, snapshot.Status);
        Assert.Empty(snapshot.Points);
    }

    [Fact]
    public async Task Cpu_ProviderNonSupportato_RestaNelCatalogoEDichiaraIlMotivo()
    {
        // Differenza fra "non si puo' misurare qui" e "me la sono dimenticata". La metrica
        // deve comparire in dashboard CON la spiegazione, non sparire.
        UnsupportedCpuProvider provider = new("i contatori per-core richiedono ntdll");
        CpuCollector collector = new(provider);

        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Unsupported, snapshot.Status);
        Assert.Contains("ntdll", snapshot.Message, StringComparison.Ordinal);
        Assert.NotEmpty(collector.Descriptors);
    }

    [Fact]
    public async Task Memoria_SenzaSwap_NonEmettePuntiSwap()
    {
        // Una macchina senza swap e' una configurazione legittima, non un guasto. Emettere
        // zeri sarebbe fuorviante: l'assenza del punto e' la convenzione per "non applicabile".
        FakeMemoryProvider provider = new(new MemoryReading(
            Total: ByteSize.FromKibibytes(1048576L),
            Available: ByteSize.FromKibibytes(524288L),
            SwapTotal: ByteSize.FromBytes(0L),
            SwapFree: ByteSize.FromBytes(0L),
            AvailableWasEstimated: false));
        MemoryCollector collector = new(provider);

        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Ok, snapshot.Status);
        Assert.Contains(snapshot.Points, p => p.MetricId == MemoryCollector.UsedPercentMetricId);
        Assert.DoesNotContain(snapshot.Points, p => p.MetricId == MemoryCollector.SwapTotalMetricId);
    }

    [Fact]
    public async Task Memoria_UsaAvailableNonFree_QuindiRiporta50NonNovantanove()
    {
        FakeMemoryProvider provider = new(new MemoryReading(
            Total: ByteSize.FromKibibytes(1048576L),
            Available: ByteSize.FromKibibytes(524288L),
            SwapTotal: ByteSize.FromKibibytes(2097152L),
            SwapFree: ByteSize.FromKibibytes(2097152L),
            AvailableWasEstimated: false));
        MemoryCollector collector = new(provider);

        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

        MetricPoint usata = Assert.Single(snapshot.Points, p => p.MetricId == MemoryCollector.UsedPercentMetricId);
        Assert.Equal(50.0, usata.Value!.Value.Number);
        Assert.Contains(snapshot.Points, p => p.MetricId == MemoryCollector.SwapTotalMetricId);
    }

    [Fact]
    public async Task OgniPuntoEmesso_HaUnDescrittoreDichiarato()
    {
        // Il legame chiave-collector non e' verificato dal compilatore: se un collector
        // emette un punto di cui non pubblica il descrittore, la UI non sa che unita' usare
        // ne' come etichettarlo, e lo disegna sbagliato o lo scarta. Questo test sposta
        // l'errore in CI invece che in dashboard.
        FakeMemoryProvider provider = new(new MemoryReading(
            Total: ByteSize.FromKibibytes(1048576L),
            Available: ByteSize.FromKibibytes(524288L),
            SwapTotal: ByteSize.FromKibibytes(2097152L),
            SwapFree: ByteSize.FromKibibytes(1048576L),
            AvailableWasEstimated: false));
        MemoryCollector collector = new(provider);

        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

        HashSet<string> dichiarati = collector.Descriptors.Select(d => d.MetricId).ToHashSet(StringComparer.Ordinal);
        Assert.All(snapshot.Points, p => Assert.Contains(p.MetricId, dichiarati));
    }

    [Fact]
    public void MetricUnit_EUnTipoAperto_QuindiUnaUnitaNuovaNonRichiedeDiToccareIlCore()
    {
        // Se le unita' fossero un enum chiuso, il primo sensore in rpm o in volt
        // costringerebbe a modificare Observer.Core. Il requisito "qualsiasi parametro"
        // richiede che questo resti possibile senza toccare nulla.
        MetricUnit giriAlMinuto = new("rpm");

        Assert.Equal("rpm", giriAlMinuto.Symbol);
    }

    [Fact]
    public void CollectorStatus_ValoreZero_EUnknownENonOk()
    {
        // Uno zero che significasse "Ok" farebbe passare per riuscita una raccolta mai
        // avvenuta: default(CollectorStatus) non deve spacciarsi per successo.
        Assert.Equal(CollectorStatus.Unknown, default(CollectorStatus));
    }

    // ---- porte finte -------------------------------------------------------------

    /// <summary>Restituisce a turno i campioni preimpostati; esaurita la lista, fallisce.</summary>
    private sealed class ScriptedCpuProvider(params CpuTimes[] samples) : ICpuTimesProvider
    {
        private int index;

        public bool IsSupported => true;

        public string? UnsupportedReason => null;

        public bool TryRead(out CpuTimes times)
        {
            if (index >= samples.Length)
            {
                times = default;
                return false;
            }

            times = samples[index];
            index++;
            return true;
        }
    }

    private sealed class UnsupportedCpuProvider(string reason) : ICpuTimesProvider
    {
        public bool IsSupported => false;

        public string? UnsupportedReason => reason;

        public bool TryRead(out CpuTimes times)
        {
            times = default;
            return false;
        }
    }

    private sealed class FakeMemoryProvider(MemoryReading reading) : IMemoryReadingProvider
    {
        public bool IsSupported => true;

        public string? UnsupportedReason => null;

        public bool TryRead(out MemoryReading value)
        {
            value = reading;
            return true;
        }
    }
}
