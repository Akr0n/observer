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
public class AvvisiDelCampionatoreTests
{
    private const int EventoGuasto = 1;
    private const int EventoRientro = 5;

    [Fact]
    public async Task DueSorgentiGuasteHannoUnaRigaCiascunaUnaSolaVolta()
    {
        Registro registro = new();
        SinkContatore sink = new(quanti: 4);
        CollettoreCapriccioso primo = new("uno", new InvalidOperationException("primo"));
        CollettoreCapriccioso secondo = new("due", new NotSupportedException("secondo"));

        using MetricSamplingService campionatore = new(
            [primo, secondo],
            new MetricSnapshotCache(),
            sink,
            registro);

        await campionatore.StartAsync(CancellationToken.None);

        try
        {
            await sink.Raggiunti.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await campionatore.StopAsync(CancellationToken.None);
        }

        // Quattro giri, due sorgenti guaste: senza freno sarebbero otto righe; con UN freno
        // condiviso ne resterebbe una sola invece di due, e il guasto della seconda sorgente
        // non comparirebbe da nessuna parte.
        Assert.Equal(2, registro.Quante(EventoGuasto));
    }

    [Fact]
    public async Task QuandoLaSorgenteGuarisceLaRigaDiRientroArriva()
    {
        Registro registro = new();
        SinkContatore sink = new(quanti: 3);
        CollettoreCapriccioso capriccioso = new("uno", new InvalidOperationException("guasto"));
        CollettoreCapriccioso sano = new("due", guasto: null);

        using MetricSamplingService campionatore = new(
            [capriccioso, sano],
            new MetricSnapshotCache(),
            sink,
            registro);

        await campionatore.StartAsync(CancellationToken.None);

        try
        {
            await sink.Raggiunti.WaitAsync(TimeSpan.FromSeconds(30));

            // Una sola sorgente guasta, una sola riga: la sana non dice niente.
            Assert.Equal(1, registro.Quante(EventoGuasto));
            Assert.Equal(0, registro.Quante(EventoRientro));

            SinkContatore dopo = new(quanti: 3);
            sink.Passa(dopo);
            capriccioso.Guarisci();

            await dopo.Raggiunti.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(1, registro.Quante(EventoRientro));
            Assert.Equal(1, registro.Quante(EventoGuasto));
        }
        finally
        {
            await campionatore.StopAsync(CancellationToken.None);
        }
    }

    private sealed class CollettoreCapriccioso(string id, Exception? guasto) : IMetricCollector
    {
        private Exception? guasto = guasto;

        public string Id { get; } = id;

        public IReadOnlyList<MetricDescriptor> Descriptors =>
            [new MetricDescriptor(Id + ".valore", "Valore", MetricUnit.None, IsPerInstance: false)];

        public void Guarisci() => guasto = null;

        public ValueTask<MetricSnapshot> CollectAsync(CancellationToken cancellationToken)
        {
            if (guasto is not null)
            {
                throw guasto;
            }

            return ValueTask.FromResult(new MetricSnapshot(
                Id,
                CollectorStatus.Ok,
                null,
                [MetricPoint.Measured(Id + ".valore", null, MetricValue.FromNumber(1d))]));
        }
    }

    /// <summary>Conta i giri e avvisa quando ne ha visti abbastanza.</summary>
    private sealed class SinkContatore(int quanti) : IMetricSnapshotSink
    {
        private readonly TaskCompletionSource arrivati =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private SinkContatore? erede;
        private int visti;

        public Task Raggiunti => arrivati.Task;

        public void Passa(SinkContatore altro) => erede = altro;

        public void Enqueue(MachineSnapshot snapshot)
        {
            if (++visti >= quanti)
            {
                arrivati.TrySetResult();
            }

            erede?.Enqueue(snapshot);
        }
    }

    /// <summary>Tiene il conto delle righe scritte, per evento.</summary>
    private sealed class Registro : ILogger<MetricSamplingService>
    {
        private readonly Lock serratura = new();
        private readonly List<(int Evento, LogLevel Livello)> righe = [];

        public int Quante(int evento)
        {
            lock (serratura)
            {
                return righe.Count(riga => riga.Evento == evento);
            }
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => Vuoto.Solo;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (serratura)
            {
                righe.Add((eventId.Id, logLevel));
            }
        }

        private sealed class Vuoto : IDisposable
        {
            public static readonly Vuoto Solo = new();

            public void Dispose()
            {
            }
        }
    }
}
