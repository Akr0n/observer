using Observer.Core.Composition;
using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Memory;
using Observer.Core.Platform;
using Observer.Core.Platform.Linux;
using Observer.Core.Platform.Windows;

namespace Observer.Core.Tests;

/// <summary>
/// Platform selection and composition root. The platform is a PARAMETER, not something read
/// from the environment: that way the Linux branch is exercised from CI's Windows runner, and
/// the point where degradation begins is itself testable.
/// </summary>
public class PlatformCompositionTests
{
    private const string ProcStat = "cpu  95 0 530 17966 170 0 119 0 0 0\ncpu0 12 0 209 4245 23 0 85 0 0 0\n";

    private const string ProcStatAfter = "cpu  595 0 530 18466 170 0 119 0 0 0\ncpu0 62 0 209 4295 23 0 85 0 0 0\n";

    private const string ProcMeminfo = """
        MemTotal:        1048576 kB
        MemFree:           10240 kB
        MemAvailable:     524288 kB
        SwapTotal:             0 kB
        SwapFree:              0 kB
        """;

    [Fact]
    public void Composition_OnEveryPlatform_AlwaysRegistersCpuAndMemory()
    {
        // A metric must NEVER disappear depending on the platform: if it did, in the
        // dashboard "not measurable here" could not be told apart from "forgotten".
        foreach (HostPlatform platform in new[] { HostPlatform.Windows, HostPlatform.Linux, HostPlatform.Unknown })
        {
            IReadOnlyList<IMetricCollector> collectors =
                ObserverMetrics.CreateCollectors(platform, new FakeFileTextReader());

            Assert.Contains(collectors, c => c.Id == "cpu");
            Assert.Contains(collectors, c => c.Id == "memory");
            Assert.All(collectors, c => Assert.NotEmpty(c.Descriptors));
        }
    }

    [Fact]
    public void TheIoReaderFollowsThePlatform()
    {
        // Same rule as the collectors: the platform is a parameter. And the wrong choice
        // would not throw - WindowsProcessIoReader outside Windows returns false in silence,
        // and every rate would stay unknown without anyone saying so.
        FakeFileTextReader reader = new();

        Assert.IsType<WindowsProcessIoReader>(ProcessIoReaders.For(HostPlatform.Windows, reader));
        Assert.IsType<LinuxProcessIoReader>(ProcessIoReaders.For(HostPlatform.Linux, reader));
        Assert.Null(ProcessIoReaders.For(HostPlatform.Unknown, reader));
    }

    [Fact]
    public async Task Linux_WithFakeProc_ComputesCpuUsageOnTheSecondSample()
    {
        // Expected delta: total from 18880 to 19880 (+1000), idle from 18136 to 18636 (+500).
        // Busy = 500/1000 = 50%.
        FakeFileTextReader reader = new();
        reader.Set("/proc/stat", ProcStat);
        IReadOnlyList<IMetricCollector> collectors = ObserverMetrics.CreateCollectors(HostPlatform.Linux, reader);
        IMetricCollector cpu = collectors.Single(c => c.Id == "cpu");

        MetricSnapshot first = await cpu.CollectAsync(CancellationToken.None);
        reader.Set("/proc/stat", ProcStatAfter);
        MetricSnapshot second = await cpu.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Warmup, first.Status);
        Assert.Equal(CollectorStatus.Ok, second.Status);
        MetricPoint usage = Assert.Single(second.Points, p => p.MetricId == CpuCollector.TotalUsageMetricId);
        Assert.Equal(50.0, usage.Value!.Value.Number);
    }

    [Fact]
    public async Task Linux_WithFakeMeminfo_UsesAvailableAndOmitsAbsentSwap()
    {
        FakeFileTextReader reader = new();
        reader.Set("/proc/meminfo", ProcMeminfo);
        IReadOnlyList<IMetricCollector> collectors = ObserverMetrics.CreateCollectors(HostPlatform.Linux, reader);
        IMetricCollector memory = collectors.Single(c => c.Id == "memory");

        MetricSnapshot snapshot = await memory.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Ok, snapshot.Status);
        MetricPoint usedPercent = Assert.Single(snapshot.Points, p => p.MetricId == MemoryCollector.UsedPercentMetricId);
        Assert.Equal(50.0, usedPercent.Value!.Value.Number);
        Assert.DoesNotContain(snapshot.Points, p => p.MetricId == MemoryCollector.SwapTotalMetricId);
    }

    [Fact]
    public async Task Linux_WithoutReadableProc_IsUnavailableAndDoesNotThrow()
    {
        // On a /proc that is missing or unreadable the service must degrade, not die.
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
    public async Task UnknownPlatform_DeclaresUnsupportedWithAReason()
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
    public async Task EveryEmittedPoint_AlwaysHasADeclaredDescriptor()
    {
        // This holds for EVERY collector the composition produces, not just for one: it is
        // the safety net under the key-to-descriptor link, which the compiler does not check.
        FakeFileTextReader reader = new();
        reader.Set("/proc/stat", ProcStat);
        reader.Set("/proc/meminfo", ProcMeminfo);
        IReadOnlyList<IMetricCollector> collectors = ObserverMetrics.CreateCollectors(HostPlatform.Linux, reader);

        foreach (IMetricCollector collector in collectors)
        {
            await collector.CollectAsync(CancellationToken.None);
            MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

            HashSet<string> declared = collector.Descriptors
                .Select(d => d.MetricId)
                .ToHashSet(StringComparer.Ordinal);

            Assert.All(snapshot.Points, p => Assert.Contains(p.MetricId, declared));
        }
    }

    [Fact]
    public void CollectorIds_AreUnique()
    {
        // Two collectors with the same id would overwrite each other in silence on the wire
        // and in the database: an error that must be caught in CI, not by spotting a skewed chart.
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
