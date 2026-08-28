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
    public async Task LeSorgentiSonoInVoloTutteInsieme()
    {
        // Si CONTA quante raccolte sono aperte nello stesso momento, invece di cronometrare
        // il giro. Un tempo assoluto qui non dimostra niente: su un runner carico 1320 ms
        // sono compatibili sia con tre raccolte in fila sia con tre raccolte insieme piu'
        // l'avvio del servizio, e infatti la prima stesura di questo test falliva accusando
        // il codice di una cosa che non poteva dimostrare. Il numero di raccolte
        // contemporanee, invece, e' tre oppure uno, e non dipende da quanto va veloce la
        // macchina.
        Contatore contatore = new();
        SinkRegistrante sink = new();
        MetricSnapshotCache cache = new();

        using MetricSamplingService campionatore = new(
            [
                new CollettoreLento("uno", contatore: contatore),
                new CollettoreLento("due", contatore: contatore),
                new CollettoreLento("tre", contatore: contatore),
            ],
            cache,
            sink,
            NullLogger<MetricSamplingService>.Instance);

        await campionatore.StartAsync(CancellationToken.None);

        try
        {
            await sink.PrimoSnapshot.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(3, contatore.Massimo);
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

    /// <summary>Quante raccolte sono state aperte nello stesso momento, al massimo.</summary>
    private sealed class Contatore
    {
        private int aperte;
        private int massimo;

        public int Massimo => Volatile.Read(ref massimo);

        public IDisposable Entra()
        {
            int adesso = Interlocked.Increment(ref aperte);

            // Alza il massimo finche' qualcun altro non lo alza di piu': senza il ciclo, due
            // raccolte che entrano insieme possono sovrascriversi a vicenda e il conteggio
            // resterebbe indietro proprio nel caso che interessa.
            int visto = Volatile.Read(ref massimo);

            while (adesso > visto)
            {
                int precedente = Interlocked.CompareExchange(ref massimo, adesso, visto);

                if (precedente == visto)
                {
                    break;
                }

                visto = precedente;
            }

            return new Uscita(this);
        }

        private void Esce() => Interlocked.Decrement(ref aperte);

        private sealed class Uscita(Contatore contatore) : IDisposable
        {
            public void Dispose() => contatore.Esce();
        }
    }

    private sealed class CollettoreLento(string id, TimeSpan? quanto = null, Contatore? contatore = null)
        : IMetricCollector
    {
        private readonly TimeSpan attesa = quanto ?? Lentezza;

        public string Id { get; } = id;

        public IReadOnlyList<MetricDescriptor> Descriptors =>
            [new MetricDescriptor(Id + ".valore", "Valore", MetricUnit.None, IsPerInstance: false)];

        public async ValueTask<MetricSnapshot> CollectAsync(CancellationToken cancellationToken)
        {
            using IDisposable? presenza = contatore?.Entra();

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