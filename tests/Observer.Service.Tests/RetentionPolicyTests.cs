using System.Globalization;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// Le due decisioni che, sbagliate, non fanno rumore: consolidare un bucket ancora aperto
/// (medie false per sempre, perche' il grezzo poi sparisce) e cancellare grezzo che nessuno
/// ha ancora aggregato (buco nello storico che nessuno puo' piu' ricostruire).
/// </summary>
public class RetentionPolicyTests
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FiveSecondGrace = TimeSpan.FromSeconds(5);

    private static long Ms(string isoInstant) =>
        DateTimeOffset.Parse(isoInstant, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUnixTimeMilliseconds();

    [Fact]
    public void Horizon_DoesNotConsolidateTheInProgressBucket()
    {
        // Alle 12:03:47 il minuto delle 12:03 non e' finito: aggregarlo adesso scriverebbe
        // una media su 47 campioni invece che su 60, e siccome il grezzo verra' cancellato
        // quel numero resterebbe sbagliato per sempre.
        long horizon = RollupMath.AlignToBucketStart(
            RetentionPolicy.ConsolidationHorizon(Ms("2026-08-26T12:03:47Z"), OneMinute, TimeSpan.Zero),
            OneMinute);

        Assert.Equal(Ms("2026-08-26T12:03:00Z"), horizon);
    }

    [Fact]
    public void Horizon_WaitsOutTheGraceAfterTheBucketCloses()
    {
        // Il minuto delle 12:02 si e' chiuso alle 12:03:00, cioe' due secondi fa. I campioni
        // dei suoi ultimi istanti sono ancora nella coda in memoria: consolidarlo adesso
        // significa perderli.
        long horizon = RetentionPolicy.ConsolidationHorizon(
            Ms("2026-08-26T12:03:02Z"), OneMinute, FiveSecondGrace);

        Assert.Equal(Ms("2026-08-26T12:02:00Z"), horizon);
    }

    [Fact]
    public void Horizon_ConsolidatesABucketClosedLongerThanTheGrace()
    {
        long horizon = RetentionPolicy.ConsolidationHorizon(
            Ms("2026-08-26T12:03:07Z"), OneMinute, FiveSecondGrace);

        Assert.Equal(Ms("2026-08-26T12:03:00Z"), horizon);
    }

    [Fact]
    public void Horizon_WithNoGraceStopsAtTheCurrentBucketStart()
    {
        long horizon = RetentionPolicy.ConsolidationHorizon(
            Ms("2026-08-26T12:03:07Z"), OneMinute, TimeSpan.Zero);

        Assert.Equal(Ms("2026-08-26T12:03:00Z"), horizon);
    }

    [Fact]
    public void Horizon_RejectsANegativeGrace()
    {
        // Una grazia negativa consoliderebbe bucket dal FUTURO, cioe' ancora vuoti.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RetentionPolicy.ConsolidationHorizon(0L, OneMinute, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Cutoff_WithNothingConsolidatedDeletesNothing()
    {
        // Se il rollup non ha mai girato, ogni cancellazione e' una perdita secca: non
        // esiste un aggregato che conservi quei numeri. Meglio un file che cresce di un
        // buco nello storico.
        long? cutoff = RetentionPolicy.PurgeCutoff(
            Ms("2026-08-26T12:00:00Z"), TimeSpan.FromHours(6), consolidatedThroughMs: null);

        Assert.Null(cutoff);
    }

    [Fact]
    public void Cutoff_NeverGoesPastWhatHasBeenConsolidated()
    {
        // IL test della ritenzione. Il rollup e' rimasto indietro (servizio fermo, disco
        // lento, riavvio): la ritenzione da sola cancellerebbe fino alle 06:00, ma dalle
        // 03:00 in poi nessuno ha ancora aggregato niente. Cancellare li' significa perdere
        // quei dati e basta, senza errori e senza log.
        long? cutoff = RetentionPolicy.PurgeCutoff(
            Ms("2026-08-26T12:00:00Z"),
            TimeSpan.FromHours(6),
            Ms("2026-08-26T03:00:00Z"));

        Assert.Equal(Ms("2026-08-26T03:00:00Z"), cutoff);
    }

    [Fact]
    public void Cutoff_UsesTheRetentionWindowWhenConsolidationIsAhead()
    {
        long? cutoff = RetentionPolicy.PurgeCutoff(
            Ms("2026-08-26T12:00:00Z"),
            TimeSpan.FromHours(6),
            Ms("2026-08-26T11:00:00Z"));

        Assert.Equal(Ms("2026-08-26T06:00:00Z"), cutoff);
    }

    [Fact]
    public void Cutoff_RejectsANonPositiveRetention()
    {
        // Una ritenzione a zero cancellerebbe i dati nello stesso istante in cui li scrive:
        // il servizio girerebbe, il file resterebbe piccolo e lo storico sarebbe sempre vuoto.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RetentionPolicy.PurgeCutoff(0L, TimeSpan.Zero, 0L));
    }
}
