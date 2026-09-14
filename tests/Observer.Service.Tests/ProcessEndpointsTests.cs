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
[Collection(ProcessEnvironment.Name)]
public class ProcessEndpointsTests
{
    private readonly InMemoryService service;

    public ProcessEndpointsTests(InMemoryService service)
    {
        this.service = service;
    }

    [Theory]
    [InlineData("/processes")]
    [InlineData("/processes?by=memory")]
    public async Task TheProcessListIsRefusedWithoutAToken(string path)
    {
        // L'elenco dei processi dice molto piu' di una percentuale di CPU: dice quali
        // programmi usa chi sta a quella macchina. Un endpoint aggiunto fuori dal middleware
        // lo regalerebbe a chiunque sia sulla rete.
        using HttpClient anonymous = service.CreateClient();

        using HttpResponseMessage response = await anonymous.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NothingCanBeKilledWithoutAToken()
    {
        using HttpClient anonymous = service.CreateClient();

        using HttpResponseMessage response = await anonymous.PostAsync(
            new Uri("/processes/999999/kill", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheListIncludesAtLeastTheProcessServingTheRequest()
    {
        using HttpClient client = service.CreateAuthorizedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/processes", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement processes = document.RootElement.GetProperty("processes");

        Assert.True(
            processes.GetArrayLength() > 0,
            "l'elenco dei processi e' vuoto sulla macchina che lo sta servendo");

        JsonElement first = processes[0];
        Assert.True(first.GetProperty("pid").GetInt32() > 0);
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task OrderingByMemoryPutsTheBiggestFirst()
    {
        using HttpClient client = service.CreateAuthorizedClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/processes?by=memory&top=5", UriKind.Relative));

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement processes = document.RootElement.GetProperty("processes");

        Assert.True(processes.GetArrayLength() <= 5);

        long previous = long.MaxValue;

        foreach (JsonElement process in processes.EnumerateArray())
        {
            long current = process.GetProperty("workingSetBytes").GetInt64();
            Assert.True(current <= previous, "l'elenco per memoria non e' in ordine decrescente");
            previous = current;
        }
    }

    [Fact]
    public async Task KillingAPidThatDoesNotExistAnswersNotFound()
    {
        using HttpClient client = service.CreateAuthorizedClient();

        // Un PID cosi' alto non e' assegnabile su nessuno dei due sistemi: il caso e' "non
        // c'e'", e la risposta giusta e' dirlo, non un errore del server.
        using HttpResponseMessage response = await client.PostAsync(
            new Uri("/processes/2147483646/kill", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/processes", "cpu")]
    [InlineData("/processes?by=memory", "memory")]
    [InlineData("/processes?by=io", "io")]
    [InlineData("/processes?by=IO", "io")]
    [InlineData("/processes?by=boh", "cpu")]
    public async Task TheResponseEchoesTheCriterionItApplied(string path, string expected)
    {
        // Il client lo usa per accorgersi di un servizio che non conosce ancora "io": senza,
        // riceverebbe l'elenco della CPU e lo mostrerebbe sotto il titolo dell'I/O.
        using HttpClient client = service.CreateAuthorizedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, document.RootElement.GetProperty("by").GetString());
    }

    [Fact]
    public async Task OrderingByIoPutsTheBusiestFirstAndTheUnknownRatesLast()
    {
        using HttpClient client = service.CreateAuthorizedClient();
        Uri path = new("/processes?by=io&top=100", UriKind.Relative);

        // Due letture: alla prima non c'e' un campione precedente e ogni tasso e' ignoto.
        (await client.GetAsync(path)).Dispose();
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        using HttpResponseMessage response = await client.GetAsync(path);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        double previous = double.MaxValue;
        bool unknownRatesStarted = false;

        foreach (JsonElement process in document.RootElement.GetProperty("processes").EnumerateArray())
        {
            JsonElement rate = process.GetProperty("ioBytesPerSecond");

            if (rate.ValueKind == JsonValueKind.Null)
            {
                unknownRatesStarted = true;

                continue;
            }

            Assert.False(unknownRatesStarted, "un tasso noto dopo uno ignoto: l'ordine e' sbagliato");

            double current = rate.GetDouble();
            Assert.True(current <= previous, "l'elenco per I/O non e' in ordine decrescente");
            previous = current;
        }

        // Almeno un tasso deve essere NOTO. Senza questa riga il test passerebbe a vuoto con
        // ogni tasso null - cioe' con il lettore dell'I/O mai collegato in Program.cs - e
        // l'ha dimostrato una mutazione: new SystemProcessLister(ioReader: null), suite verde.
        // Questo e' l'unico test che attraversa il cablaggio vero, dal servizio al sistema.
        Assert.Contains(
            document.RootElement.GetProperty("processes").EnumerateArray(),
            process => process.GetProperty("ioBytesPerSecond").ValueKind != JsonValueKind.Null);
    }
}
