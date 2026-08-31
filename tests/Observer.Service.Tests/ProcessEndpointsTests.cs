using System.Net;
using System.Text.Json;

namespace Observer.Service.Tests;

/// <summary>
/// Gli endpoint dei processi, sul servizio vero avviato in memoria.
/// </summary>
/// <remarks>
/// Qui c'e' l'unica cosa che questo servizio fa e non e' una lettura, e le verifiche che
/// contano sono proprio quelle: che senza token non si arrivi a <c>/processes</c>, e che
/// <c>kill</c> su un PID che non esiste risponda "non c'e'" invece di far cadere qualcos'altro.
/// <para>
/// Il percorso in cui un processo viene terminato DAVVERO non e' coperto, ed e' una scelta:
/// un test che uccide un processo su una macchina di sviluppo o su un runner della CI puo'
/// colpire qualcosa che serve, e l'unica parte nostra di quel percorso — trovare il processo
/// dal PID e chiedere al sistema di fermarlo — sono due chiamate della libreria standard. Il
/// rischio vero non e' che Kill non funzioni: e' che si fermi il processo sbagliato, e quello
/// dipende dal PID che arriva nella richiesta.
/// </para>
/// </remarks>
[Collection(AmbienteDelProcesso.Nome)]
public class ProcessEndpointsTests
{
    private readonly ServizioInMemoria servizio;

    public ProcessEndpointsTests(ServizioInMemoria servizio)
    {
        this.servizio = servizio;
    }

    [Theory]
    [InlineData("/processes")]
    [InlineData("/processes?by=memory")]
    public async Task SenzaTokenNonSiVedeChiGiraSullaMacchina(string percorso)
    {
        // L'elenco dei processi dice molto piu' di una percentuale di CPU: dice quali
        // programmi usa chi sta a quella macchina. Un endpoint aggiunto fuori dal middleware
        // lo regalerebbe a chiunque sia sulla rete.
        using HttpClient anonimo = servizio.CreateClient();

        using HttpResponseMessage risposta = await anonimo.GetAsync(new Uri(percorso, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, risposta.StatusCode);
    }

    [Fact]
    public async Task SenzaTokenNonSiTerminaNiente()
    {
        using HttpClient anonimo = servizio.CreateClient();

        using HttpResponseMessage risposta = await anonimo.PostAsync(
            new Uri("/processes/999999/kill", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, risposta.StatusCode);
    }

    [Fact]
    public async Task LElencoContieneAlmenoIlProcessoCheStaRispondendo()
    {
        using HttpClient client = servizio.CreateAuthorizedClient();

        using HttpResponseMessage risposta = await client.GetAsync(new Uri("/processes", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, risposta.StatusCode);

        using JsonDocument documento = JsonDocument.Parse(await risposta.Content.ReadAsStringAsync());
        JsonElement processi = documento.RootElement.GetProperty("processes");

        Assert.True(
            processi.GetArrayLength() > 0,
            "l'elenco dei processi e' vuoto sulla macchina che lo sta servendo");

        JsonElement primo = processi[0];
        Assert.True(primo.GetProperty("pid").GetInt32() > 0);
        Assert.False(string.IsNullOrWhiteSpace(primo.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task LOrdinePerMemoriaMetteIPiuIngombrantiInCima()
    {
        using HttpClient client = servizio.CreateAuthorizedClient();

        using HttpResponseMessage risposta = await client.GetAsync(
            new Uri("/processes?by=memory&top=5", UriKind.Relative));

        using JsonDocument documento = JsonDocument.Parse(await risposta.Content.ReadAsStringAsync());
        JsonElement processi = documento.RootElement.GetProperty("processes");

        Assert.True(processi.GetArrayLength() <= 5);

        long precedente = long.MaxValue;

        foreach (JsonElement processo in processi.EnumerateArray())
        {
            long adesso = processo.GetProperty("workingSetBytes").GetInt64();
            Assert.True(adesso <= precedente, "l'elenco per memoria non e' in ordine decrescente");
            precedente = adesso;
        }
    }

    [Fact]
    public async Task TerminareUnPidCheNonEsisteRispondeNonTrovato()
    {
        using HttpClient client = servizio.CreateAuthorizedClient();

        // Un PID cosi' alto non e' assegnabile su nessuno dei due sistemi: il caso e' "non
        // c'e'", e la risposta giusta e' dirlo, non un errore del server.
        using HttpResponseMessage risposta = await client.PostAsync(
            new Uri("/processes/2147483646/kill", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.NotFound, risposta.StatusCode);
    }
}
