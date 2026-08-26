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
    private static readonly TimeSpan UnMinuto = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CinqueSecondiDiGrazia = TimeSpan.FromSeconds(5);

    private static long Ms(string istanteIso) =>
        DateTimeOffset.Parse(istanteIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUnixTimeMilliseconds();

    [Fact]
    public void Orizzonte_NonConsolidaIlBucketInCorso()
    {
        // Alle 12:03:47 il minuto delle 12:03 non e' finito: aggregarlo adesso scriverebbe
        // una media su 47 campioni invece che su 60, e siccome il grezzo verra' cancellato
        // quel numero resterebbe sbagliato per sempre.
        long orizzonte = RollupMath.AlignToBucketStart(
            RetentionPolicy.ConsolidationHorizon(Ms("2026-08-26T12:03:47Z"), UnMinuto, TimeSpan.Zero),
            UnMinuto);

        Assert.Equal(Ms("2026-08-26T12:03:00Z"), orizzonte);
    }

    [Fact]
    public void Orizzonte_AspettaLaGraziaDopoLaChiusuraDelBucket()
    {
        // Il minuto delle 12:02 si e' chiuso alle 12:03:00, cioe' due secondi fa. I campioni
        // dei suoi ultimi istanti sono ancora nella coda in memoria: consolidarlo adesso
        // significa perderli.
        long orizzonte = RetentionPolicy.ConsolidationHorizon(
            Ms("2026-08-26T12:03:02Z"), UnMinuto, CinqueSecondiDiGrazia);

        Assert.Equal(Ms("2026-08-26T12:02:00Z"), orizzonte);
    }

    [Fact]
    public void Orizzonte_ConsolidaIlBucketChiusoDaPiuDellaGrazia()
    {
        long orizzonte = RetentionPolicy.ConsolidationHorizon(
            Ms("2026-08-26T12:03:07Z"), UnMinuto, CinqueSecondiDiGrazia);

        Assert.Equal(Ms("2026-08-26T12:03:00Z"), orizzonte);
    }

    [Fact]
    public void Orizzonte_SenzaGraziaSiFermaAllInizioDelBucketCorrente()
    {
        long orizzonte = RetentionPolicy.ConsolidationHorizon(
            Ms("2026-08-26T12:03:07Z"), UnMinuto, TimeSpan.Zero);

        Assert.Equal(Ms("2026-08-26T12:03:00Z"), orizzonte);
    }

    [Fact]
    public void Orizzonte_RifiutaUnaGraziaNegativa()
    {
        // Una grazia negativa consoliderebbe bucket dal FUTURO, cioe' ancora vuoti.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RetentionPolicy.ConsolidationHorizon(0L, UnMinuto, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Soglia_SenzaConsolidamentoNonCancellaNulla()
    {
        // Se il rollup non ha mai girato, ogni cancellazione e' una perdita secca: non
        // esiste un aggregato che conservi quei numeri. Meglio un file che cresce di un
        // buco nello storico.
        long? soglia = RetentionPolicy.PurgeCutoff(
            Ms("2026-08-26T12:00:00Z"), TimeSpan.FromHours(6), consolidatedThroughMs: null);

        Assert.Null(soglia);
    }

    [Fact]
    public void Soglia_NonSuperaMaiIlConsolidamento()
    {
        // IL test della ritenzione. Il rollup e' rimasto indietro (servizio fermo, disco
        // lento, riavvio): la ritenzione da sola cancellerebbe fino alle 06:00, ma dalle
        // 03:00 in poi nessuno ha ancora aggregato niente. Cancellare li' significa perdere
        // quei dati e basta, senza errori e senza log.
        long? soglia = RetentionPolicy.PurgeCutoff(
            Ms("2026-08-26T12:00:00Z"),
            TimeSpan.FromHours(6),
            Ms("2026-08-26T03:00:00Z"));

        Assert.Equal(Ms("2026-08-26T03:00:00Z"), soglia);
    }

    [Fact]
    public void Soglia_UsaLaRitenzioneQuandoIlConsolidamentoEPiuAvanti()
    {
        long? soglia = RetentionPolicy.PurgeCutoff(
            Ms("2026-08-26T12:00:00Z"),
            TimeSpan.FromHours(6),
            Ms("2026-08-26T11:00:00Z"));

        Assert.Equal(Ms("2026-08-26T06:00:00Z"), soglia);
    }

    [Fact]
    public void Soglia_RifiutaUnaRitenzioneNonPositiva()
    {
        // Una ritenzione a zero cancellerebbe i dati nello stesso istante in cui li scrive:
        // il servizio girerebbe, il file resterebbe piccolo e lo storico sarebbe sempre vuoto.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RetentionPolicy.PurgeCutoff(0L, TimeSpan.Zero, 0L));
    }
}
