using Observer.Core.Metrics;
using Observer.Core.Metrics.Disk;
using Observer.Core.Units;

namespace Observer.Core.Tests;

/// <summary>
/// L'aritmetica dei tassi di lettura e scrittura.
/// </summary>
/// <remarks>
/// Questa e' la prima metrica che misura una VELOCITA', e cambia le regole. Lo spazio su
/// disco si legge e si pubblica; i byte al secondo esistono solo come differenza fra due
/// campioni divisa per il tempo passato in mezzo, e ogni modo in cui quella divisione puo'
/// andare storta produrrebbe un numero credibile e falso.
/// <para>
/// La regola che nessuno indovina: la percentuale di tempo occupato NON si ottiene sommando
/// il tempo di lettura e quello di scrittura. Le due code si sovrappongono, e su una stessa
/// finestra quella somma ha gia' dato 843%. Si ricava dall'INATTIVITA' su Windows e dai tick
/// di occupato su Linux, che sono la stessa grandezza vista dai due lati.
/// </para>
/// </remarks>
public class DiskActivityRatesTests
{
    private static DiskActivityReading Inattivo(ulong letti, ulong scritti, double secondiInattivo) =>
        DiskActivityReading.ConTempoInattivo("Disk 0", letti, scritti, TimeSpan.FromSeconds(secondiInattivo));

    private static DiskActivityReading Occupato(ulong letti, ulong scritti, double secondiOccupato) =>
        DiskActivityReading.ConTempoOccupato("sda", letti, scritti, TimeSpan.FromSeconds(secondiOccupato));

    [Fact]
    public void IlTassoEIlDeltaDivisoIlTempo()
    {
        Assert.True(DiskActivityRates.TryComputeBytesPerSecond(
            1_000UL, 3_000UL, TimeSpan.FromSeconds(2), out double tasso, out _));

        Assert.Equal(1_000d, tasso);
    }

    [Fact]
    public void UnContatoreCheTornaIndietroNonProduceUnTasso()
    {
        // Sospensione, ripristino, disco staccato e riattaccato, migrazione di macchina
        // virtuale. Il delta calcolato su ulong darebbe un numero enorme e plausibile.
        Assert.False(DiskActivityRates.TryComputeBytesPerSecond(
            3_000UL, 1_000UL, TimeSpan.FromSeconds(2), out _, out SampleFailure guasto));

        Assert.Equal(SampleFailure.CounterWentBackwards, guasto);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SenzaTempoTrascorsoNonSiCalcolaNiente(int secondi)
    {
        // Una divisione per zero qui non darebbe un errore: darebbe infinito, e
        // MetricValue.FromNumber lancerebbe, azzerando l'INTERA risposta HTTP per colpa di
        // un disco solo.
        Assert.False(DiskActivityRates.TryComputeBytesPerSecond(
            0UL, 1_000UL, TimeSpan.FromSeconds(secondi), out _, out SampleFailure guasto));

        Assert.Equal(SampleFailure.NoElapsedTime, guasto);
    }

    [Fact]
    public void LOccupazioneSiCalcolaDallInattivita()
    {
        // Windows conta il tempo INATTIVO. Su un secondo di intervallo con 0,6 s di
        // inattivita', il disco ha lavorato il 40% del tempo.
        Assert.True(DiskActivityRates.TryComputeBusy(
            Inattivo(0UL, 0UL, 10.0),
            Inattivo(0UL, 0UL, 10.6),
            TimeSpan.FromSeconds(1),
            out Percent occupato,
            out _));

        Assert.Equal(40d, occupato.Points, 6);
    }

    [Fact]
    public void UnDiscoFermoNonPuoRisultareOccupatoNegativamente()
    {
        // Misurato su questa macchina, PhysicalDrive1 fermo: l'inattivita' avanza di un
        // filo PIU' dell'intervallo, perche' i due orologi non sono lo stesso orologio, e il
        // calcolo dava -0,07%. Percent.TryFromRatio rifiuta i negativi, quindi senza limite
        // un disco fermo si dichiarerebbe GUASTO invece che fermo.
        Assert.True(DiskActivityRates.TryComputeBusy(
            Inattivo(0UL, 0UL, 10.0),
            Inattivo(0UL, 0UL, 11.0007),
            TimeSpan.FromSeconds(1),
            out Percent occupato,
            out _));

        Assert.Equal(0d, occupato.Points);
    }

    [Fact]
    public void LOccupazioneSiCalcolaAncheDaiTickDiOccupato()
    {
        // Linux conta il tempo OCCUPATO: la stessa grandezza vista dall'altro lato.
        Assert.True(DiskActivityRates.TryComputeBusy(
            Occupato(0UL, 0UL, 5.0),
            Occupato(0UL, 0UL, 5.25),
            TimeSpan.FromSeconds(1),
            out Percent occupato,
            out _));

        Assert.Equal(25d, occupato.Points, 6);
    }

    [Fact]
    public void LOccupazioneNonSuperaIlCentoPerCento()
    {
        // Con piu' richieste in coda i tick di occupato possono superare l'intervallo. Il
        // disco non e' occupato al 150%: e' occupato, e basta.
        Assert.True(DiskActivityRates.TryComputeBusy(
            Occupato(0UL, 0UL, 5.0),
            Occupato(0UL, 0UL, 6.5),
            TimeSpan.FromSeconds(1),
            out Percent occupato,
            out _));

        Assert.Equal(100d, occupato.Points);
    }

    [Fact]
    public void IlTempoDiOccupazioneCheTornaIndietroSiDichiara()
    {
        Assert.False(DiskActivityRates.TryComputeBusy(
            Occupato(0UL, 0UL, 6.0),
            Occupato(0UL, 0UL, 5.0),
            TimeSpan.FromSeconds(1),
            out _,
            out SampleFailure guasto));

        Assert.Equal(SampleFailure.CounterWentBackwards, guasto);
    }
}

/// <summary>
/// Il collector dell'attivita' dei dischi.
/// </summary>
/// <remarks>
/// Tiene uno stato — la lettura precedente e l'istante in cui e' stata presa — e lo tiene
/// PER ISTANZA, che e' la differenza rispetto alla CPU: i dischi compaiono e spariscono
/// mentre il programma gira, e un disco appena comparso non deve rubare il campione
/// precedente di un altro.
/// </remarks>
public class DiskActivityCollectorTests
{
    private static readonly TimeSpan Giro = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task IlPrimoGiroEUnRiscaldamentoSenzaPunti()
    {
        // Zero non e' "il disco e' fermo": e' "non lo so ancora". Pubblicare zero al primo
        // giro e' il modo piu' facile di far sembrare fermo un disco che sta lavorando.
        ProviderFinto provider = new([Inattivo("Disk 0", 0UL, 0UL, 0)]);
        (DiskActivityCollector collector, _) = Crea(provider);

        MetricSnapshot primo = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Warmup, primo.Status);
        Assert.Empty(primo.Points);
    }

