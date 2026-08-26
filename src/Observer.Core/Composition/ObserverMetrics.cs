using Microsoft.Extensions.DependencyInjection;
using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Memory;
using Observer.Core.Platform;
using Observer.Core.Platform.Linux;
using Observer.Core.Platform.Windows;

namespace Observer.Core.Composition;

/// <summary>
/// Radice di composizione delle metriche. E' l'UNICO file da modificare per aggiungere una
/// sorgente nuova: i collector, le loro porte e il vocabolario delle metriche restano
/// intatti. E' questa proprieta' a sostenere il requisito "misurare qualsiasi parametro".
/// </summary>
public static class ObserverMetrics
{
    /// <summary>
    /// Costruisce i collector per la piattaforma indicata. La piattaforma e' un parametro
    /// e non una lettura dell'ambiente, cosi' entrambi i rami sono provabili da un runner solo.
    /// </summary>
    /// <remarks>
    /// Ogni collector viene creato SEMPRE, su ogni piattaforma: cambia solo la porta che ci
    /// sta sotto. Una metrica non misurabile qui resta nel catalogo e si dichiara
    /// Unsupported con il motivo, invece di sparire — perche' una metrica sparita e' una
    /// metrica dimenticata, e le due cose non vanno confuse in dashboard.
    /// </remarks>
    public static IReadOnlyList<IMetricCollector> CreateCollectors(HostPlatform platform, IFileTextReader fileReader)
    {
        ArgumentNullException.ThrowIfNull(fileReader);

        (ICpuTimesProvider cpu, IMemoryReadingProvider memory) = platform switch
        {
            HostPlatform.Linux => (
                new LinuxCpuTimesProvider(fileReader) as ICpuTimesProvider,
                new LinuxMemoryReadingProvider(fileReader) as IMemoryReadingProvider),

            HostPlatform.Windows => (
                new WindowsCpuTimesProvider(),
                new WindowsMemoryReadingProvider() as IMemoryReadingProvider),

            _ => (
                new UnsupportedCpuTimesProvider("piattaforma non riconosciuta: sono supportate Windows e Linux"),
                new UnsupportedMemoryReadingProvider("piattaforma non riconosciuta: sono supportate Windows e Linux")),
        };

        return [new CpuCollector(cpu), new MemoryCollector(memory)];
    }

    /// <summary>
    /// Registra i collector della piattaforma corrente. Da usare da Observer.Service e da
    /// Observer.App, cosi' entrambi vedono lo stesso insieme di metriche.
    /// </summary>
    public static IServiceCollection AddObserverMetrics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IFileTextReader, FileTextReader>();

        // Singleton e non transient: il collector CPU conserva il campione precedente, e
        // ricrearlo a ogni raccolta lo terrebbe per sempre in Warmup senza mai un valore.
        services.AddSingleton<IReadOnlyList<IMetricCollector>>(sp =>
            CreateCollectors(HostPlatformDetector.Current, sp.GetRequiredService<IFileTextReader>()));

        return services;
    }
}
