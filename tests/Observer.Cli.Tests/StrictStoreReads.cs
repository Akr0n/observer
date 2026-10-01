using System.Diagnostics;
using Observer.Service.Credentials;

namespace Observer.Cli.Tests;

/// <summary>
/// The tests that turn the strict read on for the whole process, and so must run alone.
/// </summary>
/// <remarks>
/// The same collection the service tests have, for the same reason: the switch is one value for the
/// process, and Console is redirected while they run. The two test projects cannot share a file, so
/// the small helpers below are written twice.
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

/// <summary>A fact that needs root, and is SKIPPED for anyone else.</summary>
/// <remarks>
/// GitHub's runner is not root for the test step itself, so what this guards is run as root by a
/// step of its own (see build.yml) and by hand in a container.
/// </remarks>
public sealed class RootOnlyFactAttribute : FactAttribute
{
    /// <summary>Skips unless this is Linux and the process is root.</summary>
    public RootOnlyFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess)
        {
            Skip = "Needs root: run by its own step in CI and by hand as root.";
        }
    }
}

/// <summary>A fact about an account that is NOT root, SKIPPED for root (which no mode keeps out).</summary>
public sealed class UnprivilegedFactAttribute : FactAttribute
{
    /// <summary>Skips off Linux, and as root.</summary>
    public UnprivilegedFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || Environment.IsPrivilegedProcess)
        {
            Skip = "Needs an account that is not root: a mode of 000 does not keep root out.";
        }
    }
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

            Assert.Fail("The call did not return within 5 seconds: it is still waiting.");

            throw;
        }
    }
}

/// <summary>Runs a system tool the tests need to set a file up.</summary>
public static class Tool
{
    /// <summary>Runs it and fails the test if it does not exit with 0.</summary>
    /// <param name="name">The tool.</param>
    /// <param name="arguments">Its arguments.</param>
    public static void Run(string name, params string[] arguments)
    {
        ProcessStartInfo start = new(name) { RedirectStandardError = true };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(name + " did not start.");

        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, name + " failed: " + error);
    }
}