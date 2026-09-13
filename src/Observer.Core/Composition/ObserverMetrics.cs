using Microsoft.Extensions.DependencyInjection;
using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Disk;
using Observer.Core.Metrics.Memory;
using Observer.Core.Platform;
using Observer.Core.Platform.Linux;
using Observer.Core.Platform.Windows;

namespace Observer.Core.Composition;

/// <summary>
/// Metric composition root. This is the ONLY file to change to add a new source: the
/// collectors, their ports and the metric vocabulary stay untouched. It is this property
/// that supports the "measure any parameter" requirement.
/// </summary>
public static class ObserverMetrics
{
    /// <summary>
    /// Builds the collectors for the given platform. The platform is a parameter and not a
    /// reading of the environment, so both branches are testable from a single runner.
    /// </summary>
    /// <remarks>
    /// Every collector is created ALWAYS, on every platform: only the port underneath
    /// changes. A metric that cannot be measured here stays in the catalog and declares
    /// itself Unsupported with the reason, instead of disappearing — because a metric that
    /// disappeared is a metric that was forgotten, and the two must not be confused in a
    /// dashboard.
    /// </remarks>
    public static IReadOnlyList<IMetricCollector> CreateCollectors(HostPlatform platform, IFileTextReader fileReader)
    {
        ArgumentNullException.ThrowIfNull(fileReader);

        const string unrecognized = "unrecognized platform: only Windows and Linux are supported";

        (ICpuTimesProvider cpu,
            IMemoryReadingProvider memory,
            IDiskReadingProvider disk,
            IDiskActivityProvider diskActivity) = platform switch
        {
            HostPlatform.Linux => (
                new LinuxCpuTimesProvider(fileReader) as ICpuTimesProvider,
                new LinuxMemoryReadingProvider(fileReader) as IMemoryReadingProvider,
                new LinuxDiskReadingProvider(fileReader) as IDiskReadingProvider,
                new LinuxDiskActivityProvider(fileReader) as IDiskActivityProvider),

            HostPlatform.Windows => (
                new WindowsCpuTimesProvider(),
                new WindowsMemoryReadingProvider() as IMemoryReadingProvider,
                new WindowsDiskReadingProvider() as IDiskReadingProvider,
                new WindowsDiskActivityProvider() as IDiskActivityProvider),

            _ => (
                new UnsupportedCpuTimesProvider(unrecognized),
                new UnsupportedMemoryReadingProvider(unrecognized),
                new UnsupportedDiskReadingProvider(unrecognized),
                new UnsupportedDiskActivityProvider(unrecognized)),
        };

        // The order is the one in which the panels appear on screen: disk space before
        // activity, because that is the question asked most often.
        return
        [
            new CpuCollector(cpu),
            new MemoryCollector(memory),
            new DiskCollector(disk),
            new DiskActivityCollector(diskActivity),
        ];
    }

    /// <summary>
    /// Registers the collectors for the current platform. To be used by Observer.Service and
    /// by Observer.App, so both see the same set of metrics.
    /// </summary>
    public static IServiceCollection AddObserverMetrics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IFileTextReader, FileTextReader>();

        // Singleton and not transient: the CPU collector keeps the previous sample, and
        // recreating it at every collection would keep it in Warmup for ever, never a value.
        services.AddSingleton<IReadOnlyList<IMetricCollector>>(sp =>
            CreateCollectors(HostPlatformDetector.Current, sp.GetRequiredService<IFileTextReader>()));

        return services;
    }
}