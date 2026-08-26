using System.Text.Json;
using Observer.Core.Metrics;

namespace Observer.Core.Tests;

/// <summary>
/// Il confine di serializzazione. E' il punto in cui un difetto non si vede compilando ne'
/// guardando il JSON in uscita: si vede solo rimettendo dentro cio' che e' uscito. Un valore
/// che si serializza ma non si rideserializza produce un client pieno di zeri marcati "Ok",
/// cioe' il bug piu' pericoloso possibile per chi non puo' leggere il codice.
/// </summary>
public class WireFormatTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static TheoryData<string, MetricValue> ValoriDiOgniTipo() => new()
    {
        { "numero", MetricValue.FromNumber(34122366976d) },
        { "testo", MetricValue.FromText("Samsung 990") },
        { "flag", MetricValue.FromFlag(true) },
    };

    [Theory]
    [MemberData(nameof(ValoriDiOgniTipo))]
    public void MetricValue_OgniTipo_SopravviveAlRoundTrip(string nome, MetricValue originale)
    {
        // Iterare su TUTTI i tipi non e' pedanteria: il giorno in cui qualcuno aggiunge un
        // quarto MetricValueKind senza aggiornare la serializzazione, questo test fallisce
        // da solo invece di lasciare che il valore sparisca in silenzio.
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [
                new MetricSnapshot(
                    "prova",
                    CollectorStatus.Ok,
                    null,
                    [MetricPoint.Measured(nome, null, originale)]),
            ]);

        string json = JsonSerializer.Serialize(snapshot, Options);
        MachineSnapshot? tornato = JsonSerializer.Deserialize<MachineSnapshot>(json, Options);

        MetricValue valore = tornato!.Collectors[0].Points[0].Value!.Value;

        Assert.Equal(originale.Kind, valore.Kind);
        Assert.Equal(originale.Number, valore.Number);
        Assert.Equal(originale.Text, valore.Text);
        Assert.Equal(originale.Flag, valore.Flag);
    }

    [Fact]
    public void MachineSnapshot_SopravviveAlRoundTrip_ConStatoEIstanza()
    {
        // Struttura e diagnostica devono tornare indietro quanto i valori: uno stato
        // degradato che si perde nel trasporto diventa un guasto invisibile.
        MachineSnapshot snapshot = new(
            MachineSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            [
                new MetricSnapshot("cpu", CollectorStatus.Unsupported, "niente ntdll qui", []),
                new MetricSnapshot("smart", CollectorStatus.Ok, null,
                    [MetricPoint.Measured("smart.temp", "nvme0", MetricValue.FromNumber(41d))]),
            ]);

        string json = JsonSerializer.Serialize(snapshot, Options);
        MachineSnapshot tornato = JsonSerializer.Deserialize<MachineSnapshot>(json, Options)!;

        Assert.Equal(MachineSnapshot.CurrentSchemaVersion, tornato.SchemaVersion);
        Assert.Equal(DateTimeOffset.UnixEpoch, tornato.CapturedAt);
        Assert.Equal(CollectorStatus.Unsupported, tornato.Collectors[0].Status);
        Assert.Equal("niente ntdll qui", tornato.Collectors[0].Message);
        Assert.Equal("nvme0", tornato.Collectors[1].Points[0].Instance);
        Assert.Equal(41d, tornato.Collectors[1].Points[0].Value!.Value.Number);
    }

    [Fact]
    public void MachineSnapshot_PortaLaVersioneDiSchemaSulFilo()
    {
        // Senza versione sul filo, un client e un servizio compilati da commit diversi
        // divergono in silenzio con campi a zero invece che con un messaggio leggibile.
        MachineSnapshot snapshot = new(MachineSnapshot.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, []);

        string json = JsonSerializer.Serialize(snapshot, Options);

        Assert.Contains("schemaVersion", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void MetricValue_FromNumber_RifiutaIValoriNonFiniti(double valoreRotto)
    {
        // Un NaN accettato qui fa LANCIARE il serializzatore piu' tardi, e a quel punto non
        // si perde una metrica: si perde l'INTERA risposta HTTP, tutte le altre comprese.
        // Meglio un errore rumoroso subito, dove si vede chi lo ha prodotto.
        Assert.Throws<ArgumentOutOfRangeException>(() => MetricValue.FromNumber(valoreRotto));
    }
}
