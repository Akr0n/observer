using System.Net;

namespace Observer.Service.Tests;

/// <summary>Il banco stesso funziona: senza questo, i fallimenti dei test seguenti sono ambigui.</summary>
[Collection(AmbienteDelProcesso.Nome)]
public class BancoKestrelRealeTests
{
    [Fact]
    public async Task IlBancoAvviaUnKestrelVeroSuUnaPortaEffimera()
    {
        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.Listen(IPAddress.Loopback, 0));

        string indirizzo = Assert.Single(banco.Indirizzi);

        using HttpClient client = new() { BaseAddress = new Uri(indirizzo) };

        Assert.Equal("pong", await client.GetStringAsync("ping", CancellationToken.None));
    }

    [Fact]
    public async Task IlBancoNonEreditaLaConfigurazioneDelServizioVero()
    {
        // Senza Sources.Clear() il banco leggerebbe l'appsettings.json copiato nell'output dei
        // test e proverebbe a legare la 5057, scontrandosi con il servizio installato.
        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.Listen(IPAddress.Loopback, 0));

        Assert.DoesNotContain(
            banco.Indirizzi,
            indirizzo => indirizzo.Contains("5057", StringComparison.Ordinal));
    }
}