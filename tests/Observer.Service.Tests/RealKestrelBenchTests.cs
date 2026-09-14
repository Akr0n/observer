using System.Net;

namespace Observer.Service.Tests;

/// <summary>The bench itself works: without this, the failures of the tests that follow are ambiguous.</summary>
[Collection(ProcessEnvironment.Name)]
public class RealKestrelBenchTests
{
    [Fact]
    public async Task TheBenchStartsARealKestrelOnAnEphemeralPort()
    {
        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.Listen(IPAddress.Loopback, 0));

        string address = Assert.Single(bench.Addresses);

        using HttpClient client = new() { BaseAddress = new Uri(address) };

        Assert.Equal("pong", await client.GetStringAsync("ping", CancellationToken.None));
    }

    [Fact]
    public async Task TheBenchDoesNotInheritTheRealServiceConfiguration()
    {
        // The test output carries Observer.Service's appsettings.json, copied there by the
        // project reference. Without Configuration.Sources.Clear() the bench reads it and
        // starts up with the real service's storage path, pipe name and port.
        //
        // This used to assert that no address contained "5057", and that assertion had stopped
        // guarding anything: the cleartext Kestrel endpoint was removed from appsettings.json
        // in 0.21.0, so "5057" is in no file the bench could inherit, and the listener is an
        // ephemeral port this test asks for by hand. Deleting Sources.Clear() left both tests
        // in this class green. Asserting on the CONFIGURATION instead of on the resulting
        // addresses is what makes the line it protects load-bearing again.
        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.Listen(IPAddress.Loopback, 0));

        Assert.Null(bench.Configuration["Observer:Network:HttpsPort"]);
        Assert.Null(bench.Configuration["Observer:LocalChannel:PipeName"]);
        Assert.Null(bench.Configuration["Observer:Storage:DatabasePath"]);
    }
}