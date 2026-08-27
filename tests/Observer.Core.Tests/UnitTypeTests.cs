using Observer.Core.Units;

namespace Observer.Core.Tests;

/// <summary>
/// Le unita' di misura sono le fondamenta: ogni metrica di RAM passa da ByteSize e ogni
/// percentuale da Percent. Un errore qui produce numeri credibili ma sbagliati, che e' la
/// categoria di bug piu' costosa in una dashboard.
/// </summary>
public class UnitTypeTests
{
    [Fact]
    public void ByteSize_FromKibibytes_MoltiplicaPer1024NonPer1000()
    {
        // /proc/meminfo scrive "kB" ma intende KiB (1024 byte). Chi legge "kB" e moltiplica
        // per 1000 ottiene numeri plausibili e sbagliati del 2,4%: nessun crash, nessun
        // allarme, solo una dashboard che mente.
        ByteSize quattroKiB = ByteSize.FromKibibytes(4);

        Assert.Equal(4096L, quattroKiB.Bytes);
    }

    [Fact]
    public void ByteSize_SaturatingSubtract_NonDiventaNegativa()
    {
        // Su alcune VM "available" supera momentaneamente "total". Senza saturazione
        // l'usato diventerebbe negativo e il grafico impazzirebbe senza alcun errore.
        ByteSize dieci = ByteSize.FromBytes(10);
        ByteSize novantanove = ByteSize.FromBytes(99);

        ByteSize risultato = dieci.SaturatingSubtract(novantanove);

        Assert.Equal(0L, risultato.Bytes);
    }

    [Fact]
    public void Percent_TryFromRatio_RifiutaNaN()
    {
        // Un NaN serializzato fa lanciare Utf8JsonWriter e azzera l'INTERA risposta HTTP
        // per colpa di una sola metrica. Va rifiutato alla fonte, non a valle.
        bool riuscito = Percent.TryFromRatio(double.NaN, out Percent _);

        Assert.False(riuscito);
    }

    [Fact]
    public void Percent_TryFromRatio_RifiutaIRapportiNegativi()
    {
        // Una percentuale d'uso negativa non significa nulla e a grafico passa per rumore.
        // Il tipo deve imporre il proprio contratto, altrimenti ogni collector futuro
        // eredita la stessa trappola: qui si chiude una volta per tutti.
        bool riuscito = Percent.TryFromRatio(-0.5, out Percent _);

        Assert.False(riuscito);
    }

    [Fact]
    public void Percent_TryFromRatio_AccettaOltreIlCento()
    {
        // Il limite superiore NON va imposto, ed e' una scelta deliberata: un futuro
        // collector per-processo su una macchina multi-core deve poter dire 350%, che e' un
        // valore legittimo e non un errore. Vincolare solo il basso.
        bool riuscito = Percent.TryFromRatio(3.5, out Percent tantissimo);

        Assert.True(riuscito);
        Assert.Equal(350.0, tantissimo.Points);
    }

    [Fact]
    public void Percent_TryFromRatio_ConverteIlRapportoInPuntiPercentuali()
    {
        // Il rapporto 0..1 e i punti 0..100 sono la confusione piu' comune: 0,5 deve
        // diventare 50, non 0,5.
        bool riuscito = Percent.TryFromRatio(0.5, out Percent mezzo);

        Assert.True(riuscito);
        Assert.Equal(50.0, mezzo.Points);
    }
}