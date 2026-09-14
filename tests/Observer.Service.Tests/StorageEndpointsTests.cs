using System.Globalization;
using System.Net;
using System.Text.Json;
using Observer.Core.Metrics;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// Gli endpoint nuovi, sul servizio VERO avviato in memoria.
/// </summary>
/// <remarks>
/// Due cose si possono verificare solo cosi'. La prima e' l'autenticazione: un endpoint
/// aggiunto fuori dal middleware esporrebbe lo storico della macchina a chiunque sia sulla
/// rete, e nessun test di unita' se ne accorgerebbe. La seconda e' il container: se manca
/// una registrazione il servizio non parte, e anche di quello nessun test di unita' si
/// accorge.
/// <para>
/// Sta nella collezione <see cref="AmbienteDelProcesso"/> perche' la sua fixture scrive
/// variabili d'ambiente e svuota i pool di SQLite: stato del PROCESSO, non della classe.
/// </para>
/// </remarks>
[Collection(ProcessEnvironment.Name)]
public class StorageEndpointsTests
{
    private readonly InMemoryService service;

    public StorageEndpointsTests(InMemoryService service)
    {
        this.service = service;
    }

    private static DateTimeOffset T(string instantIso) =>
        DateTimeOffset.Parse(instantIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [Theory]
    [InlineData("/metrics/series")]
    [InlineData("/metrics/history?collector=cpu&metric=cpu.usage.total")]
    [InlineData("/metrics/storage")]
    public async Task NewEndpoints_RespondWith401WithoutAToken(string path)
    {
        using HttpClient anonymous = service.CreateClient();

        using HttpResponseMessage response = await anonymous.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Series_ListsWhatHasBeenWritten()
    {
        Seed("seriegia", 5d);

        using HttpClient client = service.CreateAuthorizedClient();
        using JsonDocument document = await ReadJson(client, "/metrics/series");

        bool found = document.RootElement.EnumerateArray().Any(element =>
            element.GetProperty("metricId").GetString() == "seriegia");

        Assert.True(found, "la serie appena scritta deve comparire nell'elenco");
    }

    [Fact]
    public async Task History_ReturnsTheSeededRawPoints()
    {
        Seed("storicogrezzo", 42d);

        using HttpClient client = service.CreateAuthorizedClient();
        using JsonDocument document = await ReadJson(
            client,
            "/metrics/history?collector=prova&metric=storicogrezzo" +
            "&from=2026-08-26T12:00:00Z&to=2026-08-26T12:01:00Z&resolution=raw");

        Assert.Equal("raw", document.RootElement.GetProperty("resolution").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("bucketSeconds").GetInt32());

        JsonElement point = Assert.Single(document.RootElement.GetProperty("points").EnumerateArray());

        // Anche sul grezzo la forma e' quella degli aggregati: conteggio 1 e i quattro
        // valori uguali. E' cio' che permette al client di cambiare risoluzione senza
        // cambiare codice di disegno.
        Assert.Equal(1, point.GetProperty("count").GetInt32());
        Assert.Equal(42d, point.GetProperty("avg").GetDouble());
        Assert.Equal(42d, point.GetProperty("min").GetDouble());
        Assert.Equal(42d, point.GetProperty("max").GetDouble());
        Assert.Equal(42d, point.GetProperty("last").GetDouble());
    }

    [Fact]
    public async Task History_AutomaticResolutionDropsToMinutesOverALongWindow()
    {
        using HttpClient client = service.CreateAuthorizedClient();
        using JsonDocument document = await ReadJson(
            client,
            "/metrics/history?collector=prova&metric=qualsiasi" +
            "&from=2026-08-26T00:00:00Z&to=2026-08-26T12:00:00Z");

        // Dodici ore a un punto al secondo sarebbero 43200 punti in una sola risposta.
        Assert.NotEqual("raw", document.RootElement.GetProperty("resolution").GetString());
    }

    [Fact]
    public async Task History_RespondsWith400WhenTheWindowIsBackwards()
    {
        using HttpClient client = service.CreateAuthorizedClient();
        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/metrics/history?collector=cpu&metric=cpu.usage.total" +
            "&from=2026-08-26T12:00:00Z&to=2026-08-26T11:00:00Z",
            UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task History_RespondsWith400ForAnUnknownResolution()
    {
        // Deve dire che la domanda e' sbagliata, non restituire zero punti: zero punti si
        // legge come "la macchina non era monitorata".
        using HttpClient client = service.CreateAuthorizedClient();
        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/metrics/history?collector=cpu&metric=cpu.usage.total&resolution=ogni-tanto",
            UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task History_RespondsWith400WithoutAMetric()
    {
        using HttpClient client = service.CreateAuthorizedClient();
        using HttpResponseMessage response =
            await client.GetAsync(new Uri("/metrics/history?collector=cpu", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Stats_SayWhereItWritesAndHowMuchItDrops()
    {
        using HttpClient client = service.CreateAuthorizedClient();
        using JsonDocument document = await ReadJson(client, "/metrics/storage");

        Assert.True(document.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(
            Path.GetFullPath(service.DatabasePath),
            document.RootElement.GetProperty("databasePath").GetString());

        // Gli scarti devono essere misurabili: uno storico con buchi che non li dichiara e'
        // indistinguibile da uno storico completo.
        Assert.True(document.RootElement.GetProperty("droppedSnapshots").GetInt64() >= 0L);

        JsonElement retention = document.RootElement.GetProperty("retention");
        Assert.Equal("06:00:00", retention.GetProperty("raw").GetString());
        Assert.Equal("7.00:00:00", retention.GetProperty("minute").GetString());
        Assert.Equal("90.00:00:00", retention.GetProperty("fiveMinute").GetString());
    }

    [Fact]
    public async Task OldEndpoints_KeepAnswering()
    {
        // La persistenza e' un'aggiunta: se rompesse cio' che c'era prima, sarebbe un
        // peggioramento netto.
        using HttpClient client = service.CreateAuthorizedClient();
        using HttpResponseMessage catalog =
            await client.GetAsync(new Uri("/metrics/catalog", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
    }

    private void Seed(string metric, double value) =>
        service.Store().WriteSamples(
        [
            new SeriesSample(
                new SeriesKey("prova", metric, string.Empty),
                MetricValueKind.Number,
                T("2026-08-26T12:00:30Z").ToUnixTimeMilliseconds(),
                value),
        ]);

    private static async Task<JsonDocument> ReadJson(HttpClient client, string path)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(),
            new JsonDocumentOptions());
    }
}