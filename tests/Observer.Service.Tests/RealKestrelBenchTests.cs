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
        // Without Sources.Clear() the bench would read the appsettings.json copied into the test
        // output and would try to bind 5057, colliding with the installed service.
        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.Listen(IPAddress.Loopback, 0));

        Assert.DoesNotContain(
            bench.Addresses,
            address => address.Contains("5057", StringComparison.Ordinal));
    }
}