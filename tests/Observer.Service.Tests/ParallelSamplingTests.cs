using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Observer.Core.Metrics;
using Observer.Service;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// Che le sorgenti vengano interrogate INSIEME, non una dopo l'altra.
/// </summary>
/// <remarks>
/// In fila il giro dura la somma dei tempi, e il caso peggiore e' il numero di collector per
/// la scadenza di ciascuno: con due sorgenti supera gia' il secondo di campionamento, con
/// cinque lo quadruplica. Il guasto che ne segue non fa rumore — <c>PeriodicTimer</c> lascia
/// cadere i tick in silenzio, i campioni spariscono, e la striscia dello storico dichiara
/// "non misurato" un periodo in cui la macchina era accesa e sana. Nessun test fallirebbe:
/// per questo ce ne vuole uno che guardi il TEMPO.
/// </remarks>
public class ParallelSamplingTests
{
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task AllSourcesAreInFlightAtTheSameTime()
    {
        // Si CONTA quante raccolte sono aperte nello stesso momento, invece di cronometrare
        // il giro. Un tempo assoluto qui non dimostra niente: su un runner carico 1320 ms
        // sono compatibili sia con tre raccolte in fila sia con tre raccolte insieme piu'
        // l'avvio del servizio, e infatti la prima stesura di questo test falliva accusando
        // il codice di una cosa che non poteva dimostrare. Il numero di raccolte
        // contemporanee, invece, e' tre oppure uno, e non dipende da quanto va veloce la
        // macchina.
        OverlapCounter counter = new();
        RecordingSink sink = new();
        MetricSnapshotCache cache = new();

        using MetricSamplingService sampler = new(
            [
                new SlowCollector("uno", counter: counter),
                new SlowCollector("due", counter: counter),
                new SlowCollector("tre", counter: counter),
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
        // Interrogare insieme non deve voler dire consegnare in ordine di arrivo: i riquadri
        // a schermo si scambierebbero di posto a ogni secondo, e non ci sarebbe niente a
        // segnalarlo se non l'occhio di chi guarda.
        RecordingSink sink = new();
        MetricSnapshotCache cache = new();

        using MetricSamplingService sampler = new(
            [
                new SlowCollector("primo", TimeSpan.FromMilliseconds(250)),
                new SlowCollector("secondo", TimeSpan.Zero),
                new SlowCollector("terzo", TimeSpan.FromMilliseconds(120)),
            ],
            cache,
            sink,
            NullLogger<MetricSamplingService>.Instance);

        await sampler.StartAsync(CancellationToken.None);

        try
        {
            MachineSnapshot first = await sink.FirstSnapshot.WaitAsync(TimeSpan.FromSeconds(15));

            // "secondo" finisce per primo e "primo" per ultimo: se contasse l'ordine di
            // arrivo, l'elenco uscirebbe rovesciato.
            Assert.Equal(
                ["primo", "secondo", "terzo"],
                first.Collectors.Select(collector => collector.CollectorId));
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Trattiene il primo campionamento consegnato allo storico.</summary>
    private sealed class RecordingSink : IMetricSnapshotSink
    {
        private readonly TaskCompletionSource<MachineSnapshot> first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MachineSnapshot> FirstSnapshot => first.Task;

        public void Enqueue(MachineSnapshot snapshot) => first.TrySetResult(snapshot);
    }

    /// <summary>Quante raccolte sono state aperte nello stesso momento, al massimo.</summary>
    private sealed class OverlapCounter
    {
        private int inFlight;
        private int peak;

        public int Peak => Volatile.Read(ref peak);

        public IDisposable Enter()
        {
            int current = Interlocked.Increment(ref inFlight);

            // Alza il massimo finche' qualcun altro non lo alza di piu': senza il ciclo, due
            // raccolte che entrano insieme possono sovrascriversi a vicenda e il conteggio
            // resterebbe indietro proprio nel caso che interessa.
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
            [new MetricDescriptor(Id + ".valore", "Valore", MetricUnit.None, IsPerInstance: false)];

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
                [MetricPoint.Measured(Id + ".valore", null, MetricValue.FromNumber(1d))]);
        }
    }
}