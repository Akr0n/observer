using System.Net;

namespace Observer.Service.Tests;

/// <summary>Il banco stesso funziona: senza questo, i fallimenti dei test seguenti sono ambigui.</summary>
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
        // Senza Sources.Clear() il banco leggerebbe l'appsettings.json copiato nell'output dei
        // test e proverebbe a legare la 5057, scontrandosi con il servizio installato.
        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.Listen(IPAddress.Loopback, 0));

        Assert.DoesNotContain(
            bench.Addresses,
            address => address.Contains("5057", StringComparison.Ordinal));
    }
}