using System.Globalization;
using Microsoft.Data.Sqlite;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// A real database, in a real file, in a different temporary folder for every test.
/// </summary>
/// <remarks>
/// Deliberately NOT in memory: half the things worth checking here — the WAL journal,
/// UNIQUE indexes, upsert, the size of the file — depend on the database really being on
/// disk. An in-memory test would take them all on trust.
/// </remarks>
internal sealed class TempMetricStore : IDisposable
{
    private readonly string directory;

    public TempMetricStore()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "observer-test-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(directory, "storico.db");
        Store = new MetricStore(DatabasePath);
        Store.Initialize();
    }

    public MetricStore Store { get; }

    public string DatabasePath { get; }

    public void Dispose()
    {
        // Without this, the pooled connections stay open and on Windows the file cannot be
        // deleted: the tests would pass all the same, leaving behind a temporary folder
        // for every run.
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Opportunistic cleanup: a file still held open must not fail a test that has
            // already verified what it set out to verify.
        }
        catch (UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }
}
