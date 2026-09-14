using Microsoft.Extensions.Logging;
using Observer.Core.Metrics;
using Observer.Service;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// How many lines the sampler writes when a source fails, and which ones.
/// </summary>
/// <remarks>
/// The throttle on its own is tested elsewhere; what is tested here is the WIRING, which is where
/// the two decisions the code bothers to comment on live and that no test touched: one throttle
/// per SOURCE and not a shared one — with a single one, the second source's fault would be
/// silenced by the first source's — and the recovery line, without which the log is left with the
/// start of the fault and no end.
/// </remarks>
public class SamplerWarningTests
{
    private const int FailureEvent = 1;
    private const int RecoveryEvent = 5;

    [Fact]
    public async Task EachFailingSourceIsLoggedOnceNotOnEveryRound()
    {
        LogRecorder recorder = new();
        CountingSink sink = new(expected: 4);
        FlakyCollector first = new("uno", new InvalidOperationException("primo"));
        FlakyCollector second = new("due", new NotSupportedException("secondo"));

        using MetricSamplingService sampler = new(
            [first, second],
            new MetricSnapshotCache(),
            sink,
            recorder);

        await sampler.StartAsync(CancellationToken.None);

        try
        {
            await sink.Reached.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }

        // Four rounds, two failing sources: with no throttle there would be eight lines; with ONE
        // shared throttle only a single line would be left instead of two, and the second source's
        // fault would appear nowhere.
        Assert.Equal(2, recorder.CountFor(FailureEvent));
    }

    [Fact]
    public async Task WhenASourceRecoversItsRecoveryLineIsWritten()
    {
        LogRecorder recorder = new();
        CountingSink sink = new(expected: 3);
        FlakyCollector flaky = new("uno", new InvalidOperationException("guasto"));
        FlakyCollector healthy = new("due", failure: null);

        using MetricSamplingService sampler = new(
            [flaky, healthy],
            new MetricSnapshotCache(),
            sink,
            recorder);

        await sampler.StartAsync(CancellationToken.None);

        try
        {
            await sink.Reached.WaitAsync(TimeSpan.FromSeconds(30));

            // One failing source, one single line: the healthy one says nothing.
            Assert.Equal(1, recorder.CountFor(FailureEvent));
            Assert.Equal(0, recorder.CountFor(RecoveryEvent));

            CountingSink next = new(expected: 3);
            sink.ChainTo(next);
            flaky.Recover();

            await next.Reached.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(1, recorder.CountFor(RecoveryEvent));
            Assert.Equal(1, recorder.CountFor(FailureEvent));
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FlakyCollector(string id, Exception? failure) : IMetricCollector
    {
        private Exception? failure = failure;

        public string Id { get; } = id;

        public IReadOnlyList<MetricDescriptor> Descriptors =>
            [new MetricDescriptor(Id + ".valore", "Valore", MetricUnit.None, IsPerInstance: false)];

        public void Recover() => failure = null;

        public ValueTask<MetricSnapshot> CollectAsync(CancellationToken cancellationToken)
        {
            if (failure is not null)
            {
                throw failure;
            }

            return ValueTask.FromResult(new MetricSnapshot(
                Id,
                CollectorStatus.Ok,
                null,
                [MetricPoint.Measured(Id + ".valore", null, MetricValue.FromNumber(1d))]));
        }
    }

    /// <summary>Counts the rounds and signals when it has seen enough of them.</summary>
    private sealed class CountingSink(int expected) : IMetricSnapshotSink
    {
        private readonly TaskCompletionSource signal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private CountingSink? successor;
        private int seen;

        public Task Reached => signal.Task;

        public void ChainTo(CountingSink next) => successor = next;

        public void Enqueue(MachineSnapshot snapshot)
        {
            if (++seen >= expected)
            {
                signal.TrySetResult();
            }

            successor?.Enqueue(snapshot);
        }
    }

    /// <summary>Keeps count of the lines written, per event.</summary>
    private sealed class LogRecorder : ILogger<MetricSamplingService>
    {
        private readonly Lock gate = new();
        private readonly List<(int EventId, LogLevel Level)> lines = [];

        public int CountFor(int eventId)
        {
            lock (gate)
            {
                return lines.Count(line => line.EventId == eventId);
            }
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NoScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (gate)
            {
                lines.Add((eventId.Id, logLevel));
            }
        }

        private sealed class NoScope : IDisposable
        {
            public static readonly NoScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
