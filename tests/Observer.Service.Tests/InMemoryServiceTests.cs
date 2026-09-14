namespace Observer.Service.Tests;

/// <summary>
/// Groups the tests that touch GLOBAL process state.
/// </summary>
/// <remarks>
/// xunit runs classes that do not declare a collection in parallel, and these tests write
/// environment variables and clear the SQLite pools: two things that belong not to a test but to
/// the whole process. Without this collection, the bench the local channel will have to build
/// (real Kestrel hosts, pipe names, socket paths) would read the variables set by another class
/// halfway through its own run, and the fault would show up at random on one CI runner and not
/// on the other.
/// </remarks>
/// <para>
/// The collection also carries <see cref="InMemoryService"/>, so there is only ONE in-memory
/// service for all the classes that use it. With a fixture per class there were two, and on
/// Linux the second would not start: the local channel creates <c>/run/user/N/observer/</c>
/// at start-up and removes it at shutdown, so the first bench to finish took the folder away
/// from the second, which failed with "Could not find file ... observer.sock". With no host
/// nobody called <c>MetricStore.Initialize()</c>, and the history tests died with
/// "no such table: series" — a message that does not name the cause even distantly.
/// On Windows it was invisible: a named pipe has no folder to remove.
/// </para>
[CollectionDefinition(Name)]
public sealed class ProcessEnvironment : ICollectionFixture<InMemoryService>
{
    /// <summary>The name of the collection, so it is not repeated as a string all over.</summary>
    public const string Name = "ambiente-del-processo";
}

/// <summary>
/// Tests on the bench itself: if the bench dirties the process, it dirties everyone else's tests.
/// </summary>
[Collection(ProcessEnvironment.Name)]
public class InMemoryServiceTests
{
    [Fact]
    public void AfterDispose_EnvironmentVariablesAreRestored()
    {
        // The fixture configures the service from environment variables because Program.cs reads
        // the token BEFORE building the host: a forced choice, not a defect. The defect is not
        // putting them back, because those variables outlive the fixture and are left on
        // whoever runs next.
        string? tokenBefore = Environment.GetEnvironmentVariable("Observer__ApiToken");
        string? databaseBefore = Environment.GetEnvironmentVariable("Observer__Storage__DatabasePath");
        string? maintenanceBefore = Environment.GetEnvironmentVariable("Observer__Storage__MaintenanceInterval");

        using (InMemoryService service = new())
        {
            Assert.Equal(InMemoryService.Token, Environment.GetEnvironmentVariable("Observer__ApiToken"));
            Assert.Equal(service.DatabasePath, Environment.GetEnvironmentVariable("Observer__Storage__DatabasePath"));
        }

        Assert.Equal(tokenBefore, Environment.GetEnvironmentVariable("Observer__ApiToken"));
        Assert.Equal(databaseBefore, Environment.GetEnvironmentVariable("Observer__Storage__DatabasePath"));
        Assert.Equal(maintenanceBefore, Environment.GetEnvironmentVariable("Observer__Storage__MaintenanceInterval"));
    }
}