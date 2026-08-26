using Microsoft.Extensions.Logging.Abstractions;
using Observer.Core.Metrics;
using Observer.Service;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// Il campionatore consegna allo storico? Senza questo test tutto il resto puo' essere
/// perfetto e il database restare vuoto per sempre, senza un errore, senza un log e senza
/// nessun test rosso: la dashboard continuerebbe a funzionare mostrando solo il presente.
/// </summary>
public class MetricSamplingSinkTests
{
    [Fact]
    public async Task Campionatore_ConsegnaLoSnapshotAncheAlloStorico()
    {
        SinkRegistrante sink = new();
        MetricSnapshotCache cache = new();

        using MetricSamplingService campionatore = new(
            [new CollettoreFinto()],
            cache,
            sink,
            NullLogger<MetricSamplingService>.Instance);

        await campionatore.StartAsync(CancellationToken.None);

        try
        {
            MachineSnapshot consegnato = await sink.PrimoSnapshot.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("finto", consegnato.Collectors[0].CollectorId);

            // La cache e lo storico devono ricevere lo STESSO oggetto: se divergessero,
            // il grafico storico e la piastrella del presente mostrerebbero numeri diversi
            // per lo stesso istante.
            Assert.Same(cache.Latest, consegnato);
        }
        finally
        {
            await campionatore.StopAsync(CancellationToken.None);
        }
    }

    private sealed class CollettoreFinto : IMetricCollector
    {
        public string Id => "finto";

        public IReadOnlyList<MetricDescriptor> Descriptors =>
            [new MetricDescriptor("finto.valore", "Valore finto", MetricUnit.None, IsPerInstance: false)];

        public ValueTask<MetricSnapshot> CollectAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MetricSnapshot(
                Id,
                CollectorStatus.Ok,
                null,
                [MetricPoint.Measured("finto.valore", null, MetricValue.FromNumber(1d))]));
    }

    private sealed class SinkRegistrante : IMetricSnapshotSink
    {
        private readonly TaskCompletionSource<MachineSnapshot> primo =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MachineSnapshot> PrimoSnapshot => primo.Task;

        public void Enqueue(MachineSnapshot snapshot) => primo.TrySetResult(snapshot);
    }
}
