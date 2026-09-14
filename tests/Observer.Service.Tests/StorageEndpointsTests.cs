using System.Globalization;
using System.Net;
using System.Text.Json;
using Observer.Core.Metrics;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// The new endpoints, against the REAL service started in memory.
/// </summary>
/// <remarks>
/// Two things can only be checked this way. The first is authentication: an endpoint added
/// outside the middleware would expose the machine's history to anyone on the network, and
/// no unit test would notice. The second is the container: if a registration is missing the
/// service does not start, and no unit test notices that either.
/// <para>
/// It sits in the <see cref="ProcessEnvironment"/> collection because its fixture writes
/// environment variables and clears SQLite's pools: PROCESS state, not class state.
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

        // Raw points have the same shape as the aggregates: count 1 and the four values
        // all equal. That is what lets the client change resolution without changing any
        // drawing code.
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

        // Twelve hours at one point a second would be 43200 points in a single response.
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
        // It has to say the question is wrong, not return zero points: zero points reads as
        // "the machine was not being monitored".
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

        // Dropped snapshots must be measurable: a history with gaps that does not declare
        // them is indistinguishable from a complete one.
        Assert.True(document.RootElement.GetProperty("droppedSnapshots").GetInt64() >= 0L);

        JsonElement retention = document.RootElement.GetProperty("retention");
        Assert.Equal("06:00:00", retention.GetProperty("raw").GetString());
        Assert.Equal("7.00:00:00", retention.GetProperty("minute").GetString());
        Assert.Equal("90.00:00:00", retention.GetProperty("fiveMinute").GetString());
    }

    [Fact]
    public async Task OldEndpoints_KeepAnswering()
    {
        // Persistence is an addition: if it broke what was there before, it would be a net
        // loss.
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