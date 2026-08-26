using Observer.Core.Units;

namespace Observer.Core.Metrics.Cpu;

/// <summary>
/// Contatori cumulativi di tempo CPU letti dalla piattaforma. L'unita' dei tick non e'
/// dichiarata di proposito: su Linux sono jiffy, su Windows intervalli da 100 ns, e nel
/// rapporto fra due differenze l'unita' si semplifica. Questo e' cio' che rende identica
/// la matematica sulle due piattaforme.
/// </summary>
/// <param name="Idle">Tempo cumulativo trascorso in inattivita'.</param>
/// <param name="Total">Tempo cumulativo totale, inattivita' inclusa.</param>
public readonly record struct CpuTimes(long Idle, long Total);

/// <summary>
/// Matematica pura dell'utilizzo CPU. Non apre file, non chiama l'OS e non attende:
/// due campioni dentro, una percentuale fuori. E' il punto in cui si compra la
/// testabilita' senza hardware.
/// </summary>
public static class CpuUsage
{
    /// <summary>
    /// Calcola la percentuale di CPU occupata fra due campioni cumulativi.
    /// Restituisce false, valorizzando <paramref name="failure"/>, quando la finestra non
    /// e' utilizzabile: e' preferibile un valore assente e spiegato a un numero inventato.
    /// </summary>
    public static bool TryComputePercent(
        CpuTimes previous,
        CpuTimes current,
        out Percent percent,
        out SampleFailure failure)
    {
        long deltaTotal = current.Total - previous.Total;
        long deltaIdle = current.Idle - previous.Idle;

        if (deltaTotal < 0L || deltaIdle < 0L)
        {
            percent = default;
            failure = SampleFailure.CounterWentBackwards;
            return false;
        }

        if (deltaTotal == 0L)
        {
            percent = default;
            failure = SampleFailure.NoElapsedTime;
            return false;
        }

        long busy = deltaTotal - deltaIdle;

        // Entrambi i delta possono essere positivi e la finestra restare incoerente: se idle
        // cresce piu' del totale, "occupato" e' negativo. Succede quando "steal" arretra dopo
        // una live migration (iowait si cancella da entrambi i lati e sfugge al guard sopra),
        // o su Windows perche' l'aggregazione per-processore di GetSystemTimes non e' atomica.
        // Senza questo controllo si pubblicherebbe un -3% marcato Ok, che a grafico passa per
        // rumore: un numero sbagliato e credibile, cioe' il caso peggiore.
        if (busy < 0L)
        {
            percent = default;
            failure = SampleFailure.CounterWentBackwards;
            return false;
        }

        if (!Percent.TryFromRatio((double)busy / deltaTotal, out percent))
        {
            percent = default;
            failure = SampleFailure.NotFinite;
            return false;
        }

        failure = SampleFailure.Unknown;
        return true;
    }
}
