using Observer.Core.Processes;
using Observer.Core.Units;

namespace Observer.Core.Tests;

/// <summary>
/// Chi consuma cosa, e i modi in cui quel numero puo' mentire.
/// </summary>
/// <remarks>
/// La memoria di un processo si legge e si mostra. La CPU no: e' un tasso, quindi nasce da due
/// campioni, e il numero sbagliato qui non ha l'aria di un errore — ha l'aria di un colpevole.
/// Attribuire il 90% di CPU al programma sbagliato manda a terminare la cosa sbagliata.
/// </remarks>
public class ProcessRankingTests
{
    private const int Core = 4;

    private static ProcessTimes Processo(int pid, string nome, double secondiDiCpu, long byteInMemoria) =>
        new(pid, nome, TimeSpan.FromSeconds(secondiDiCpu), ByteSize.FromBytes(byteInMemoria));

    [Fact]
    public void AlPrimoGiroLaMemoriaCEELaCpuNo()
    {
        // Zero sarebbe una bugia: non e' "questo processo non sta lavorando", e' "non lo so
        // ancora". A schermo la differenza e' fra un trattino e uno zero, e sono due cose
        // diverse.
        Banco banco = new([Processo(10, "notepad", 5, 100_000)]);

        Assert.True(banco.Classifica.TryLeggi(out IReadOnlyList<ProcessUsage> primo));

        ProcessUsage solo = Assert.Single(primo);
        Assert.Null(solo.CpuPercent);
        Assert.Equal(100_000L, solo.WorkingSet.Bytes);
    }

    [Fact]
    public void DalSecondoGiroLaCpuESullaMacchinaIntera()
    {
        // Mezzo secondo di processore in un secondo, su quattro core, e' il 12,5% della
        // macchina - non il 50%. Se fosse riferita a un core solo, un processo a pieno regime
        // su una macchina a 16 core direbbe 100% e sembrerebbe che la macchina sia satura.
        Banco banco = new([Processo(10, "notepad", 5, 100_000)]);

        banco.Classifica.TryLeggi(out _);
        banco.Avanza([Processo(10, "notepad", 5.5, 100_000)]);

        Assert.True(banco.Classifica.TryLeggi(out IReadOnlyList<ProcessUsage> secondo));
        Assert.Equal(12.5d, Assert.Single(secondo).CpuPercent!.Value, 6);
    }

    [Fact]
    public void UnPidRiusatoNonEreditaIlTempoDelMorto()
    {
        // La trappola vera. Il sistema riassegna i numeri: se il PID 10 era "notepad" con 5
        // secondi di processore e ora e' "chrome" con 300, la differenza attribuirebbe a
        // chrome un tempo consumato da qualcun altro - e lo metterebbe in cima all'elenco,
        // cioe' esattamente dove chi guarda decide che cosa terminare.
        Banco banco = new([Processo(10, "notepad", 5, 100_000)]);

        banco.Classifica.TryLeggi(out _);
        banco.Avanza([Processo(10, "chrome", 300, 100_000)]);

        Assert.True(banco.Classifica.TryLeggi(out IReadOnlyList<ProcessUsage> secondo));
        Assert.Null(Assert.Single(secondo).CpuPercent);
    }

    [Fact]
    public void UnTempoCheTornaIndietroNonProduceUnaPercentuale()
    {
        Banco banco = new([Processo(10, "notepad", 5, 100_000)]);

        banco.Classifica.TryLeggi(out _);
        banco.Avanza([Processo(10, "notepad", 4, 100_000)]);

        Assert.True(banco.Classifica.TryLeggi(out IReadOnlyList<ProcessUsage> secondo));
        Assert.Null(Assert.Single(secondo).CpuPercent);
    }

