using Microsoft.Extensions.Logging;
using Observer.Core.Metrics;
using Observer.Service;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// Quante righe scrive il campionatore quando una sorgente si guasta, e quali.
/// </summary>
/// <remarks>
/// Il freno da solo e' provato altrove; qui si prova il CABLAGGIO, che e' dove stanno le due
/// decisioni che il codice si preoccupa di commentare e che nessun test toccava: un freno per
/// SORGENTE e non uno condiviso — con uno solo, il guasto della seconda sorgente sarebbe
/// silenziato dal guasto della prima — e la riga di rientro, senza la quale il registro resta
/// con l'inizio del guasto e nessuna fine.
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

        // Quattro giri, due sorgenti guaste: senza freno sarebbero otto righe; con UN freno
        // condiviso ne resterebbe una sola invece di due, e il guasto della seconda sorgente
        // non comparirebbe da nessuna parte.
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

            // Una sola sorgente guasta, una sola riga: la sana non dice niente.
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

    /// <summary>Conta i giri e avvisa quando ne ha visti abbastanza.</summary>
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

    /// <summary>Tiene il conto delle righe scritte, per evento.</summary>
    private sealed class LogRecorder : ILogger<MetricSamplingService>
    {
        private readonly Lock gate = new();
        private readonly List<(int Evento, LogLevel Livello)> lines = [];

        public int CountFor(int eventId)
        {
            lock (gate)
            {
                return lines.Count(line => line.Evento == eventId);
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
