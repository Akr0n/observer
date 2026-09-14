using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// The history configuration. Every wrong value in here produces a service that starts, runs,
/// throws nothing and keeps nothing: the fault nobody notices until they need the history.
/// </summary>
public class StorageOptionsTests
{
    [Fact]
    public void Defaults_AreTheDeclaredValues()
    {
        // This test checks no calculation: it pins a CHOICE, so the day someone changes it
        // without saying so is obvious. Six hours of raw, seven days of minutes, ninety days
        // of five-minute buckets.
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
        // A system service has no predictable working directory: on Windows it starts from
        // system32, under systemd from / unless told otherwise. A relative path would put the
        // database somewhere different for every way of starting it, and in development it
        // drops it inside the source tree. It must always resolve to the same place.
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
        // Whoever gives an explicit path has their reasons (a different disk, a data volume):
        // it must not be reinterpreted.
        string explicitPath = Path.Combine(Path.GetTempPath(), "observer-explicit.db");
        StorageOptions options = new() { DatabasePath = explicitPath };

        Assert.Equal(explicitPath, options.ResolveDatabasePath());
    }

    [Fact]
    public void Validate_RejectsAGracePeriodShorterThanTheWriteQueue()
    {
        // The gap this closes: the buffer can hold QueueCapacity samples (at 1 Hz, that
        // many seconds) before they reach the disk, but consolidation treats a minute as
        // closed after the grace period alone. A sample that arrives later no longer enters
        // the average for its minute, and shortly afterwards the raw data is purged: what is
        // left is a believable average computed over half the samples, with no exception and
        // no log line. It is exactly the kind of error nobody can diagnose by looking at a
        // graph, so it has to be stopped at start-up.
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