    [Fact]
    public void LaPercentualeNonSuperaIlCento()
    {
        // Otto secondi di processore in un secondo su quattro core: il doppio del possibile.
        // Succede perche' l'orologio del campionamento e quello dei contatori non sono lo
        // stesso orologio, ed e' la stessa ragione per cui si limita l'occupazione dei dischi.
        Banco banco = new([Processo(10, "build", 0, 100_000)]);

        banco.Classifica.TryLeggi(out _);
        banco.Avanza([Processo(10, "build", 8, 100_000)]);

        Assert.True(banco.Classifica.TryLeggi(out IReadOnlyList<ProcessUsage> secondo));
        Assert.Equal(100d, Assert.Single(secondo).CpuPercent!.Value);
    }

    [Fact]
    public void UnaLetturaFallitaAzzeraLaStoria()
    {
        Banco banco = new([Processo(10, "notepad", 5, 100_000)]);

        banco.Classifica.TryLeggi(out _);

        banco.Elenco.Leggibile = false;
        banco.Orologio.Avanza(TimeSpan.FromSeconds(1));

        Assert.False(banco.Classifica.TryLeggi(out IReadOnlyList<ProcessUsage> rotto));
        Assert.Empty(rotto);

        banco.Elenco.Leggibile = true;
        banco.Avanza([Processo(10, "notepad", 500, 100_000)]);

        // Senza l'azzeramento, i 495 secondi accumulati durante il buco verrebbero divisi per
        // l'ultimo secondo e notepad risulterebbe il colpevole di tutto.
        Assert.True(banco.Classifica.TryLeggi(out IReadOnlyList<ProcessUsage> ripresa));
        Assert.Null(Assert.Single(ripresa).CpuPercent);
    }

    [Fact]
    public void ChiNonHaAncoraUnaPercentualeVaInFondo()
    {
        List<ProcessUsage> tutti =
        [
            new(1, "ignoto", null, ByteSize.FromBytes(10)),
            new(2, "fermo", 0d, ByteSize.FromBytes(10)),
            new(3, "affamato", 80d, ByteSize.FromBytes(10)),
        ];

        IReadOnlyList<ProcessUsage> ordinati = ProcessRanking.PiuAffamatiDiCpu(tutti, 3);

        Assert.Equal(["affamato", "fermo", "ignoto"], ordinati.Select(processo => processo.Name));
    }

    [Fact]
    public void LaMemoriaSiOrdinaPerByteOccupati()
    {
        List<ProcessUsage> tutti =
        [
            new(1, "piccolo", 0d, ByteSize.FromBytes(1_000)),
            new(2, "grosso", 0d, ByteSize.FromBytes(9_000)),
            new(3, "medio", 0d, ByteSize.FromBytes(5_000)),
        ];

        IReadOnlyList<ProcessUsage> ordinati = ProcessRanking.PiuAffamatiDiMemoria(tutti, 2);

        Assert.Equal(["grosso", "medio"], ordinati.Select(processo => processo.Name));
    }

    /// <summary>Classifica, elenco finto e orologio finto tenuti insieme, uno per test.</summary>
    private sealed class Banco
    {
        public Banco(IReadOnlyList<ProcessTimes> processi)
        {
            Elenco = new ElencoFinto { Processi = processi };
            Orologio = new OrologioFinto();
            Classifica = new ProcessRanking(Elenco, Orologio, Core);
        }

        public ElencoFinto Elenco { get; }

        public OrologioFinto Orologio { get; }

        public ProcessRanking Classifica { get; }

        public void Avanza(IReadOnlyList<ProcessTimes> processi)
        {
            Elenco.Processi = processi;
            Orologio.Avanza(TimeSpan.FromSeconds(1));
        }
    }

    private sealed class OrologioFinto : TimeProvider
    {
        private long adesso;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => adesso;

        public void Avanza(TimeSpan quanto) => adesso += quanto.Ticks;
    }

    private sealed class ElencoFinto : IProcessLister
    {
        public IReadOnlyList<ProcessTimes> Processi { get; set; } = [];

        public bool Leggibile { get; set; } = true;

        public bool TryList(out IReadOnlyList<ProcessTimes> processes)
        {
            processes = Processi;

            return Leggibile;
        }
    }
}
