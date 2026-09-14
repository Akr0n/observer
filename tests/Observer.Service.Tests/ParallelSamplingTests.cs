using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Observer.Core.Metrics;
using Observer.Service;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// That the sources are polled TOGETHER, not one after another.
/// </summary>
/// <remarks>
/// In sequence a round takes the sum of the times, and the worst case is the number of collectors
/// times the timeout of each: with two sources it already runs past the one-second sampling
/// interval, with five it quadruples it. The fault that follows makes no noise —
/// <c>PeriodicTimer</c> drops ticks silently, the samples disappear, and the history strip
/// declares "not measured" a period in which the machine was up and healthy. No test would fail:
/// that is why one is needed that watches the TIME.
/// </remarks>
public class ParallelSamplingTests
{
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task AllSourcesAreInFlightAtTheSameTime()
    {
        // This COUNTS how many collections are in flight at the same moment, instead of timing
        // the round. An absolute time proves nothing here: on a loaded runner 1320 ms is
        // consistent both with three collections in sequence and with three collections together
        // plus the service start-up, and in fact the first draft of this test failed, accusing
        // the code of something it could not prove. The number of simultaneous collections, on
        // the other hand, is either three or one, and it does not depend on how fast the
        // machine runs.
        OverlapCounter counter = new();
        RecordingSink sink = new();
        MetricSnapshotCache cache = new();

        using MetricSamplingService sampler = new(
            [
                new SlowCollector("one", counter: counter),
                new SlowCollector("two", counter: counter),
                new SlowCollector("three", counter: counter),
            ],
            cache,
            sink,
            NullLogger<MetricSamplingService>.Instance);

        await sampler.StartAsync(CancellationToken.None);

        try
        {
            await sink.FirstSnapshot.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(3, counter.Peak);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TheCollectorOrderDoesNotChangeFromOneRoundToTheNext()
    {
        // Polling together must not mean delivering in arrival order: the tiles on screen would
        // swap places every second, and nothing would flag it except the eye of whoever is
        // watching.
        RecordingSink sink = new();
        MetricSnapshotCache cache = new();

        using MetricSamplingService sampler = new(
            [
                new SlowCollector("first", TimeSpan.FromMilliseconds(250)),
                new SlowCollector("second", TimeSpan.Zero),
                new SlowCollector("third", TimeSpan.FromMilliseconds(120)),
            ],
            cache,
            sink,
            NullLogger<MetricSamplingService>.Instance);

        await sampler.StartAsync(CancellationToken.None);

        try
        {
            MachineSnapshot first = await sink.FirstSnapshot.WaitAsync(TimeSpan.FromSeconds(15));

            // "second" finishes first and "first" finishes last: if arrival order counted, the
            // list would come out reversed.
            Assert.Equal(
                ["first", "second", "third"],
                first.Collectors.Select(collector => collector.CollectorId));
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Holds on to the first sample delivered to history.</summary>
    private sealed class RecordingSink : IMetricSnapshotSink
    {
        private readonly TaskCompletionSource<MachineSnapshot> first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MachineSnapshot> FirstSnapshot => first.Task;

        public void Enqueue(MachineSnapshot snapshot) => first.TrySetResult(snapshot);
    }

    /// <summary>How many collections were in flight at the same moment, at most.</summary>
    private sealed class OverlapCounter
    {
        private int inFlight;
        private int peak;

        public int Peak => Volatile.Read(ref peak);

        public IDisposable Enter()
        {
            int current = Interlocked.Increment(ref inFlight);

            // Raise the peak until somebody else raises it higher: without the loop, two
            // collections entering together can overwrite each other and the count would lag
            // behind in exactly the case that matters.
            int seen = Volatile.Read(ref peak);

            while (current > seen)
            {
                int previous = Interlocked.CompareExchange(ref peak, current, seen);

                if (previous == seen)
                {
                    break;
                }

                seen = previous;
            }

            return new Exit(this);
        }

        private void Leave() => Interlocked.Decrement(ref inFlight);

        private sealed class Exit(OverlapCounter counter) : IDisposable
        {
            public void Dispose() => counter.Leave();
        }
    }

    private sealed class SlowCollector(string id, TimeSpan? delay = null, OverlapCounter? counter = null)
        : IMetricCollector
    {
        private readonly TimeSpan wait = delay ?? DefaultDelay;

        public string Id { get; } = id;

        public IReadOnlyList<MetricDescriptor> Descriptors =>
            [new MetricDescriptor(Id + ".value", "Value", MetricUnit.None, IsPerInstance: false)];

        public async ValueTask<MetricSnapshot> CollectAsync(CancellationToken cancellationToken)
        {
            using IDisposable? scope = counter?.Enter();

            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }

            return new MetricSnapshot(
                Id,
                CollectorStatus.Ok,
                null,
                [MetricPoint.Measured(Id + ".value", null, MetricValue.FromNumber(1d))]);
        }
    }
}