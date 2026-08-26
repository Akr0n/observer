using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;
using Observer.Core.Units;

namespace Observer.Core.Tests;

/// <summary>
/// Il cuore del progetto: due campioni di contatori dentro, una percentuale fuori.
/// Nessun hardware, nessun I/O, nessuna attesa di tempo reale. Se questi test sono verdi
/// la matematica e' corretta su entrambe le piattaforme, perche' l'unita' dei tick si
/// semplifica nel rapporto e non conta se sono jiffy Linux o intervalli da 100 ns Windows.
/// </summary>
public class CpuUsageMathTests
{
    [Fact]
    public void TryComputePercent_MetaCaricoSuFinestraNota_RestituisceCinquanta()
    {
        // Finestra: total +1000 tick, di cui 500 in idle. Occupato = 500/1000 = 50%.
        CpuTimes precedente = new(Idle: 1000L, Total: 2000L);
        CpuTimes corrente = new(Idle: 1500L, Total: 3000L);

        bool riuscito = CpuUsage.TryComputePercent(precedente, corrente, out Percent uso, out SampleFailure _);

        Assert.True(riuscito);
        Assert.Equal(50.0, uso.Points);
    }

    [Fact]
    public void TryComputePercent_DueCampioniIdentici_FallisceConNoElapsedTime()
    {
        // Su Windows i contatori si aggiornano solo sul clock tick (~15,6 ms): due letture
        // ravvicinate danno delta ESATTAMENTE zero. Senza questo guard sarebbe 0/0 = NaN,
        // cioe' JSON non valido e dashboard rotta.
        CpuTimes stesso = new(Idle: 1000L, Total: 2000L);

        bool riuscito = CpuUsage.TryComputePercent(stesso, stesso, out Percent _, out SampleFailure motivo);

        Assert.False(riuscito);
        Assert.Equal(SampleFailure.NoElapsedTime, motivo);
    }

    [Fact]
    public void TryComputePercent_ContatoriTornatiIndietro_FallisceConCounterWentBackwards()
    {
        // Dopo suspend/resume o migrazione di VM il delta e' negativo.
        CpuTimes precedente = new(Idle: 5000L, Total: 9000L);
        CpuTimes corrente = new(Idle: 1000L, Total: 2000L);

        bool riuscito = CpuUsage.TryComputePercent(precedente, corrente, out Percent _, out SampleFailure motivo);

        Assert.False(riuscito);
        Assert.Equal(SampleFailure.CounterWentBackwards, motivo);
    }

    [Fact]
    public void TryComputePercent_IdleCresceOltreIlTotale_FallisceInveceDiPubblicareUnNegativo()
    {
        // Finestra incoerente: entrambi i delta sono POSITIVI, quindi il guard sui contatori
        // che arretrano non scatta, ma idle cresce piu' del totale e "occupato" diventa
        // negativo. Succede davvero: su Linux basta che "steal" arretri dopo una live
        // migration (iowait si cancella da entrambi i lati e sfugge al primo controllo); su
        // Windows l'aggregazione per-processore di GetSystemTimes non e' atomica e idle puo'
        // risultare in anticipo su kernel. A grafico un -3% passa per rumore: e' un numero
        // sbagliato pubblicato come valido, cioe' esattamente cio' che non deve accadere.
        CpuTimes precedente = new(Idle: 500L, Total: 1000L);
        CpuTimes corrente = new(Idle: 520L, Total: 1010L);

        bool riuscito = CpuUsage.TryComputePercent(precedente, corrente, out Percent uso, out SampleFailure motivo);

        Assert.False(riuscito);
        Assert.Equal(SampleFailure.CounterWentBackwards, motivo);
        Assert.Equal(0.0, uso.Points);
    }

    [Fact]
    public void Describe_OgniMotivoDiFallimento_HaUnaSpiegazionePropriaENonVuota()
    {
        // Il committente deve leggere in dashboard PERCHE' manca il dato, non trovare un
        // buco muto. Non verifico le parole esatte (sarebbe un test che si rompe a ogni
        // riscrittura del testo): verifico che una spiegazione ci sia e che i motivi
        // diversi non collassino tutti sulla stessa frase generica.
        string indietro = SampleFailureText.Describe(SampleFailure.CounterWentBackwards);
        string fermo = SampleFailureText.Describe(SampleFailure.NoElapsedTime);

        Assert.False(string.IsNullOrWhiteSpace(indietro));
        Assert.False(string.IsNullOrWhiteSpace(fermo));
        Assert.NotEqual(indietro, fermo);
    }

    [Fact]
    public void SampleFailure_ValoreZero_EUnknownENonUnMotivoReale()
    {
        // default(SampleFailure) non deve spacciarsi per una causa diagnosticata: uno zero
        // che significa "CounterWentBackwards" farebbe apparire una diagnosi mai fatta.
        Assert.Equal(SampleFailure.Unknown, default(SampleFailure));
    }
}
