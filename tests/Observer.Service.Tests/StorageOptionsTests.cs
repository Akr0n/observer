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
    public void ResolveDatabasePath_PercorsoRelativo_DiventaAssolutoENonDipendeDallaCartellaCorrente()
    {
        // Un servizio di sistema non ha una cartella di lavoro prevedibile: su Windows parte
        // da system32, con systemd da / salvo direttive. Un percorso relativo produrrebbe un
        // database in un posto diverso a ogni modo di avvio, e in sviluppo lo pianta dentro
        // l'albero dei sorgenti. Deve risolversi sempre allo stesso posto.
        StorageOptions opzioni = new() { DatabasePath = "observer.db" };

        string risolto = opzioni.ResolveDatabasePath();

        Assert.True(Path.IsPathRooted(risolto));
        Assert.NotEqual(
            Path.GetFullPath("observer.db"),
            risolto);
        Assert.EndsWith("observer.db", risolto, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveDatabasePath_PercorsoGiaAssoluto_RestaComeE()
    {
        // Chi indica un percorso esplicito ha le sue ragioni (un disco diverso, un volume di
        // dati): non va reinterpretato.
        string esplicito = Path.Combine(Path.GetTempPath(), "observer-esplicito.db");
        StorageOptions opzioni = new() { DatabasePath = esplicito };

        Assert.Equal(esplicito, opzioni.ResolveDatabasePath());
    }

    [Fact]
    public void Convalida_RifiutaUnaGraziaPiuCortaDellaCodaDiScrittura()
    {
        // Il buco che questo chiude: la coda puo' trattenere QueueCapacity campionamenti
        // (a 1 Hz, altrettanti secondi) prima che finiscano su disco, ma il consolidamento
        // considera chiuso un minuto dopo la sola grazia. Un campione che arriva dopo non
        // entra piu' nella media del suo minuto, e poco dopo il grezzo viene cancellato:
        // resta una media credibile calcolata su meta' dei campioni, senza eccezioni ne'
        // log. E' esattamente il genere di errore che nessuno puo' diagnosticare guardando
        // un grafico, quindi va impedito all'avvio.
        StorageOptions incoerenti = new()
        {
            QueueCapacity = 240,
            ConsolidationGrace = TimeSpan.FromSeconds(10),
        };

        InvalidOperationException errore = Assert.Throws<InvalidOperationException>(incoerenti.Validate);

        Assert.Contains(nameof(StorageOptions.ConsolidationGrace), errore.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(StorageOptions.QueueCapacity), errore.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Convalida_AccettaUnaGraziaCheCopreLaCoda()
    {
        StorageOptions coerenti = new()
        {
            QueueCapacity = 60,
            ConsolidationGrace = TimeSpan.FromSeconds(60),
        };

        coerenti.Validate();
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
