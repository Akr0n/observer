using System.Globalization;
using Microsoft.Data.Sqlite;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// Un database vero, su file vero, in una cartella temporanea diversa per ogni test.
/// </summary>
/// <remarks>
/// Deliberatamente NON in memoria: meta' delle cose che si vogliono verificare qui —
/// giornale WAL, indici UNIQUE, upsert, dimensione del file — dipendono dal fatto che il
/// database stia davvero su disco. Un test in memoria le darebbe tutte per buone.
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
        // Senza questo, le connessioni del pool restano aperte e su Windows il file non si
        // puo' cancellare: i test passerebbero comunque, lasciando dietro una cartella
        // temporanea per ogni esecuzione.
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Pulizia opportunistica: un file ancora agganciato non deve far fallire un test
            // che ha gia' verificato quello che doveva verificare.
        }
        catch (UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }
}
