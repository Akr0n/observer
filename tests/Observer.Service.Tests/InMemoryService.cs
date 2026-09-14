using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// The real service, started in memory, with a temporary database all of its own.
/// </summary>
/// <remarks>
/// The configuration goes through ENVIRONMENT VARIABLES and not through UseSetting because
/// Program.cs reads the token BEFORE building the host: anything the test adds at Build
/// time would arrive too late, and the service would refuse to start.
/// </remarks>
public sealed class InMemoryService : WebApplicationFactory<Program>
{
    /// <summary>The token the tests authenticate with.</summary>
    public const string Token = "test-token";

    private readonly string directory;

    // What was in each variable touched BEFORE, to put it back on the way out. Environment
    // variables belong to the PROCESS, not to this instance: leaving them behind means that
    // whoever runs next starts with a token and a database path they did not choose, and a fault
    // like that shows up at random on one CI runner and not on the other.
    private readonly List<(string Name, string? Previous)> savedVariables = [];

    /// <summary>Prepares the temporary folder and the service configuration.</summary>
    public InMemoryService()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "observer-http-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(directory, "history.db");

        SetVariable("Observer__ApiToken", Token);
        SetVariable("Observer__Storage__DatabasePath", DatabasePath);

        // HTTPS off: the transport here is fake, because WebApplicationFactory replaces
        // Kestrel with a TestServer. Generating a 3072-bit RSA key and trying to store it
        // in a system folder would cost seconds on every instance, for a port that will
        // never be opened. Real TLS has its own test class: HttpsTransportTests.
        SetVariable("Observer__Network__Https", "false");

        // Maintenance must not start on its own during the tests: it would consolidate and
        // purge out from under the assertions.
        SetVariable("Observer__Storage__MaintenanceInterval", "01:00:00");
    }

    /// <summary>Path of the database used by this instance of the service.</summary>
    public string DatabasePath { get; }

    /// <summary>A client that is already authenticated.</summary>
    /// <returns>The client.</returns>
    public HttpClient CreateAuthorizedClient()
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        return client;
    }

    /// <summary>The service's store, for seeding predictable data.</summary>
    /// <returns>The store registered in the container.</returns>
    public MetricStore Store() => Services.GetRequiredService<MetricStore>();

    /// <summary>Sets an environment variable, remembering what was there before.</summary>
    private void SetVariable(string name, string? value)
    {
        savedVariables.Add((name, Environment.GetEnvironmentVariable(name)));
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        // After base.Dispose: the host is stopped, so nobody will read the configuration again.
        // Before deleting the folder, which can fail: the variables have to be put back
        // either way.
        foreach ((string name, string? previous) in savedVariables)
        {
            Environment.SetEnvironmentVariable(name, previous);
        }

        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
