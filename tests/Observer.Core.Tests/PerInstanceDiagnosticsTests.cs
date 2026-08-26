using System.Text.Json;
using Observer.Core.Metrics;

namespace Observer.Core.Tests;

/// <summary>
/// La prova del requisito "misurare qualsiasi parametro". Il collector SMART qui sotto e'
/// definito INTERAMENTE in questo file di test: se per farlo funzionare servisse modificare
/// anche un solo file di Observer.Core, il design avrebbe perso la sua proprieta' principale.
/// </summary>
/// <remarks>
/// Il caso scelto e' quello che rompe un design a stato unico per collector: tre dischi, di
/// cui uno dietro un bridge USB che non inoltra i comandi SMART. Con un solo stato per
/// collector le uniche scelte sarebbero dichiarare tutto Ok facendo sparire in silenzio il
/// disco problematico, oppure dichiarare tutto Unavailable perdendo anche i dischi sani.
/// Entrambe sono vietate: la prima nasconde un guasto, la seconda degrada piu' del dovuto.
/// </remarks>
public class PerInstanceDiagnosticsTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task DischiMisti_IlSanoRiportaIlValoreEIlProblematicoRiportaIlMotivo()
    {
        SmartCollector collector = new();

        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

        // Il collector nel suo insieme funziona: e' un disco a non essere leggibile.
        Assert.Equal(CollectorStatus.Ok, snapshot.Status);

        MetricPoint sano = Assert.Single(
            snapshot.Points,
            p => p.MetricId == "smart.temperature" && p.Instance == "nvme0");
        Assert.Equal(CollectorStatus.Ok, sano.Status);
        Assert.Equal(41d, sano.Value!.Value.Number);

        MetricPoint problematico = Assert.Single(
            snapshot.Points,
            p => p.MetricId == "smart.temperature" && p.Instance == "sdb");
        Assert.Equal(CollectorStatus.Unsupported, problematico.Status);
        Assert.Null(problematico.Value);
        Assert.Contains("USB", problematico.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValoriMistiSullaStessaIstanza_NumeroTestoEFlagConvivono()
    {
        // SMART emette per lo stesso disco un numero, un testo e un booleano. Se il
        // vocabolario non li reggesse tutti e tre, servirebbe un secondo formato di trasporto.
        SmartCollector collector = new();

        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(
            MetricValueKind.Number,
            snapshot.Points.Single(p => p.MetricId == "smart.temperature" && p.Instance == "nvme0").Value!.Value.Kind);
        Assert.Equal(
            MetricValueKind.Text,
            snapshot.Points.Single(p => p.MetricId == "smart.model").Value!.Value.Kind);
        Assert.Equal(
            MetricValueKind.Flag,
            snapshot.Points.Single(p => p.MetricId == "smart.failurePredicted").Value!.Value.Kind);
    }

    [Fact]
    public async Task LaDiagnosticaPerIstanza_SopravviveAlTrasporto()
    {
        // Un motivo che si perde nel trasporto e' un guasto invisibile: il client vedrebbe
        // un punto senza valore e senza spiegazione, cioe' un buco muto.
        SmartCollector collector = new();
        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);
        MachineSnapshot macchina = new(MachineSnapshot.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, [snapshot]);

        string json = JsonSerializer.Serialize(macchina, Options);
        MachineSnapshot tornata = JsonSerializer.Deserialize<MachineSnapshot>(json, Options)!;

        MetricPoint problematico = tornata.Collectors[0].Points
            .Single(p => p.MetricId == "smart.temperature" && p.Instance == "sdb");

        Assert.Equal(CollectorStatus.Unsupported, problematico.Status);
        Assert.Null(problematico.Value);
        Assert.Contains("USB", problematico.Message, StringComparison.Ordinal);

        MetricPoint sano = tornata.Collectors[0].Points
            .Single(p => p.MetricId == "smart.temperature" && p.Instance == "nvme0");
        Assert.Equal(41d, sano.Value!.Value.Number);
    }

    [Fact]
    public void UnitaDiMisuraNuova_NonRichiedeDiToccareIlCore()
    {
        // Il grado Celsius non esiste fra le unita' predefinite, e non deve servire
        // aggiungercelo: MetricUnit e' un tipo aperto proprio per questo.
        MetricUnit gradi = new("degC");

        Assert.Equal("degC", gradi.Symbol);
    }

    /// <summary>
    /// Sorgente SMART finta, scritta per intero qui dentro. Il fatto che compili senza
    /// modificare alcun file di Observer.Core E' la dimostrazione che il punto di estensione
    /// tiene: nessuna interfaccia allargata, nessun enum ampliato, nessun caso aggiunto altrove.
    /// </summary>
    private sealed class SmartCollector : IMetricCollector
    {
        private static readonly MetricDescriptor[] DescriptorList =
        [
            new("smart.temperature", "Temperatura disco", new MetricUnit("degC"), IsPerInstance: true),
            new("smart.model", "Modello disco", MetricUnit.None, IsPerInstance: true),
            new("smart.failurePredicted", "Guasto previsto", MetricUnit.None, IsPerInstance: true),
        ];

        public string Id => "smart";

        public IReadOnlyList<MetricDescriptor> Descriptors => DescriptorList;

        public ValueTask<MetricSnapshot> CollectAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MetricSnapshot(
                Id,
                CollectorStatus.Ok,
                Message: null,
                [
                    MetricPoint.Measured("smart.temperature", "nvme0", MetricValue.FromNumber(41d)),
                    MetricPoint.Measured("smart.model", "nvme0", MetricValue.FromText("Samsung 990 PRO")),
                    MetricPoint.Measured("smart.failurePredicted", "nvme0", MetricValue.FromFlag(false)),
                    MetricPoint.Unsupported(
                        "smart.temperature",
                        "sdb",
                        "il bridge USB non inoltra i comandi SMART a questo dispositivo"),
                ]));
    }
}
