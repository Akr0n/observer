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
    public async Task Sampler_DeliversTheSnapshotToHistoryAsWell()
    {
        MetricSnapshotCache cache = new();

        // Il sink legge la cache NEL MOMENTO in cui riceve, e non dopo. Il campionatore
        // pubblica ogni secondo: leggendo cache.Latest a valle dell'attesa si confrontava il
        // primo snapshot consegnato con quello che per caso era in cache in quell'istante, e
        // bastava un giro in mezzo per far fallire un test che non aveva niente che non
        // andasse. Succedeva davvero sul runner: quattro secondi invece di zero, e rosso.
        RecordingSink sink = new(cache);

        using MetricSamplingService sampler = new(
            [new FakeCollector()],
            cache,
            sink,
            NullLogger<MetricSamplingService>.Instance);

        await sampler.StartAsync(CancellationToken.None);

        try
        {
            MachineSnapshot delivered = await sink.FirstSnapshot.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("finto", delivered.Collectors[0].CollectorId);

            // La cache e lo storico devono ricevere lo STESSO oggetto: se divergessero, il
            // grafico storico e la piastrella del presente mostrerebbero numeri diversi per lo
            // stesso istante.
            Assert.Same(sink.CacheAtDelivery, delivered);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FakeCollector : IMetricCollector
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

    private sealed class RecordingSink(MetricSnapshotCache cache) : IMetricSnapshotSink
    {
        private readonly TaskCompletionSource<MachineSnapshot> first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MachineSnapshot> FirstSnapshot => first.Task;

        /// <summary>Cosa c'era in cache quando e' arrivata la prima consegna.</summary>
        public MachineSnapshot? CacheAtDelivery { get; private set; }

        public void Enqueue(MachineSnapshot snapshot)
        {
            // Il campionatore pubblica in cache PRIMA di consegnare qui: leggerla adesso
            // significa leggere lo stesso giro, qualunque cosa faccia il ciclo dopo.
            CacheAtDelivery ??= cache.Latest;

            first.TrySetResult(snapshot);
        }
    }
}