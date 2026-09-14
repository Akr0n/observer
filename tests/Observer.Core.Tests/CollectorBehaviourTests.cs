using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Memory;
using Observer.Core.Units;

namespace Observer.Core.Tests;

/// <summary>
/// Collector behaviour against fake ports: no access to /proc, to the registry or to the
/// system APIs. These tests check graceful degradation, which is the property the requirement
/// "measure any parameter" rests on: a source that is not there, or that blows up, must
/// degrade one gauge, not bring the service down.
/// </summary>
public class CollectorBehaviourTests
{
    [Fact]
    public async Task Cpu_FirstSample_IsWarmupAndNotZeroPercent()
    {
        // Without a "warming up" state the first pass would publish a made-up 0%, which on a
        // chart looks like an idle machine: a number that is false and perfectly plausible.
        // A missing reading must be DECLARED, not silent.
        ScriptedCpuProvider provider = new(
            new CpuTimes(Idle: 1000L, Total: 2000L),
            new CpuTimes(Idle: 1500L, Total: 3000L));
        CpuCollector collector = new(provider);

        MetricSnapshot first = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Warmup, first.Status);
        Assert.Empty(first.Points);
        Assert.False(string.IsNullOrWhiteSpace(first.Message));
    }

    [Fact]
    public async Task Cpu_SecondSample_PublishesThePercentage()
    {
        ScriptedCpuProvider provider = new(
            new CpuTimes(Idle: 1000L, Total: 2000L),
            new CpuTimes(Idle: 1500L, Total: 3000L));
        CpuCollector collector = new(provider);

        await collector.CollectAsync(CancellationToken.None);
        MetricSnapshot second = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Ok, second.Status);
        MetricPoint point = Assert.Single(second.Points, p => p.MetricId == CpuCollector.TotalUsageMetricId);
        Assert.Equal(50.0, point.Value!.Value.Number);
    }

    [Fact]
    public async Task Cpu_ReadFailed_IsUnavailableAndNotWarmup()
    {
        // "I do not have two readings yet" and "I cannot read" are two different things and
        // must be shown differently. Confusing them hides a fault behind a wait.
        ScriptedCpuProvider provider = new();
        CpuCollector collector = new(provider);

        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Unavailable, snapshot.Status);
        Assert.Empty(snapshot.Points);
    }

    [Fact]
    public async Task Cpu_UnsupportedProvider_StaysInTheCatalogAndStatesTheReason()
    {
        // The difference between "it cannot be measured here" and "I forgot about it". The
        // metric must show up in the dashboard WITH the explanation, not disappear.
        UnsupportedCpuProvider provider = new("per-core counters need ntdll");
        CpuCollector collector = new(provider);

        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Unsupported, snapshot.Status);
        Assert.Contains("ntdll", snapshot.Message, StringComparison.Ordinal);
        Assert.NotEmpty(collector.Descriptors);
    }

    [Fact]
    public async Task Memory_NoSwap_EmitsNoSwapPoints()
    {
        // A machine with no swap is a legitimate configuration, not a fault. Emitting zeros
        // would be misleading: the absence of the point is the convention for "not applicable".
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
    public async Task Memory_UsesAvailableNotFree_SoItReports50Not99()
    {
        FakeMemoryProvider provider = new(new MemoryReading(
            Total: ByteSize.FromKibibytes(1048576L),
            Available: ByteSize.FromKibibytes(524288L),
            SwapTotal: ByteSize.FromKibibytes(2097152L),
            SwapFree: ByteSize.FromKibibytes(2097152L),
            AvailableWasEstimated: false));
        MemoryCollector collector = new(provider);

        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

        MetricPoint usedPercent = Assert.Single(snapshot.Points, p => p.MetricId == MemoryCollector.UsedPercentMetricId);
        Assert.Equal(50.0, usedPercent.Value!.Value.Number);
        Assert.Contains(snapshot.Points, p => p.MetricId == MemoryCollector.SwapTotalMetricId);
    }

    [Fact]
    public async Task EveryEmittedPoint_HasADeclaredDescriptor()
    {
        // The key-to-collector link is not checked by the compiler: if a collector emits a
        // point whose descriptor it does not publish, the UI does not know which unit to use
        // nor how to label it, and either draws it wrong or discards it. This test moves the
        // error into CI instead of into the dashboard.
        FakeMemoryProvider provider = new(new MemoryReading(
            Total: ByteSize.FromKibibytes(1048576L),
            Available: ByteSize.FromKibibytes(524288L),
            SwapTotal: ByteSize.FromKibibytes(2097152L),
            SwapFree: ByteSize.FromKibibytes(1048576L),
            AvailableWasEstimated: false));
        MemoryCollector collector = new(provider);

        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

        HashSet<string> declared = collector.Descriptors.Select(d => d.MetricId).ToHashSet(StringComparer.Ordinal);
        Assert.All(snapshot.Points, p => Assert.Contains(p.MetricId, declared));
    }

    [Fact]
    public void MetricUnit_IsAnOpenType_SoANewUnitNeedsNoChangeToCore()
    {
        // If the units were a closed enum, the first sensor measured in rpm or volts would force a
        // change to Observer.Core. The requirement "any parameter" needs this to stay
        // possible without touching anything.
        MetricUnit rpm = new("rpm");

        Assert.Equal("rpm", rpm.Symbol);
    }

    [Fact]
    public void CollectorStatus_ZeroValue_IsUnknownAndNotOk()
    {
        // A zero that meant "Ok" would report a collection that never happened as a success:
        // default(CollectorStatus) must not pass itself off as one.
        Assert.Equal(CollectorStatus.Unknown, default(CollectorStatus));
    }

    // ---- fake ports --------------------------------------------------------------

    /// <summary>Returns the preset samples in turn; once the list is exhausted, it fails.</summary>
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
