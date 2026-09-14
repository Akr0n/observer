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
    public void Defaults_AreTheDeclaredValues()
    {
        // Questo test non verifica un calcolo: fissa una SCELTA, per rendere evidente il
        // giorno in cui qualcuno la cambia senza dirlo. Sei ore di grezzo, sette giorni di
        // minuti, novanta giorni di cinque minuti.
        StorageOptions defaults = new();

        Assert.True(defaults.Enabled);
        Assert.Equal(TimeSpan.FromHours(6), defaults.RawRetention);
        Assert.Equal(TimeSpan.FromDays(7), defaults.MinuteRetention);
        Assert.Equal(TimeSpan.FromDays(90), defaults.FiveMinuteRetention);
    }

    [Fact]
    public void Validate_AcceptsTheDefaults()
    {
        new StorageOptions().Validate();
    }

    [Fact]
    public void ResolveDatabasePath_RelativePath_BecomesAbsoluteAndIgnoresTheCurrentDirectory()
    {
        // Un servizio di sistema non ha una cartella di lavoro prevedibile: su Windows parte
        // da system32, con systemd da / salvo direttive. Un percorso relativo produrrebbe un
        // database in un posto diverso a ogni modo di avvio, e in sviluppo lo pianta dentro
        // l'albero dei sorgenti. Deve risolversi sempre allo stesso posto.
        StorageOptions options = new() { DatabasePath = "observer.db" };

        string resolved = options.ResolveDatabasePath();

        Assert.True(Path.IsPathRooted(resolved));
        Assert.NotEqual(
            Path.GetFullPath("observer.db"),
            resolved);
        Assert.EndsWith("observer.db", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveDatabasePath_AlreadyAbsolutePath_IsLeftAsItIs()
    {
        // Chi indica un percorso esplicito ha le sue ragioni (un disco diverso, un volume di
        // dati): non va reinterpretato.
        string explicitPath = Path.Combine(Path.GetTempPath(), "observer-esplicito.db");
        StorageOptions options = new() { DatabasePath = explicitPath };

        Assert.Equal(explicitPath, options.ResolveDatabasePath());
    }

    [Fact]
    public void Validate_RejectsAGracePeriodShorterThanTheWriteQueue()
    {
        // Il buco che questo chiude: la coda puo' trattenere QueueCapacity campionamenti
        // (a 1 Hz, altrettanti secondi) prima che finiscano su disco, ma il consolidamento
        // considera chiuso un minuto dopo la sola grazia. Un campione che arriva dopo non
        // entra piu' nella media del suo minuto, e poco dopo il grezzo viene cancellato:
        // resta una media credibile calcolata su meta' dei campioni, senza eccezioni ne'
        // log. E' esattamente il genere di errore che nessuno puo' diagnosticare guardando
        // un grafico, quindi va impedito all'avvio.
        StorageOptions inconsistent = new()
        {
            QueueCapacity = 240,
            ConsolidationGrace = TimeSpan.FromSeconds(10),
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(inconsistent.Validate);

        Assert.Contains(nameof(StorageOptions.ConsolidationGrace), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(StorageOptions.QueueCapacity), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptsAGracePeriodThatCoversTheQueue()
    {
        StorageOptions consistent = new()
        {
            QueueCapacity = 60,
            ConsolidationGrace = TimeSpan.FromSeconds(60),
        };

        consistent.Validate();
    }

    [Fact]
    public void Validate_RejectsNonPositiveRawRetention()
    {
        StorageOptions options = new() { RawRetention = TimeSpan.Zero };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsNonPositiveMinuteRetention()
    {
        StorageOptions options = new() { MinuteRetention = TimeSpan.FromMinutes(-1) };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsANegativeGracePeriod()
    {
        StorageOptions options = new() { ConsolidationGrace = TimeSpan.FromSeconds(-1) };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsAnEmptyPath()
    {
        StorageOptions options = new() { DatabasePath = "   " };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsAQueueWithNoRoom()
    {
        StorageOptions options = new() { QueueCapacity = 0 };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsANonPositivePointLimit()
    {
        StorageOptions options = new() { MaxHistoryPoints = 0 };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsANonPositiveMaintenanceInterval()
    {
        StorageOptions options = new() { MaintenanceInterval = TimeSpan.Zero };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
