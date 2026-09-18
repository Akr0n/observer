using System.Net;
using System.Text;
using Observer.App.Services;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// The boundary with the network. Every way of failing must become a DISTINCT outcome with its
/// own wording: "the service is down" and "the token is wrong" are fixed in two different ways,
/// and whoever is looking at the window has nothing else to tell them apart by.
/// </summary>
public class MetricsClientTests
{
    private const string ValidResponse =
        """
        {"schemaVersion":1,"capturedAt":"2026-08-26T09:15:49.34Z","collectors":[
          {"collectorId":"cpu","status":1,"message":null,"points":[
            {"metricId":"cpu.usage.total","instance":null,
             "value":{"kind":1,"number":64.25,"text":null,"flag":false},
             "status":1,"message":null}]}]}
        """;

    [Fact]
    public async Task GetLatestAsync_WithAValidResponse_ReturnsTheSnapshot()
    {
        using MetricsClient client = Create(new FakeHandler(_ => Json(HttpStatusCode.OK, ValidResponse)));

        SnapshotFetch fetch = await client.GetLatestAsync(CancellationToken.None);

        Assert.True(fetch.IsOk);
        Assert.Equal(ServiceOutcome.Ok, fetch.Outcome);
        Assert.Equal(1, fetch.Snapshot!.SchemaVersion);

        MetricPoint point = fetch.Snapshot.Collectors[0].Points[0];

        // The most dangerous defect in the whole project is a value that serialises and does
        // not deserialise back: the client would show zeros marked "Ok". This checks that the
        // real number makes it all the way through.
        Assert.Equal(CollectorStatus.Ok, point.Status);
        Assert.Equal(MetricValueKind.Number, point.Value!.Value.Kind);
        Assert.Equal(64.25d, point.Value.Value.Number);
    }

