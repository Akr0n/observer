using Observer.Core.Metrics.Cpu;
using Observer.Core.Metrics.Memory;
using Observer.Core.Platform.Linux;

namespace Observer.Core.Tests;

/// <summary>
/// Parser puri di /proc: stringa dentro, lettura fuori. Non toccano il filesystem, quindi
/// girano identici sul runner Windows e su quello Linux e la CI e' verde su entrambi.
/// Ogni test qui blocca un bug SILENZIOSO: nessuno di questi errori farebbe crashare il
/// servizio, tutti produrrebbero numeri credibili e sbagliati.
/// </summary>
public class ProcParserTests
{
    [Fact]
    public void ProcStat_RigaAggregataConDoppioSpazio_NonSfasaLeColonne()
    {
        // Due trappole in un test solo:
        // (a) la riga aggregata "cpu" ha DUE spazi, le righe "cpuN" ne hanno UNO. Uno split
        //     ingenuo sfasa le colonne e fa leggere "user" dove c'e' "nice".
        // (b) guest e guest_nice (gli ultimi due) sono GIA' conteggiati dentro user e nice:
        //     risommarli gonfia il denominatore e fa sottostimare la CPU.
        //           user nice system  idle iowait irq softirq steal guest guest_nice
        const string stat = "cpu  95 0 530 17966 170 0 119 0 0 0\n";

        bool riuscito = ProcStatParser.TryParseAggregate(stat, out CpuTimes tempi);

        Assert.True(riuscito);
        Assert.Equal(18136L, tempi.Idle);   // idle 17966 + iowait 170
        Assert.Equal(18880L, tempi.Total);  // somma dei primi OTTO campi, guest esclusi
    }

    [Fact]
    public void ProcStat_KernelVecchioAQuattroColonne_NonLancia()
    {
        // Caso reale: il /proc emulato di MSYS2 espone solo quattro colonne. Indicizzare a
        // lunghezza fissa farebbe crashare il servizio all'avvio.
        const string stat = "cpu 100 0 200 300\n";

        bool riuscito = ProcStatParser.TryParseAggregate(stat, out CpuTimes tempi);

        Assert.True(riuscito);
        Assert.Equal(300L, tempi.Idle);
        Assert.Equal(600L, tempi.Total);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("cpu\n")]
    [InlineData("cpu pippo pluto\n")]
    [InlineData("intr 12345 0 0\n")]
    public void ProcStat_InputDegenere_RestituisceFalseSenzaLanciare(string contenuto)
    {
        // Il parser e' il confine con dati che non controlliamo. Deve degradare, non
        // lanciare: un'eccezione qui abbatterebbe il campionamento di TUTTE le metriche.
        bool riuscito = ProcStatParser.TryParseAggregate(contenuto, out CpuTimes _);

        Assert.False(riuscito);
    }

    [Fact]
    public void ProcStat_ConFineRigaWindows_NonLasciaIlRitornoCarrelloNelNumero()
    {
        // I parser dipendono dal fatto che EnumerateLines tolga il \r dei fine riga Windows.
        // E' un'assunzione su codice altrui: se cadesse, l'ultimo numero di ogni riga
        // arriverebbe come "0\r", long.TryParse fallirebbe e la CPU risulterebbe
        // permanentemente Unavailable senza che nulla vada in crash.
        const string stat = "cpu  95 0 530 17966 170 0 119 0 0 0\r\ncpu0 12 0 209 4245 23 0 85 0 0 0\r\n";

        bool riuscito = ProcStatParser.TryParseAggregate(stat, out CpuTimes tempi);

        Assert.True(riuscito);
        Assert.Equal(18136L, tempi.Idle);
        Assert.Equal(18880L, tempi.Total);
    }

    [Fact]
    public void ProcMeminfo_ConFineRigaWindows_NonLasciaIlRitornoCarrelloNelNumero()
    {
        const string meminfo = "MemTotal:        1048576 kB\r\nMemAvailable:     524288 kB\r\n";

        bool riuscito = ProcMeminfoParser.TryParse(meminfo, out MemoryReading lettura);

        Assert.True(riuscito);
        Assert.Equal(1048576L * 1024L, lettura.Total.Bytes);
        Assert.Equal(524288L * 1024L, lettura.Available.Bytes);
    }

    [Fact]
    public void ProcMeminfo_UsaMemAvailableNonMemFree()
    {
        // Il bug piu' insidioso della RAM su Linux. MemFree ignora la cache riutilizzabile,
        // quindi usarlo fa apparire quasi ogni macchina al 95-99% e genera falsi allarmi
        // permanenti. Qui MemFree direbbe 99%, MemAvailable dice 50%.
        const string meminfo = """
            MemTotal:        1048576 kB
            MemFree:           10240 kB
            MemAvailable:     524288 kB
            """;

        bool riuscito = ProcMeminfoParser.TryParse(meminfo, out MemoryReading lettura);

        Assert.True(riuscito);
        Assert.Equal(1048576L * 1024L, lettura.Total.Bytes);
        Assert.Equal(524288L * 1024L, lettura.Available.Bytes);
        Assert.Equal(524288L * 1024L, lettura.Used.Bytes);
        Assert.False(lettura.AvailableWasEstimated);
    }

    [Fact]
    public void ProcMeminfo_SenzaMemAvailable_StimaEDichiaraDiAverStimato()
    {
        // Kernel < 3.14 e /proc parziali non espongono MemAvailable. Il punto non e' solo
        // stimare: e' DICHIARARE di aver stimato, cosi' la UI puo' scrivere "approssimato"
        // invece di presentare una stima come una misura.
        // stima = Free 10240 + Buffers 2048 + Cached 500000 + SReclaimable 12000 - Shmem 4288
        const string meminfo = """
            MemTotal:        1048576 kB
            MemFree:           10240 kB
            Buffers:            2048 kB
            Cached:           500000 kB
            SReclaimable:      12000 kB
            Shmem:              4288 kB
            """;

        bool riuscito = ProcMeminfoParser.TryParse(meminfo, out MemoryReading lettura);

        Assert.True(riuscito);
        Assert.Equal(520000L * 1024L, lettura.Available.Bytes);
        Assert.True(lettura.AvailableWasEstimated);
    }

    [Fact]
    public void ProcMeminfo_SenzaMemTotal_RestituisceFalse()
    {
        // Un totale a zero renderebbe la percentuale una divisione per zero. Meglio
        // dichiarare il campione non credibile che pubblicarne uno inventato.
        const string meminfo = "MemFree:  10240 kB\nMemAvailable:  524288 kB\n";

        bool riuscito = ProcMeminfoParser.TryParse(meminfo, out MemoryReading _);

        Assert.False(riuscito);
    }

    [Fact]
    public void ProcMeminfo_SenzaSwap_RiportaSwapTotaleZero()
    {
        // Una macchina senza swap e' una configurazione legittima, non un guasto.
        const string meminfo = "MemTotal: 1048576 kB\nMemAvailable: 524288 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n";

        bool riuscito = ProcMeminfoParser.TryParse(meminfo, out MemoryReading lettura);

        Assert.True(riuscito);
        Assert.Equal(0L, lettura.SwapTotal.Bytes);
    }
}
