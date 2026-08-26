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
    private static readonly TimeSpan UnMinuto = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CinqueMinuti = TimeSpan.FromMinutes(5);

    private static long Ms(string istanteIso) =>
        DateTimeOffset.Parse(istanteIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUnixTimeMilliseconds();

    [Fact]
    public void AllineaAlBucket_RiportaAllInizioDelMinuto()
    {
        long allineato = RollupMath.AlignToBucketStart(Ms("2026-08-26T12:03:47.812Z"), UnMinuto);

        Assert.Equal(Ms("2026-08-26T12:03:00Z"), allineato);
    }

    [Fact]
    public void AllineaAlBucket_LasciaFermoUnIstanteGiaAllineato()
    {
        // Se un istante esattamente sul bordo scivolasse al bucket precedente, ogni bucket
        // conterrebbe un campione del bucket successivo e le medie sarebbero tutte sfalsate
        // di un campione: sbagliate di poco, quindi invisibili.
        long allineato = RollupMath.AlignToBucketStart(Ms("2026-08-26T12:05:00Z"), CinqueMinuti);

        Assert.Equal(Ms("2026-08-26T12:05:00Z"), allineato);
    }

    [Fact]
    public void AllineaAlBucket_ArrotondaVersoIlPassatoAnchePrimaDellEpoch()
    {
        // Con la divisione intera del C# -1500 / 60000 fa 0, e un istante prima del 1970
        // finirebbe nel bucket SUCCESSIVO invece che nel precedente. Non capita in
        // produzione, ma e' il modo piu' economico di verificare che l'arrotondamento sia un
        // vero floor e non un troncamento verso lo zero.
        long allineato = RollupMath.AlignToBucketStart(-1500L, UnMinuto);

        Assert.Equal(-60000L, allineato);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public void AllineaAlBucket_RifiutaUnAmpiezzaNonPositiva(int millisecondi)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RollupMath.AlignToBucketStart(0L, TimeSpan.FromMilliseconds(millisecondi)));
    }

    [Fact]
    public void Aggrega_CalcolaConteggioSommaMinimoMassimoEUltimo()
    {
        RawSample[] campioni =
        [
            new(Ms("2026-08-26T12:00:00Z"), 10d),
            new(Ms("2026-08-26T12:00:01Z"), 30d),
            new(Ms("2026-08-26T12:00:02Z"), 20d),
        ];

        RollupBucket bucket = Assert.Single(RollupMath.Aggregate(campioni, UnMinuto));

        Assert.Equal(Ms("2026-08-26T12:00:00Z"), bucket.BucketStartMs);
        Assert.Equal(3, bucket.Count);
        Assert.Equal(60d, bucket.Sum);
        Assert.Equal(10d, bucket.Min);
        Assert.Equal(30d, bucket.Max);
        Assert.Equal(20d, bucket.Last);
        Assert.Equal(20d, bucket.Average);
    }

    [Fact]
    public void Aggrega_SeparaIBucketEliRestituisceInOrdineDiTempo()
    {
        RawSample[] campioni =
        [
            new(Ms("2026-08-26T12:01:30Z"), 5d),
            new(Ms("2026-08-26T12:00:30Z"), 1d),
            new(Ms("2026-08-26T12:00:31Z"), 3d),
        ];

        IReadOnlyList<RollupBucket> bucket = RollupMath.Aggregate(campioni, UnMinuto);

        Assert.Equal(2, bucket.Count);
        Assert.Equal(Ms("2026-08-26T12:00:00Z"), bucket[0].BucketStartMs);
        Assert.Equal(2, bucket[0].Count);
        Assert.Equal(Ms("2026-08-26T12:01:00Z"), bucket[1].BucketStartMs);
        Assert.Equal(1, bucket[1].Count);
    }

    [Fact]
    public void Aggrega_LUltimoEIlPiuRecenteNonLUltimoArrivato()
    {
        // I campioni arrivano gia' ordinati dal database, ma "ultimo" deve significare
        // "piu' recente" e non "ultimo della lista": altrimenti il giorno in cui qualcuno
        // toglie l'ORDER BY dalla query il valore corrente mostrato in dashboard diventa un
        // valore vecchio a caso, senza che nulla fallisca.
        RawSample[] campioniInDisordine =
        [
            new(Ms("2026-08-26T12:00:59Z"), 99d),
            new(Ms("2026-08-26T12:00:01Z"), 1d),
        ];

        RollupBucket bucket = Assert.Single(RollupMath.Aggregate(campioniInDisordine, UnMinuto));

        Assert.Equal(99d, bucket.Last);
    }

    [Fact]
    public void Aggrega_SenzaCampioniNonProduceBucket()
    {
        // Un bucket vuoto avrebbe conteggio zero e media 0/0 = NaN, e un NaN in JSON fa
        // fallire l'INTERA risposta HTTP, non solo quella metrica.
        Assert.Empty(RollupMath.Aggregate([], UnMinuto));
    }

    [Fact]
    public void Ricombina_LaMediaACinqueMinutiCoincideConLaMediaDeiGrezzi()
    {
        // IL test. Cinque minuti con un numero DIVERSO di campioni ciascuno: e' il caso
        // normale, non un caso limite — succede a ogni riavvio del servizio, a ogni timeout
        // di un collector e ogni volta che una metrica compare a meta' minuto. Chi conserva
        // la media invece di somma e conteggio calcola qui la media delle medie e ottiene un
        // numero credibile e falso.
        RawSample[] grezzi =
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

        IReadOnlyList<RollupBucket> minuti = RollupMath.Aggregate(grezzi, UnMinuto);
        RollupBucket cinqueMinuti = Assert.Single(RollupMath.Combine(minuti, CinqueMinuti));

        // Media vera: 100 / 8 = 12,5. Media delle medie: (100+0+0+0+0)/5 = 20.
        Assert.Equal(100d / 8d, cinqueMinuti.Average);
        Assert.Equal(8, cinqueMinuti.Count);
        Assert.Equal(100d, cinqueMinuti.Sum);
    }

    [Fact]
    public void Ricombina_PrendeGliEstremiNonLaLoroSomma()
    {
        RollupBucket[] minuti =
        [
            new(Ms("2026-08-26T12:00:00Z"), 60, 600d, 2d, 40d, 7d),
            new(Ms("2026-08-26T12:01:00Z"), 60, 600d, 5d, 90d, 9d),
        ];

        RollupBucket combinato = Assert.Single(RollupMath.Combine(minuti, CinqueMinuti));

        Assert.Equal(2d, combinato.Min);
        Assert.Equal(90d, combinato.Max);
    }

    [Fact]
    public void Ricombina_LUltimoVieneDalBucketPiuRecente()
    {
        RollupBucket[] minutiInDisordine =
        [
            new(Ms("2026-08-26T12:04:00Z"), 60, 600d, 1d, 20d, 42d),
            new(Ms("2026-08-26T12:00:00Z"), 60, 600d, 1d, 20d, 7d),
        ];

        RollupBucket combinato = Assert.Single(RollupMath.Combine(minutiInDisordine, CinqueMinuti));

        Assert.Equal(42d, combinato.Last);
        Assert.Equal(Ms("2026-08-26T12:00:00Z"), combinato.BucketStartMs);
    }

    [Fact]
    public void Ricombina_TieneSeparatiIBucketDiCinqueMinutiDiversi()
    {
        RollupBucket[] minuti =
        [
            new(Ms("2026-08-26T12:04:00Z"), 60, 60d, 1d, 1d, 1d),
            new(Ms("2026-08-26T12:05:00Z"), 60, 120d, 2d, 2d, 2d),
        ];

        IReadOnlyList<RollupBucket> combinati = RollupMath.Combine(minuti, CinqueMinuti);

        Assert.Equal(2, combinati.Count);
        Assert.Equal(Ms("2026-08-26T12:00:00Z"), combinati[0].BucketStartMs);
        Assert.Equal(Ms("2026-08-26T12:05:00Z"), combinati[1].BucketStartMs);
    }

    [Fact]
    public void Bucket_RifiutaUnConteggioNonPositivo()
    {
        // Un bucket a conteggio zero produce media NaN e fa saltare la serializzazione
        // dell'intera risposta. Meglio non lasciarlo nascere.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RollupBucket(0L, 0, 0d, 0d, 0d, 0d));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Bucket_RifiutaIValoriNonFiniti(double valoreRotto)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RollupBucket(0L, 1, valoreRotto, valoreRotto, valoreRotto, valoreRotto));
    }
}