    [Fact]
    public async Task DalSecondoGiroCiSonoTrePuntiPerDisco()
    {
        ProviderFinto provider = new([Inattivo("Disk 0", 0UL, 0UL, 0)]);
        (DiskActivityCollector collector, OrologioFinto orologio) = Crea(provider);

        await collector.CollectAsync(CancellationToken.None);

        provider.Letture = [Inattivo("Disk 0", 2_000UL, 6_000UL, 0.75)];
        orologio.Avanza(Giro);

        MetricSnapshot secondo = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Ok, secondo.Status);
        Assert.Equal(3, secondo.Points.Count);
        Assert.Equal(2_000d, Valore(secondo, DiskActivityCollector.ReadBytesPerSecondMetricId));
        Assert.Equal(6_000d, Valore(secondo, DiskActivityCollector.WriteBytesPerSecondMetricId));
        Assert.Equal(25d, Valore(secondo, DiskActivityCollector.BusyPercentMetricId), 6);
    }

    [Fact]
    public async Task OgniDiscoHaLaSuaIstanza()
    {
        ProviderFinto provider = new(
            [Inattivo("Disk 0", 0UL, 0UL, 0), Inattivo("Disk 1", 0UL, 0UL, 0)]);
        (DiskActivityCollector collector, OrologioFinto orologio) = Crea(provider);

        await collector.CollectAsync(CancellationToken.None);

        provider.Letture =
            [Inattivo("Disk 0", 1_000UL, 0UL, 0.5), Inattivo("Disk 1", 4_000UL, 0UL, 1.0)];
        orologio.Avanza(Giro);

        MetricSnapshot secondo = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(3, secondo.Points.Count(p => p.Instance == "Disk 0"));
        Assert.Equal(3, secondo.Points.Count(p => p.Instance == "Disk 1"));

        // Se lo stato fosse tenuto per collector invece che per disco, i due si
        // ruberebbero il campione precedente a vicenda e i numeri sarebbero incrociati.
        Assert.Equal(
            1_000d,
            secondo.Points.Single(p =>
                p.MetricId == DiskActivityCollector.ReadBytesPerSecondMetricId
                && p.Instance == "Disk 0").Value!.Value.Number);
    }

    [Fact]
    public async Task UnDiscoCheCompareDopoAspettaIlSuoSecondoCampione()
    {
        // Una chiavetta infilata adesso non ha un campione precedente. Calcolare il suo
        // tasso sul contatore assoluto darebbe "da quando esiste il disco", non "adesso":
        // un numero enorme, e nessuno lo segnalerebbe.
        ProviderFinto provider = new([Inattivo("Disk 0", 0UL, 0UL, 0)]);
        (DiskActivityCollector collector, OrologioFinto orologio) = Crea(provider);

        await collector.CollectAsync(CancellationToken.None);

        provider.Letture =
            [Inattivo("Disk 0", 1_000UL, 0UL, 0.5), Inattivo("Disk 9", 999_999UL, 0UL, 0.5)];
        orologio.Avanza(Giro);

        MetricSnapshot secondo = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(3, secondo.Points.Count(p => p.Instance == "Disk 0"));
        Assert.DoesNotContain(secondo.Points, p => p.Instance == "Disk 9" && p.Value is not null);
    }

    [Fact]
    public async Task UnDiscoCheSparisceNonLasciaNumeriFermi()
    {
        // Un disco staccato non deve continuare a mostrare l'ultimo numero: sarebbe una
        // misura ferma che si legge come attuale.
        ProviderFinto provider = new(
            [Inattivo("Disk 0", 0UL, 0UL, 0), Inattivo("Disk 1", 0UL, 0UL, 0)]);
        (DiskActivityCollector collector, OrologioFinto orologio) = Crea(provider);

        await collector.CollectAsync(CancellationToken.None);

        provider.Letture = [Inattivo("Disk 0", 1_000UL, 0UL, 0.5)];
        orologio.Avanza(Giro);

        MetricSnapshot secondo = await collector.CollectAsync(CancellationToken.None);

        Assert.DoesNotContain(secondo.Points, p => p.Instance == "Disk 1");
    }

    [Fact]
    public async Task UnaLetturaFallitaAzzeraLaStoria()
    {
        // Il buco e' il punto: riprendendo dopo un errore, il delta sarebbe calcolato su un
        // intervallo di cui non si sa la durata. Meglio un giro di riscaldamento in piu'
        // che una media inventata su un tempo sconosciuto.
        ProviderFinto provider = new([Inattivo("Disk 0", 0UL, 0UL, 0)]);
        (DiskActivityCollector collector, OrologioFinto orologio) = Crea(provider);

        await collector.CollectAsync(CancellationToken.None);

        provider.Leggibile = false;
        orologio.Avanza(Giro);
        MetricSnapshot rotto = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Unavailable, rotto.Status);

        provider.Leggibile = true;
        provider.Letture = [Inattivo("Disk 0", 50_000UL, 0UL, 0.5)];
        orologio.Avanza(Giro);
        MetricSnapshot ripresa = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Warmup, ripresa.Status);
        Assert.Empty(ripresa.Points);
    }

    [Fact]
    public async Task UnaPiattaformaCheNonSiSaMisurareLoDice()
    {
        ProviderFinto provider = new([], supportato: false, motivo: "qui non si misura");
        (DiskActivityCollector collector, _) = Crea(provider);

        MetricSnapshot snapshot = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorStatus.Unsupported, snapshot.Status);
        Assert.Equal("qui non si misura", snapshot.Message);
    }

    [Fact]
    public void IlCatalogoDichiaraTuttoPerIstanza()
    {
        (DiskActivityCollector collector, _) = Crea(new ProviderFinto([]));

        Assert.Equal(3, collector.Descriptors.Count);
        Assert.All(collector.Descriptors, d => Assert.True(d.IsPerInstance));

        // Un quadrante compare solo per le percentuali: i due tassi restano righe scritte,
        // ed e' una conseguenza voluta, non una dimenticanza.
        Assert.Single(collector.Descriptors, d => d.Unit == MetricUnit.Percent);
    }

    private static double Valore(MetricSnapshot snapshot, string metrica) =>
        snapshot.Points.Single(p => p.MetricId == metrica).Value!.Value.Number;

    private static DiskActivityReading Inattivo(
        string istanza, ulong letti, ulong scritti, double secondiInattivo) =>
        DiskActivityReading.ConTempoInattivo(
            istanza, letti, scritti, TimeSpan.FromSeconds(secondiInattivo));

    private static (DiskActivityCollector Collector, OrologioFinto Orologio) Crea(
        IDiskActivityProvider provider)
    {
        OrologioFinto orologio = new();

        return (new DiskActivityCollector(provider, orologio), orologio);
    }

    /// <summary>Un orologio che avanza solo quando glielo si dice.</summary>
    private sealed class OrologioFinto : TimeProvider
    {
        private long adesso;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => adesso;

        public void Avanza(TimeSpan quanto) => adesso += quanto.Ticks;
    }

    private sealed class ProviderFinto(
        IReadOnlyList<DiskActivityReading> letture,
        bool supportato = true,
        string? motivo = null) : IDiskActivityProvider
    {
        public IReadOnlyList<DiskActivityReading> Letture { get; set; } = letture;

        public bool Leggibile { get; set; } = true;

        public bool IsSupported => supportato;

        public string? UnsupportedReason => motivo;

        public bool TryRead(out IReadOnlyList<DiskActivityReading> readings)
        {
            readings = Letture;

            return Leggibile;
        }
    }
}