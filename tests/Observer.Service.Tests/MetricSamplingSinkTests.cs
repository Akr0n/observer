using Microsoft.Extensions.Logging.Abstractions;
using Observer.Core.Metrics;
using Observer.Service;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// Does the sampler deliver to history? Without this test everything else can be perfect and
/// the database stay empty for ever, with no error, no log line and no red test: the dashboard
/// would carry on working, showing only the present.
/// </summary>
public class MetricSamplingSinkTests
{
    [Fact]
    public async Task Sampler_DeliversTheSnapshotToHistoryAsWell()
    {
        MetricSnapshotCache cache = new();

        // The sink reads the cache AT THE MOMENT it receives, not afterwards. The sampler
        // publishes once a second: reading cache.Latest after the wait compared the first
        // snapshot delivered with whatever happened to be in the cache at that instant, and a
        // single round in between was enough to fail a test that had nothing wrong with it. It
        // really happened on the runner: four seconds instead of zero, and red.
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

            Assert.Equal("fake", delivered.Collectors[0].CollectorId);

            // The cache and history must receive the SAME object: if they diverged, the history
            // chart and the tile showing the present would show different numbers for the same
            // instant.
            Assert.Same(sink.CacheAtDelivery, delivered);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FakeCollector : IMetricCollector
    {
        public string Id => "fake";

        public IReadOnlyList<MetricDescriptor> Descriptors =>
            [new MetricDescriptor("fake.value", "Fake value", MetricUnit.None, IsPerInstance: false)];

        public ValueTask<MetricSnapshot> CollectAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MetricSnapshot(
                Id,
                CollectorStatus.Ok,
                null,
                [MetricPoint.Measured("fake.value", null, MetricValue.FromNumber(1d))]));
    }

    private sealed class RecordingSink(MetricSnapshotCache cache) : IMetricSnapshotSink
    {
        private readonly TaskCompletionSource<MachineSnapshot> first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MachineSnapshot> FirstSnapshot => first.Task;

        /// <summary>What was in the cache when the first delivery arrived.</summary>
        public MachineSnapshot? CacheAtDelivery { get; private set; }

        public void Enqueue(MachineSnapshot snapshot)
        {
            // The sampler publishes to the cache BEFORE delivering here: reading it now means
            // reading the same round, whatever the loop does next.
            CacheAtDelivery ??= cache.Latest;

            first.TrySetResult(snapshot);
        }
    }
}