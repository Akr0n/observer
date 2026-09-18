using System.Net;
using System.Text;
using Observer.App.Services;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// Il confine con la rete. Ogni modo di fallire deve diventare un esito DISTINTO con la sua
/// frase: "il servizio e' spento" e "il token e' sbagliato" si risolvono in due modi diversi,
/// e chi guarda la finestra non ha altro da cui capirlo.
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

        // Il difetto piu' pericoloso di tutto il progetto e' un valore che si serializza e non
        // si rideserializza: il client mostrerebbe zeri marcati "Ok". Qui si verifica che il
        // numero vero arrivi fino in fondo.
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
        // 503 all'avvio e' normale: il campionatore non ha ancora pubblicato nulla.
        using MetricsClient client =
            Create(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        SnapshotFetch fetch = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.NotReadyYet, fetch.Outcome);
        Assert.NotEmpty(fetch.Problem);
    }

    [Fact]
    public async Task GetProcessesAsync_OnAnOldService_SaysItIsOldNotThatTheResponseWasUnexpected()
    {
        // Successo davvero, su questa macchina: la dashboard nuova ha interrogato un servizio
        // 0.4.1, che quell'endpoint non ce l'ha, e ha risposto 404. Il messaggio diceva "non
        // so come interpretarlo" e mandava a cercare un difetto che non c'era. La causa e'
        // nota e il rimedio pure: aggiornare il servizio su quella macchina.
        using MetricsClient client =
            Create(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        ProcessFetch fetch = await client.GetProcessesAsync("cpu", 15, CancellationToken.None);

        // IncompatibleVersion e non UnexpectedResponse: aspettare non aggiorna un servizio, e
        // la barra di stato deve dirlo subito invece di restare in attesa.
        Assert.Equal(ServiceOutcome.IncompatibleVersion, fetch.Outcome);
        Assert.Contains("older than this dashboard", fetch.Problem, StringComparison.Ordinal);
        Assert.Empty(fetch.Processes);
    }

    [Fact]
    public async Task GetProcessesAsync_ForIoOnAServiceThatDoesNotKnowIt_SaysItIsOld()
    {
        // Un servizio 0.6 non conosce "io": risponde 200 con l'elenco della CPU, e senza il
        // campo "by". Mostrare quell'elenco sotto il titolo dell'I/O sarebbe una bugia.
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
        // Lo stesso servizio vecchio sa ordinare per CPU: l'assenza di "by" non deve
        // rifiutare un elenco che e' giusto.
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
        // Un servizio piu' recente riempirebbe la finestra di campi a zero marcati "Ok".
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
        // Mandare il token dove non serve significa continuare a esporlo senza guadagnarci
        // niente: il servizio, sul canale locale, non lo guarda nemmeno.
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

    /// <summary>Un client sul canale locale, che NON deve mandare alcuna credenziale.</summary>
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