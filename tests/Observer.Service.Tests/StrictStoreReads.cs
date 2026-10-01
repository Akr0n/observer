using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// The tests that turn the strict read on for the whole process, and so must run alone.
/// </summary>
/// <remarks>
/// <see cref="StoreFile.StrictSwitch"/> is an <see cref="AppContext"/> switch: one value for the
/// process. The existing process-environment collection does not isolate anything from the tests of
/// other collections, so a test that flipped the switch there would change what a neighbour reads.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StrictStoreReads
{
    /// <summary>The name of the collection, so it is not repeated as a string all over.</summary>
    public const string Name = "strict-store-reads";
}

/// <summary>Turns the strict read on for the process, and puts it back.</summary>
public sealed class StrictScope : IDisposable
{
    /// <summary>Turns it on.</summary>
    public StrictScope() => AppContext.SetSwitch(StoreFile.StrictSwitch, true);

    /// <summary>Turns it off again.</summary>
    public void Dispose() => AppContext.SetSwitch(StoreFile.StrictSwitch, false);
}

/// <summary>Runs a read that could wait for ever, and stops waiting for it.</summary>
public static class Patience
{
    /// <summary>How long a read that should return at once is given.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    /// <summary>Runs <paramref name="read"/> and fails the test if it is still waiting after 5 seconds.</summary>
    /// <typeparam name="T">What the read returns.</typeparam>
    /// <param name="read">The read.</param>
    /// <param name="fifo">A FIFO the read may be stuck on: it is opened for writing to free it.</param>
    /// <returns>What the read returned.</returns>
    /// <remarks>
    /// A regression then costs five seconds and a red test, never a held runner. Opening a FIFO
    /// read-write never blocks, and it is what lets the stuck open() of the read finish.
    /// </remarks>
    public static async Task<T> Patiently<T>(Func<T> read, string? fifo = null)
    {
        Task<T> task = Task.Run(read);

        try
        {
            return await task.WaitAsync(Limit);
        }
        catch (TimeoutException)
        {
            if (fifo is not null)
            {
                using FileStream release = new(fifo, FileMode.Open, FileAccess.ReadWrite);
            }

            Assert.Fail("The read did not return within 5 seconds: it is waiting on the FIFO.");

            throw;
        }
    }
}