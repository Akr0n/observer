using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// La configurazione dello storico. Ogni valore sbagliato qui dentro produce un servizio che
/// parte, gira, non lancia e non conserva niente: e' il guasto che nessuno nota finche' non
/// gli serve lo storico.
/// </summary>
public class StorageOptionsTests
{
    [Fact]
    public void Predefiniti_SonoQuelliDichiarati()
    {
        // Questo test non verifica un calcolo: fissa una SCELTA, per rendere evidente il
        // giorno in cui qualcuno la cambia senza dirlo. Sei ore di grezzo, sette giorni di
        // minuti, novanta giorni di cinque minuti.
        StorageOptions predefinite = new();

        Assert.True(predefinite.Enabled);
        Assert.Equal(TimeSpan.FromHours(6), predefinite.RawRetention);
        Assert.Equal(TimeSpan.FromDays(7), predefinite.MinuteRetention);
        Assert.Equal(TimeSpan.FromDays(90), predefinite.FiveMinuteRetention);
    }

    [Fact]
    public void Convalida_AccettaIPredefiniti()
    {
        new StorageOptions().Validate();
    }

    [Fact]
    public void Convalida_RifiutaUnaRitenzioneDelGrezzoNonPositiva()
    {
        StorageOptions opzioni = new() { RawRetention = TimeSpan.Zero };

        Assert.Throws<InvalidOperationException>(opzioni.Validate);
    }

    [Fact]
    public void Convalida_RifiutaUnaRitenzioneDeiMinutiNonPositiva()
    {
        StorageOptions opzioni = new() { MinuteRetention = TimeSpan.FromMinutes(-1) };

        Assert.Throws<InvalidOperationException>(opzioni.Validate);
    }

    [Fact]
    public void Convalida_RifiutaUnaGraziaNegativa()
    {
        StorageOptions opzioni = new() { ConsolidationGrace = TimeSpan.FromSeconds(-1) };

        Assert.Throws<InvalidOperationException>(opzioni.Validate);
    }

    [Fact]
    public void Convalida_RifiutaUnPercorsoVuoto()
    {
        StorageOptions opzioni = new() { DatabasePath = "   " };

        Assert.Throws<InvalidOperationException>(opzioni.Validate);
    }

    [Fact]
    public void Convalida_RifiutaUnaCodaSenzaPosti()
    {
        StorageOptions opzioni = new() { QueueCapacity = 0 };

        Assert.Throws<InvalidOperationException>(opzioni.Validate);
    }

    [Fact]
    public void Convalida_RifiutaUnLimiteDiPuntiNonPositivo()
    {
        StorageOptions opzioni = new() { MaxHistoryPoints = 0 };

        Assert.Throws<InvalidOperationException>(opzioni.Validate);
    }

    [Fact]
    public void Convalida_RifiutaUnIntervalloDiManutenzioneNonPositivo()
    {
        StorageOptions opzioni = new() { MaintenanceInterval = TimeSpan.Zero };

        Assert.Throws<InvalidOperationException>(opzioni.Validate);
    }
}
