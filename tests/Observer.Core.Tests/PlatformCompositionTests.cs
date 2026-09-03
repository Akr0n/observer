using Observer.Core.Composition;
using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Memory;
using Observer.Core.Platform;
using Observer.Core.Platform.Linux;
using Observer.Core.Platform.Windows;

namespace Observer.Core.Tests;

/// <summary>
/// Selezione della piattaforma e radice di composizione. La piattaforma e' un PARAMETRO e
/// non una lettura dell'ambiente: cosi' il ramo Linux si prova dal runner Windows della CI,
/// ed e' testabile proprio il punto da cui nasce la degradazione.
/// </summary>
public class PlatformCompositionTests
{
    private const string ProcStat = "cpu  95 0 530 17966 170 0 119 0 0 0\ncpu0 12 0 209 4245 23 0 85 0 0 0\n";

    private const string ProcStatDopo = "cpu  595 0 530 18466 170 0 119 0 0 0\ncpu0 62 0 209 4295 23 0 85 0 0 0\n";

    private const string ProcMeminfo = """
        MemTotal:        1048576 kB
        MemFree:           10240 kB
        MemAvailable:     524288 kB
        SwapTotal:             0 kB
        SwapFree:              0 kB
        """;

    [Fact]
    public void Composizione_SuOgniPiattaforma_RegistraSempreCpuEMemoria()
    {
        // Una metrica non deve MAI sparire in base alla piattaforma: se sparisse, in
        // dashboard non si distinguerebbe "non misurabile qui" da "dimenticata".
        foreach (HostPlatform piattaforma in new[] { HostPlatform.Windows, HostPlatform.Linux, HostPlatform.Unknown })
        {
            IReadOnlyList<IMetricCollector> collectors =
                ObserverMetrics.CreateCollectors(piattaforma, new FakeFileTextReader());

            Assert.Contains(collectors, c => c.Id == "cpu");
            Assert.Contains(collectors, c => c.Id == "memory");
            Assert.All(collectors, c => Assert.NotEmpty(c.Descriptors));
        }
    }

    [Fact]
    public void IlLettoreDellIoSegueLaPiattaforma()
    {
        // Stessa regola dei collector: la piattaforma e' un parametro. E la scelta sbagliata
        // non farebbe eccezione - WindowsProcessIoReader fuori da Windows risponde false in
        // silenzio, e ogni tasso resterebbe ignoto senza che nessuno lo dica.
        FakeFileTextReader lettore = new();

        Assert.IsType<WindowsProcessIoReader>(ProcessIoReaders.Per(HostPlatform.Windows, lettore));
        Assert.IsType<LinuxProcessIoReader>(ProcessIoReaders.Per(HostPlatform.Linux, lettore));
        Assert.Null(ProcessIoReaders.Per(HostPlatform.Unknown, lettore));
    }

    [Fact]
    public async Task Linux_ConProcFinto_CalcolaUsoCpuAlSecondoCampione()
    {
        // Delta atteso: total da 18880 a 19880 (+1000), idle da 18136 a 18636 (+500).
        // Occupato = 500/1000 = 50%.
        FakeFileTextReader lettore = new();
        lettore.Set("/proc/stat", ProcStat);
        IReadOnlyList<IMetricCollector> collectors = ObserverMetrics.CreateCollectors(HostPlatform.Linux, lettore);
        IMetricCollector cpu = collectors.Single(c => c.Id == "cpu");

        MetricSnapshot primo = await cpu.CollectAsync(CancellationToken.None);
        lettore.Set("/proc/stat", ProcStatDopo);
        MetricSnapshot secondo = await cpu.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Warmup, primo.Status);
        Assert.Equal(CollectorStatus.Ok, secondo.Status);
        MetricPoint uso = Assert.Single(secondo.Points, p => p.MetricId == CpuCollector.TotalUsageMetricId);
        Assert.Equal(50.0, uso.Value!.Value.Number);
    }

    [Fact]
    public async Task Linux_ConMeminfoFinto_UsaAvailableEOmetteLoSwapAssente()
    {
        FakeFileTextReader lettore = new();
        lettore.Set("/proc/meminfo", ProcMeminfo);
        IReadOnlyList<IMetricCollector> collectors = ObserverMetrics.CreateCollectors(HostPlatform.Linux, lettore);
        IMetricCollector memoria = collectors.Single(c => c.Id == "memory");

        MetricSnapshot snapshot = await memoria.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Ok, snapshot.Status);
        MetricPoint usata = Assert.Single(snapshot.Points, p => p.MetricId == MemoryCollector.UsedPercentMetricId);
        Assert.Equal(50.0, usata.Value!.Value.Number);
        Assert.DoesNotContain(snapshot.Points, p => p.MetricId == MemoryCollector.SwapTotalMetricId);
    }

    [Fact]
    public async Task Linux_SenzaProcLeggibile_EUnavailableENonLancia()
    {
        // Su un /proc assente o non leggibile il servizio deve degradare, non morire.
        IReadOnlyList<IMetricCollector> collectors =
            ObserverMetrics.CreateCollectors(HostPlatform.Linux, new FakeFileTextReader());

        foreach (IMetricCollector collector in collectors)
        {
            MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

            Assert.Equal(CollectorStatus.Unavailable, snapshot.Status);
            Assert.Empty(snapshot.Points);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Message));
        }
    }

    [Fact]
    public async Task PiattaformaSconosciuta_DichiaraNonSupportatoConIlMotivo()
    {
        IReadOnlyList<IMetricCollector> collectors =
            ObserverMetrics.CreateCollectors(HostPlatform.Unknown, new FakeFileTextReader());

        foreach (IMetricCollector collector in collectors)
        {
            MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

            Assert.Equal(CollectorStatus.Unsupported, snapshot.Status);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Message));
        }
    }

    [Fact]
    public async Task OgniPuntoEmesso_HaSempreUnDescrittoreDichiarato()
    {
        // Vale per TUTTI i collector prodotti dalla composizione, non solo per uno: e' la
        // rete che tiene il legame chiave-descrittore, che il compilatore non verifica.
        FakeFileTextReader lettore = new();
        lettore.Set("/proc/stat", ProcStat);
        lettore.Set("/proc/meminfo", ProcMeminfo);
        IReadOnlyList<IMetricCollector> collectors = ObserverMetrics.CreateCollectors(HostPlatform.Linux, lettore);

        foreach (IMetricCollector collector in collectors)
        {
            await collector.CollectAsync(CancellationToken.None);
            MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

            HashSet<string> dichiarati = collector.Descriptors
                .Select(d => d.MetricId)
                .ToHashSet(StringComparer.Ordinal);

            Assert.All(snapshot.Points, p => Assert.Contains(p.MetricId, dichiarati));
        }
    }

    [Fact]
    public void IdDeiCollector_SonoUnivoci()
    {
        // Due collector con lo stesso id si sovrascriverebbero in silenzio sul filo e nel
        // database: e' un errore che va scoperto in CI, non guardando un grafico storto.
        IReadOnlyList<IMetricCollector> collectors =
            ObserverMetrics.CreateCollectors(HostPlatform.Linux, new FakeFileTextReader());

        Assert.Equal(collectors.Count, collectors.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
    }

    private sealed class FakeFileTextReader : IFileTextReader
    {
        private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);

        public void Set(string path, string content) => files[path] = content;

        public bool TryReadAllText(string path, out string content)
        {
            if (files.TryGetValue(path, out string? found))
            {
                content = found;
                return true;
            }

            content = string.Empty;
            return false;
        }
    }
}
