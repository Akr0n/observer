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
public class CampionamentoParalleloTests
{
    private static readonly TimeSpan Lentezza = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task TreSorgentiLenteNonSommanoIProprioTempi()
    {
        // Tre sorgenti da 300 ms. In fila fanno 900 ms, insieme 300: la soglia sta in mezzo
        // con margine da entrambe le parti, cosi' il test non diventa fragile su una macchina
        // lenta ne' indulgente su una veloce.
        SinkRegistrante sink = new();
        MetricSnapshotCache cache = new();

        using MetricSamplingService campionatore = new(
            [new CollettoreLento("uno"), new CollettoreLento("due"), new CollettoreLento("tre")],
            cache,
            sink,
            NullLogger<MetricSamplingService>.Instance);

        long inizio = Stopwatch.GetTimestamp();

        await campionatore.StartAsync(CancellationToken.None);

        try
        {
            MachineSnapshot primo = await sink.PrimoSnapshot.WaitAsync(TimeSpan.FromSeconds(15));
            TimeSpan trascorso = Stopwatch.GetElapsedTime(inizio);

            Assert.Equal(3, primo.Collectors.Count);

            Assert.True(
                trascorso < TimeSpan.FromMilliseconds(600),
                $"Il giro ha impiegato {trascorso.TotalMilliseconds:F0} ms: con tre sorgenti da "
                    + $"{Lentezza.TotalMilliseconds:F0} ms sono state interrogate in fila, non insieme.");
        }
        finally
        {
            await campionatore.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LOrdineDeiRiquadriNonCambiaDaUnGiroAllAltro()
    {
        // Interrogare insieme non deve voler dire consegnare in ordine di arrivo: i riquadri
        // a schermo si scambierebbero di posto a ogni secondo, e non ci sarebbe niente a
        // segnalarlo se non l'occhio di chi guarda.
        SinkRegistrante sink = new();
        MetricSnapshotCache cache = new();

        using MetricSamplingService campionatore = new(
            [
                new CollettoreLento("primo", TimeSpan.FromMilliseconds(250)),
                new CollettoreLento("secondo", TimeSpan.Zero),
                new CollettoreLento("terzo", TimeSpan.FromMilliseconds(120)),
            ],
            cache,
            sink,
            NullLogger<MetricSamplingService>.Instance);

        await campionatore.StartAsync(CancellationToken.None);

        try
        {
            MachineSnapshot primo = await sink.PrimoSnapshot.WaitAsync(TimeSpan.FromSeconds(15));

            // "secondo" finisce per primo e "primo" per ultimo: se contasse l'ordine di
            // arrivo, l'elenco uscirebbe rovesciato.
            Assert.Equal(
                ["primo", "secondo", "terzo"],
                primo.Collectors.Select(collettore => collettore.CollectorId));
        }
        finally
        {
            await campionatore.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Trattiene il primo campionamento consegnato allo storico.</summary>
    private sealed class SinkRegistrante : IMetricSnapshotSink
    {
        private readonly TaskCompletionSource<MachineSnapshot> primo =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MachineSnapshot> PrimoSnapshot => primo.Task;

        public void Enqueue(MachineSnapshot snapshot) => primo.TrySetResult(snapshot);
    }

    private sealed class CollettoreLento(string id, TimeSpan? quanto = null) : IMetricCollector
    {
        private readonly TimeSpan attesa = quanto ?? Lentezza;

        public string Id { get; } = id;

        public IReadOnlyList<MetricDescriptor> Descriptors =>
            [new MetricDescriptor(Id + ".valore", "Valore", MetricUnit.None, IsPerInstance: false)];

        public async ValueTask<MetricSnapshot> CollectAsync(CancellationToken cancellationToken)
        {
            if (attesa > TimeSpan.Zero)
            {
                await Task.Delay(attesa, cancellationToken);
            }

            return new MetricSnapshot(
                Id,
                CollectorStatus.Ok,
                null,
                [MetricPoint.Measured(Id + ".valore", null, MetricValue.FromNumber(1d))]);
        }
    }
}