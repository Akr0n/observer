namespace Observer.Service.Persistence;

/// <summary>
/// History configuration. Every duration in here is a CHOICE, not a truth: they are exposed
/// precisely so whoever installs the service can change them without touching the code.
/// </summary>
public sealed class StorageOptions
{
    /// <summary>Configuration section this is read from.</summary>
    public const string SectionName = "Observer:Storage";

    /// <summary>If false the service works exactly as before, writing nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Path of the file. If relative, it is resolved by <see cref="ResolveDatabasePath"/>
    /// under the user's data directory, NEVER against the process working directory.
    /// The *.db, *.db-wal and *.db-shm files are already excluded from git.
    /// </summary>
    public string DatabasePath { get; set; } = "observer.db";

    /// <summary>
    /// How long the one-second sampling is kept. It is the parameter that decides how much the
    /// file grows: at 1 Hz the raw level is about 3600 rows an hour PER SERIES.
    /// </summary>
    public TimeSpan RawRetention { get; set; } = TimeSpan.FromHours(6);

    /// <summary>How long the one-minute buckets are kept.</summary>
    public TimeSpan MinuteRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How long the five-minute buckets are kept.</summary>
    public TimeSpan FiveMinuteRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How often consolidation and deletion run.</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait after a bucket closes before consolidating it. It covers the time a
    /// sample spends in the in-memory queue before reaching the disk.
    /// </summary>
    /// <remarks>
    /// It must cover the time a sampling can spend in the queue before reaching the disk,
    /// otherwise a late sample never enters the average of its own interval and shortly
    /// afterwards the raw level is deleted: what is left is a credible number computed over half
    /// the samples. The default is aligned with <see cref="QueueCapacity"/>, which at 1 Hz is
    /// worth that many seconds, and the consistency between the two is enforced by
    /// <see cref="Validate"/>.
    /// </remarks>
    public TimeSpan ConsolidationGrace { get; set; } = TimeSpan.FromSeconds(240);

    /// <summary>
    /// How much history is consolidated at most in a single pass. It is needed after a long
    /// stop: without this limit the first pass would try to aggregate hours of data in a single
    /// transaction.
    /// </summary>
    public TimeSpan MaxSpanPerPass { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How many snapshots can wait in the queue before the oldest ones are dropped.
    /// The queue exists because the 1 Hz sampler must NEVER wait for the disk.
    /// </summary>
    public int QueueCapacity { get; set; } = 240;

    /// <summary>How many points at most a single query can return.</summary>
    public int MaxHistoryPoints { get; set; } = 5000;

    /// <summary>
    /// The absolute path of the database. An already absolute path is honoured; a relative one
    /// is resolved under the user's data directory, NEVER against the working directory.
    /// </summary>
    /// <remarks>
    /// A system service has no predictable working directory: on Windows it starts from
    /// system32, under systemd from "/" unless explicitly directed otherwise. With a relative
    /// path the database would end up in a different place depending on how the service was
    /// started — and in development inside the source tree — giving the impression that the
    /// history was lost every time the way it is started changes.
    /// </remarks>
    /// <returns>The absolute path of the SQLite file.</returns>
    public string ResolveDatabasePath()
    {
        if (Path.IsPathRooted(DatabasePath))
        {
            return DatabasePath;
        }

        // LocalApplicationData is writable both by a user and by a service account, on both
        // platforms: %LOCALAPPDATA% on Windows, ~/.local/share on Linux.
        // /var/lib would be more orthodox for a Linux system service, but it requires
        // privileges we do not want to demand here.
        string baseDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        return Path.Combine(baseDirectory, "Observer", DatabasePath);
    }

    /// <summary>
    /// Checks the configuration at start-up. It fails immediately and loudly: a retention of
    /// zero would break nothing, it would only delete the whole history in silence.
    /// </summary>
    /// <exception cref="InvalidOperationException">If a value is not usable.</exception>
    public void Validate()
    {
        RequirePositive(RawRetention, nameof(RawRetention));
        RequirePositive(MinuteRetention, nameof(MinuteRetention));
        RequirePositive(FiveMinuteRetention, nameof(FiveMinuteRetention));
        RequirePositive(MaintenanceInterval, nameof(MaintenanceInterval));
        RequirePositive(MaxSpanPerPass, nameof(MaxSpanPerPass));

        if (ConsolidationGrace < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"{SectionName}:{nameof(ConsolidationGrace)} cannot be negative."));
        }

        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"{SectionName}:{nameof(DatabasePath)} cannot be empty."));
        }

        if (QueueCapacity < 1)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"{SectionName}:{nameof(QueueCapacity)} must be at least 1."));
        }

        if (MaxHistoryPoints < 1)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"{SectionName}:{nameof(MaxHistoryPoints)} must be at least 1."));
        }

        // The queue can hold QueueCapacity samplings — at 1 Hz, that many seconds — before they
        // reach the disk. If consolidation closes an interval before those samples have arrived,
        // they never enter it again and shortly afterwards the raw level is deleted: what is
        // left is a credible average computed over part of the samples, with no exception and no
        // log. It is a mistake nobody can see by looking at a chart, so it has to be prevented
        // here, at start-up, where it is noticed at once.
        TimeSpan worstCaseQueueDelay = TimeSpan.FromSeconds(QueueCapacity);

        if (ConsolidationGrace < worstCaseQueueDelay)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"{SectionName}:{nameof(ConsolidationGrace)} is {ConsolidationGrace.TotalSeconds:0} s but must be at least {worstCaseQueueDelay.TotalSeconds:0} s, because {SectionName}:{nameof(QueueCapacity)} is {QueueCapacity} and a sample can wait that long before it is written to disk. A shorter grace period would leave late samples out of their own average."));
        }
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"{SectionName}:{name} must be positive, but is {value}."));
        }
    }
}