    [Fact]
    public async Task GetLatestAsync_SendsTheBearerTokenToTheConfiguredAddress()
    {
        HttpRequestMessage? captured = null;

        using MetricsClient client = Create(new FakeHandler(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, ValidResponse);
        }));

        await client.GetLatestAsync(CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("il-token", captured.Headers.Authorization.Parameter);
        Assert.Equal("http://altra-macchina:5057/metrics/latest", captured.RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetLatestAsync_WhenTheServiceRejectsTheToken_SaysWhereItCameFromWithoutPrintingIt(HttpStatusCode code)
    {
        using MetricsClient client = Create(new FakeHandler(_ => new HttpResponseMessage(code)));

        SnapshotFetch fetch = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.TokenRejected, fetch.Outcome);
        Assert.Null(fetch.Snapshot);
        Assert.Contains("dai test", fetch.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("il-token", fetch.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLatestAsync_WhenTheServiceHasNotSampledYet_DoesNotCallItAnError()
    {
        // A 503 at startup is normal: the sampler has not published anything yet.
        using MetricsClient client =
            Create(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        SnapshotFetch fetch = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.NotReadyYet, fetch.Outcome);
        Assert.NotEmpty(fetch.Problem);
    }

    [Fact]
    public async Task GetProcessesAsync_OnAnOldService_SaysItIsOldNotThatTheResponseWasUnexpected()
    {
        // This actually happened, on this machine: the new dashboard queried a 0.4.1 service,
        // which does not have that endpoint, and got a 404. The message said "I do not know
        // how to interpret this" and sent you hunting for a defect that was not there. The
        // cause is known and so is the remedy: update the service on that machine.
        using MetricsClient client =
            Create(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        ProcessFetch fetch = await client.GetProcessesAsync("cpu", 15, CancellationToken.None);

        // IncompatibleVersion and not UnexpectedResponse: waiting does not update a service,
        // and the status bar has to say so straight away instead of staying in a waiting state.
        Assert.Equal(ServiceOutcome.IncompatibleVersion, fetch.Outcome);
        Assert.Contains("older than this dashboard", fetch.Problem, StringComparison.Ordinal);
        Assert.Empty(fetch.Processes);
    }

    [Fact]
    public async Task GetProcessesAsync_ForIoOnAServiceThatDoesNotKnowIt_SaysItIsOld()
    {
        // A 0.6 service does not know "io": it answers 200 with the CPU list, and without the
        // "by" field. Showing that list under the I/O title would be a lie.
        using MetricsClient client = Create(new FakeHandler(_ => Json(HttpStatusCode.OK,
            """{"capturedAt":"2026-09-03T08:00:00Z","processes":[{"pid":1,"name":"x","cpuPercent":null,"workingSetBytes":10}]}""")));

        ProcessFetch fetch = await client.GetProcessesAsync("io", 15, CancellationToken.None);

        Assert.Equal(ServiceOutcome.IncompatibleVersion, fetch.Outcome);
        Assert.Contains("older than this dashboard", fetch.Problem, StringComparison.Ordinal);
        Assert.Empty(fetch.Processes);
    }

    [Fact]
    public async Task GetProcessesAsync_ForCpuOnAServiceThatDoesNotEchoTheCriterion_StillWorks()
    {
        // That same old service can sort by CPU: a missing "by" must not make us reject a
        // list that is correct.
        using MetricsClient client = Create(new FakeHandler(_ => Json(HttpStatusCode.OK,
            """{"capturedAt":"2026-09-03T08:00:00Z","processes":[{"pid":1,"name":"x","cpuPercent":null,"workingSetBytes":10}]}""")));

        ProcessFetch fetch = await client.GetProcessesAsync("cpu", 15, CancellationToken.None);

        Assert.Equal(ServiceOutcome.Ok, fetch.Outcome);
        Assert.Single(fetch.Processes);
        Assert.Equal("—", fetch.Processes[0].Io);
    }

    [Fact]
    public async Task GetProcessesAsync_ReadsTheIoRateAndShowsADashWhenItIsMissing()
    {
        using MetricsClient client = Create(new FakeHandler(_ => Json(HttpStatusCode.OK,
            """{"capturedAt":"2026-09-03T08:00:00Z","by":"io","processes":[{"pid":1,"name":"copia","cpuPercent":2.5,"workingSetBytes":10,"ioBytesPerSecond":1572864},{"pid":2,"name":"ignoto","cpuPercent":null,"workingSetBytes":10,"ioBytesPerSecond":null}]}""")));

        ProcessFetch fetch = await client.GetProcessesAsync("io", 15, CancellationToken.None);

        Assert.Equal(ServiceOutcome.Ok, fetch.Outcome);
        Assert.Equal(["1.5 MiB/s", "—"], fetch.Processes.Select(row => row.Io));
    }

    [Fact]
    public async Task GetLatestAsync_WhenTheServiceIsDown_SaysItIsUnreachable()
    {
        using MetricsClient client = Create(new FakeHandler(_ =>
            throw new HttpRequestException("Connessione rifiutata")));

        SnapshotFetch fetch = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.Unreachable, fetch.Outcome);
        Assert.Contains("altra-macchina:5057", fetch.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLatestAsync_WithAResponseThatIsNotASnapshot_SaysSoInsteadOfThrowing()
    {
        using MetricsClient client =
            Create(new FakeHandler(_ => Json(HttpStatusCode.OK, "<html>ciao</html>")));

        SnapshotFetch fetch = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.UnreadableResponse, fetch.Outcome);
        Assert.Null(fetch.Snapshot);
    }

    [Fact]
    public async Task GetLatestAsync_WithADifferentSchemaVersion_RefusesInsteadOfShowingZeros()
    {
        // A newer service would fill the window with zeroed fields marked "Ok".
        using MetricsClient client = Create(new FakeHandler(_ => Json(
            HttpStatusCode.OK,
            """{"schemaVersion":99,"capturedAt":"2026-08-26T09:15:49.34Z","collectors":[]}""")));

        SnapshotFetch fetch = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.IncompatibleVersion, fetch.Outcome);
        Assert.Null(fetch.Snapshot);
        Assert.Contains("99", fetch.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCatalogAsync_ReadsDisplayNamesAndUnits()
    {
        using MetricsClient client = Create(new FakeHandler(_ => Json(
            HttpStatusCode.OK,
            """
            [{"collectorId":"cpu","descriptors":[
               {"metricId":"cpu.usage.total","displayName":"CPU usage",
                "unit":{"symbol":"%"},"isPerInstance":false}]}]
            """)));

        CatalogFetch fetch = await client.GetCatalogAsync(CancellationToken.None);

        Assert.True(fetch.IsOk);

        MetricDescriptor? descriptor = fetch.Catalog!.Find("cpu.usage.total");

        Assert.NotNull(descriptor);
        Assert.Equal("CPU usage", descriptor.DisplayName);
        Assert.Equal("%", descriptor.Unit.Symbol);
    }

    [Fact]
    public async Task GetCatalogAsync_WhenTheTokenIsWrong_ReturnsTheSameOutcomeAsGetLatestAsync()
    {
        using MetricsClient client =
            Create(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        CatalogFetch fetch = await client.GetCatalogAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.TokenRejected, fetch.Outcome);
        Assert.Null(fetch.Catalog);
    }

    [Fact]
    public async Task OnTheLOCALChannelNoCredentialIsSent()
    {
        // Sending the token where it is not needed means going on exposing it for nothing in
        // return: on the local channel the service does not even look at it.
        HttpRequestMessage? captured = null;

        using FakeHandler handler = new(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, "[]");
        });

        using MetricsClient client = CreateLocal(handler);
        await client.GetCatalogAsync(CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Null(captured.Headers.Authorization);
    }

    private static MetricsClient Create(HttpMessageHandler handler) =>
        new(
            ObserverEndpoint.Remote(new Uri("http://altra-macchina:5057/"), "il-token", "dai test"),
            handler);

    /// <summary>A client on the local channel, which must NOT send any credential.</summary>
    private static MetricsClient CreateLocal(HttpMessageHandler handler) =>
        new(ObserverEndpoint.LocalChannel(), handler);

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}