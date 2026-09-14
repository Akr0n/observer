using System.Globalization;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// La matematica del rollup, provata SENZA database. E' il punto piu' pericoloso di tutta la
/// persistenza: un errore qui non fa fallire nulla, non lancia e non si vede nei log —
/// produce grafici pieni di numeri plausibili e sbagliati. L'unico modo di scoprirlo e'
/// confrontare l'aggregato con il calcolo diretto sui campioni grezzi.
/// </summary>
public class RollupMathTests
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    private static long Ms(string instantIso) =>
        DateTimeOffset.Parse(instantIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUnixTimeMilliseconds();

    [Fact]
    public void AlignToBucketStart_SnapsToTheStartOfTheMinute()
    {
        long aligned = RollupMath.AlignToBucketStart(Ms("2026-08-26T12:03:47.812Z"), OneMinute);

        Assert.Equal(Ms("2026-08-26T12:03:00Z"), aligned);
    }

    [Fact]
    public void AlignToBucketStart_LeavesAnAlreadyAlignedInstantWhereItIs()
    {
        // Se un istante esattamente sul bordo scivolasse al bucket precedente, ogni bucket
        // conterrebbe un campione del bucket successivo e le medie sarebbero tutte sfalsate
        // di un campione: sbagliate di poco, quindi invisibili.
        long aligned = RollupMath.AlignToBucketStart(Ms("2026-08-26T12:05:00Z"), FiveMinutes);

        Assert.Equal(Ms("2026-08-26T12:05:00Z"), aligned);
    }

    [Fact]
    public void AlignToBucketStart_RoundsDownEvenBeforeTheEpoch()
    {
        // Con la divisione intera del C# -1500 / 60000 fa 0, e un istante prima del 1970
        // finirebbe nel bucket SUCCESSIVO invece che nel precedente. Non capita in
        // produzione, ma e' il modo piu' economico di verificare che l'arrotondamento sia un
        // vero floor e non un troncamento verso lo zero.
        long aligned = RollupMath.AlignToBucketStart(-1500L, OneMinute);

        Assert.Equal(-60000L, aligned);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public void AlignToBucketStart_RejectsANonPositiveWidth(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RollupMath.AlignToBucketStart(0L, TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void Aggregate_ComputesCountSumMinMaxAndLast()
    {
        RawSample[] samples =
        [
            new(Ms("2026-08-26T12:00:00Z"), 10d),
            new(Ms("2026-08-26T12:00:01Z"), 30d),
            new(Ms("2026-08-26T12:00:02Z"), 20d),
        ];

        RollupBucket bucket = Assert.Single(RollupMath.Aggregate(samples, OneMinute));

        Assert.Equal(Ms("2026-08-26T12:00:00Z"), bucket.BucketStartMs);
        Assert.Equal(3, bucket.Count);
        Assert.Equal(60d, bucket.Sum);
        Assert.Equal(10d, bucket.Min);
        Assert.Equal(30d, bucket.Max);
        Assert.Equal(20d, bucket.Last);
        Assert.Equal(20d, bucket.Average);
    }

    [Fact]
    public void Aggregate_SplitsTheBucketsAndReturnsThemInTimeOrder()
    {
        RawSample[] samples =
        [
            new(Ms("2026-08-26T12:01:30Z"), 5d),
            new(Ms("2026-08-26T12:00:30Z"), 1d),
            new(Ms("2026-08-26T12:00:31Z"), 3d),
        ];

        IReadOnlyList<RollupBucket> bucket = RollupMath.Aggregate(samples, OneMinute);

        Assert.Equal(2, bucket.Count);
        Assert.Equal(Ms("2026-08-26T12:00:00Z"), bucket[0].BucketStartMs);
        Assert.Equal(2, bucket[0].Count);
        Assert.Equal(Ms("2026-08-26T12:01:00Z"), bucket[1].BucketStartMs);
        Assert.Equal(1, bucket[1].Count);
    }

    [Fact]
    public void Aggregate_LastIsTheMostRecentNotTheLastToArrive()
    {
        // I campioni arrivano gia' ordinati dal database, ma "ultimo" deve significare
        // "piu' recente" e non "ultimo della lista": altrimenti il giorno in cui qualcuno
        // toglie l'ORDER BY dalla query il valore corrente mostrato in dashboard diventa un
        // valore vecchio a caso, senza che nulla fallisca.
        RawSample[] samplesOutOfOrder =
        [
            new(Ms("2026-08-26T12:00:59Z"), 99d),
            new(Ms("2026-08-26T12:00:01Z"), 1d),
        ];

        RollupBucket bucket = Assert.Single(RollupMath.Aggregate(samplesOutOfOrder, OneMinute));

        Assert.Equal(99d, bucket.Last);
    }

    [Fact]
    public void Aggregate_ProducesNoBucketWhenThereAreNoSamples()
    {
        // Un bucket vuoto avrebbe conteggio zero e media 0/0 = NaN, e un NaN in JSON fa
        // fallire l'INTERA risposta HTTP, non solo quella metrica.
        Assert.Empty(RollupMath.Aggregate([], OneMinute));
    }

    [Fact]
    public void Combine_TheFiveMinuteAverageMatchesTheAverageOfTheRawSamples()
    {
        // IL test. Cinque minuti con un numero DIVERSO di campioni ciascuno: e' il caso
        // normale, non un caso limite — succede a ogni riavvio del servizio, a ogni timeout
        // di un collector e ogni volta che una metrica compare a meta' minuto. Chi conserva
        // la media invece di somma e conteggio calcola qui la media delle medie e ottiene un
        // numero credibile e falso.
        RawSample[] rawSamples =
        [
            new(Ms("2026-08-26T12:00:10Z"), 100d),
            new(Ms("2026-08-26T12:01:10Z"), 0d),
            new(Ms("2026-08-26T12:01:20Z"), 0d),
            new(Ms("2026-08-26T12:01:30Z"), 0d),
            new(Ms("2026-08-26T12:02:10Z"), 0d),
            new(Ms("2026-08-26T12:02:20Z"), 0d),
            new(Ms("2026-08-26T12:03:10Z"), 0d),
            new(Ms("2026-08-26T12:04:10Z"), 0d),
        ];

        IReadOnlyList<RollupBucket> minuteBuckets = RollupMath.Aggregate(rawSamples, OneMinute);
        RollupBucket fiveMinuteBucket = Assert.Single(RollupMath.Combine(minuteBuckets, FiveMinutes));

        // Media vera: 100 / 8 = 12,5. Media delle medie: (100+0+0+0+0)/5 = 20.
        Assert.Equal(100d / 8d, fiveMinuteBucket.Average);
        Assert.Equal(8, fiveMinuteBucket.Count);
        Assert.Equal(100d, fiveMinuteBucket.Sum);
    }

    [Fact]
    public void Combine_TakesTheExtremesNotTheirSum()
    {
        RollupBucket[] minuteBuckets =
        [
            new(Ms("2026-08-26T12:00:00Z"), 60, 600d, 2d, 40d, 7d),
            new(Ms("2026-08-26T12:01:00Z"), 60, 600d, 5d, 90d, 9d),
        ];

        RollupBucket combined = Assert.Single(RollupMath.Combine(minuteBuckets, FiveMinutes));

        Assert.Equal(2d, combined.Min);
        Assert.Equal(90d, combined.Max);
    }

    [Fact]
    public void Combine_LastComesFromTheMostRecentBucket()
    {
        RollupBucket[] minuteBucketsOutOfOrder =
        [
            new(Ms("2026-08-26T12:04:00Z"), 60, 600d, 1d, 20d, 42d),
            new(Ms("2026-08-26T12:00:00Z"), 60, 600d, 1d, 20d, 7d),
        ];

        RollupBucket combined = Assert.Single(RollupMath.Combine(minuteBucketsOutOfOrder, FiveMinutes));

        Assert.Equal(42d, combined.Last);
        Assert.Equal(Ms("2026-08-26T12:00:00Z"), combined.BucketStartMs);
    }

    [Fact]
    public void Combine_KeepsDifferentFiveMinuteBucketsApart()
    {
        RollupBucket[] minuteBuckets =
        [
            new(Ms("2026-08-26T12:04:00Z"), 60, 60d, 1d, 1d, 1d),
            new(Ms("2026-08-26T12:05:00Z"), 60, 120d, 2d, 2d, 2d),
        ];

        IReadOnlyList<RollupBucket> combined = RollupMath.Combine(minuteBuckets, FiveMinutes);

        Assert.Equal(2, combined.Count);
        Assert.Equal(Ms("2026-08-26T12:00:00Z"), combined[0].BucketStartMs);
        Assert.Equal(Ms("2026-08-26T12:05:00Z"), combined[1].BucketStartMs);
    }

    [Fact]
    public void Bucket_RejectsANonPositiveCount()
    {
        // Un bucket a conteggio zero produce media NaN e fa saltare la serializzazione
        // dell'intera risposta. Meglio non lasciarlo nascere.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RollupBucket(0L, 0, 0d, 0d, 0d, 0d));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Bucket_RejectsNonFiniteValues(double brokenValue)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RollupBucket(0L, 1, brokenValue, brokenValue, brokenValue, brokenValue));
    }
}
