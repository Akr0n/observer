using Observer.Core.Metrics;
using Observer.Core.Metrics.Disk;
using Observer.Core.Units;

namespace Observer.Core.Tests;

/// <summary>
/// Il collector dei dischi, e i modi in cui potrebbe mentire.
/// </summary>
/// <remarks>
/// E' il primo collector per ISTANZA, e la differenza conta: qui un guasto non riguarda "la
/// metrica", riguarda un volume solo. Tre dischi di cui uno che non si lascia interrogare
/// devono dare due misure e un motivo, non tre volte niente.
/// </remarks>
public class DiskCollectorTests
{
    private static DiskReading Volume(string nome, long totale, long libero) =>
        new(nome, ByteSize.FromBytes(totale), ByteSize.FromBytes(libero));

    private static MetricSnapshot Raccogli(IDiskReadingProvider provider) =>
        new DiskCollector(provider).CollectAsync(CancellationToken.None).AsTask().Result;

    [Fact]
    public void OgniVolumeDaQuattroPuntiConLaPropriaIstanza()
    {
        MetricSnapshot snapshot = Raccogli(new ProviderFinto(
            [
                Volume("C:", 500_000_000_000L, 100_000_000_000L),
                Volume("D:", 1_000_000_000_000L, 900_000_000_000L),
            ]));

        Assert.Equal(CollectorStatus.Ok, snapshot.Status);
        Assert.Equal(8, snapshot.Points.Count);

        // L'istanza e' cio' che tiene distinti due dischi: senza, le righe si sovrascrivono a
        // vicenda e a schermo ne resta una sola, con i numeri dell'ultimo letto.
        Assert.Equal(4, snapshot.Points.Count(punto => punto.Instance == "C:"));
        Assert.Equal(4, snapshot.Points.Count(punto => punto.Instance == "D:"));
    }

    [Fact]
    public void LoSpazioOccupatoEQuelloCheManca()
    {
        MetricSnapshot snapshot = Raccogli(new ProviderFinto([Volume("C:", 1000L, 250L)]));

        MetricPoint usato = snapshot.Points.Single(
            punto => punto.MetricId == DiskCollector.UsedBytesMetricId);

        Assert.Equal(750d, usato.Value!.Value.Number);

        MetricPoint percentuale = snapshot.Points.Single(
            punto => punto.MetricId == DiskCollector.UsedPercentMetricId);

        Assert.Equal(75d, percentuale.Value!.Value.Number);
    }

    [Fact]
    public void UnVolumePiuLiberoCheGrandeNonProduceUnaPercentualeAssurda()
    {
        // Succede: su un volume con quote o con blocchi riservati i due numeri arrivano da
        // contatori diversi. Una sottrazione negativa darebbe una percentuale negativa, che e'
        // peggio di un numero mancante perche' sembra comunque una misura.
        MetricSnapshot snapshot = Raccogli(new ProviderFinto([Volume("C:", 1000L, 1200L)]));

        MetricPoint percentuale = snapshot.Points.Single(
            punto => punto.MetricId == DiskCollector.UsedPercentMetricId);

        Assert.Equal(0d, percentuale.Value!.Value.Number);
    }

    [Fact]
    public void UnVolumeDiDimensioneZeroNonSiDichiaraVuoto()
    {
        // Zero byte totali NON vuol dire "c'e' tutto lo spazio del mondo": vuol dire che la
        // capienza non si sa. Succede sui montaggi speciali e sui dispositivi che si smontano
        // mentre li si legge. Pubblicare 0% sarebbe la bugia piu' rassicurante possibile.
        MetricSnapshot snapshot = Raccogli(new ProviderFinto([Volume("Z:", 0L, 0L)]));

        MetricPoint percentuale = snapshot.Points.Single(
            punto => punto.MetricId == DiskCollector.UsedPercentMetricId);

        Assert.Equal(CollectorStatus.Unavailable, percentuale.Status);
        Assert.Null(percentuale.Value);
        Assert.Contains("size of zero", percentuale.Message, StringComparison.Ordinal);

        // Ma capienza e spazio libero restano pubblicati: sono misure, per quanto valgano
        // zero, e toglierle nasconderebbe che quel volume esiste.
        Assert.Equal(4, snapshot.Points.Count);
    }

    [Fact]
    public void NessunVolumeNonEUnGuasto()
    {
        // Dentro un container minimale puo' non esserci un solo filesystem che valga la pena
        // mostrare. "Ok con zero punti" e "non sono riuscito a leggere" devono restare
        // distinguibili, altrimenti si va a cercare un guasto che non c'e'.
        MetricSnapshot snapshot = Raccogli(new ProviderFinto([]));

        Assert.Equal(CollectorStatus.Ok, snapshot.Status);
        Assert.Empty(snapshot.Points);
        Assert.NotNull(snapshot.Message);
    }

    [Fact]
    public void UnaPiattaformaCheNonSiSaMisurareLoDiceEnonSparisce()
    {
        MetricSnapshot snapshot = Raccogli(
            new ProviderFinto([], supportato: false, motivo: "questa piattaforma non ha volumi"));

        Assert.Equal(CollectorStatus.Unsupported, snapshot.Status);
        Assert.Equal("questa piattaforma non ha volumi", snapshot.Message);
    }

    [Fact]
    public void UnaLetturaFallitaSiDistingueDaZeroVolumi()
    {
        MetricSnapshot snapshot = Raccogli(new ProviderFinto([], leggibile: false));

        Assert.Equal(CollectorStatus.Unavailable, snapshot.Status);
        Assert.Empty(snapshot.Points);
    }

    [Fact]
    public void IlCatalogoDichiaraTutteEQuattroLeMetricheComePerIstanza()
    {
        // Se una fosse dichiarata non-per-istanza, il client la cercherebbe una volta sola e
        // mostrerebbe un disco solo, senza che niente lo segnali.
        DiskCollector collector = new(new ProviderFinto([]));

        Assert.Equal(4, collector.Descriptors.Count);
        Assert.All(collector.Descriptors, descrittore => Assert.True(descrittore.IsPerInstance));
    }

    private sealed class ProviderFinto(
        IReadOnlyList<DiskReading> letture,
        bool supportato = true,
        bool leggibile = true,
        string? motivo = null) : IDiskReadingProvider
    {
        public bool IsSupported => supportato;

        public string? UnsupportedReason => motivo;

        public bool TryRead(out IReadOnlyList<DiskReading> readings)
        {
            readings = letture;

            return leggibile;
        }
    }
}