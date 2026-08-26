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
    private const string RispostaValida =
        """
        {"schemaVersion":1,"capturedAt":"2026-08-26T09:15:49.34Z","collectors":[
          {"collectorId":"cpu","status":1,"message":null,"points":[
            {"metricId":"cpu.usage.total","instance":null,
             "value":{"kind":1,"number":64.25,"text":null,"flag":false},
             "status":1,"message":null}]}]}
        """;

    [Fact]
    public async Task GetLatestAsync_ConRispostaValida_RestituisceIlCampionamento()
    {
        using MetricsClient client = Crea(new FintoHandler(_ => Json(HttpStatusCode.OK, RispostaValida)));

        SnapshotFetch esito = await client.GetLatestAsync(CancellationToken.None);

        Assert.True(esito.IsOk);
        Assert.Equal(ServiceOutcome.Ok, esito.Outcome);
        Assert.Equal(1, esito.Snapshot!.SchemaVersion);

        MetricPoint punto = esito.Snapshot.Collectors[0].Points[0];

        // Il difetto piu' pericoloso di tutto il progetto e' un valore che si serializza e non
        // si rideserializza: il client mostrerebbe zeri marcati "Ok". Qui si verifica che il
        // numero vero arrivi fino in fondo.
        Assert.Equal(CollectorStatus.Ok, punto.Status);
        Assert.Equal(MetricValueKind.Number, punto.Value!.Value.Kind);
        Assert.Equal(64.25d, punto.Value.Value.Number);
    }

    [Fact]
    public async Task GetLatestAsync_MandaLAuthorizationBearer()
    {
        HttpRequestMessage? vista = null;

        using MetricsClient client = Crea(new FintoHandler(richiesta =>
        {
            vista = richiesta;
            return Json(HttpStatusCode.OK, RispostaValida);
        }));

        await client.GetLatestAsync(CancellationToken.None);

        Assert.NotNull(vista);
        Assert.Equal("Bearer", vista.Headers.Authorization!.Scheme);
        Assert.Equal("il-token", vista.Headers.Authorization.Parameter);
        Assert.Equal("http://localhost:5057/metrics/latest", vista.RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetLatestAsync_QuandoIlServizioRifiutaIlToken_LoDiceEIndicaDaDoveArriva(HttpStatusCode codice)
    {
        using MetricsClient client = Crea(new FintoHandler(_ => new HttpResponseMessage(codice)));

        SnapshotFetch esito = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.TokenRifiutato, esito.Outcome);
        Assert.Null(esito.Snapshot);
        Assert.Contains("dai test", esito.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("il-token", esito.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLatestAsync_QuandoIlServizioNonHaAncoraCampionato_NonLoChiamaErrore()
    {
        // 503 all'avvio e' normale: il campionatore non ha ancora pubblicato nulla.
        using MetricsClient client =
            Crea(new FintoHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        SnapshotFetch esito = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.NonAncoraPronto, esito.Outcome);
        Assert.NotEmpty(esito.Problem);
    }

    [Fact]
    public async Task GetLatestAsync_QuandoIlServizioEspento_DiceCheNonEraggiungibile()
    {
        using MetricsClient client = Crea(new FintoHandler(_ =>
            throw new HttpRequestException("Connessione rifiutata")));

        SnapshotFetch esito = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.NonRaggiungibile, esito.Outcome);
        Assert.Contains("localhost:5057", esito.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLatestAsync_ConRispostaCheNonEunCampionamento_LoDiceInvecediLanciare()
    {
        using MetricsClient client =
            Crea(new FintoHandler(_ => Json(HttpStatusCode.OK, "<html>ciao</html>")));

        SnapshotFetch esito = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.RispostaIncomprensibile, esito.Outcome);
        Assert.Null(esito.Snapshot);
    }

    [Fact]
    public async Task GetLatestAsync_ConVersioneDiSchemaDiversa_RifiutaInvecediMostrareZeri()
    {
        // Un servizio piu' recente riempirebbe la finestra di campi a zero marcati "Ok".
        using MetricsClient client = Crea(new FintoHandler(_ => Json(
            HttpStatusCode.OK,
            """{"schemaVersion":99,"capturedAt":"2026-08-26T09:15:49.34Z","collectors":[]}""")));

        SnapshotFetch esito = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.VersioneIncompatibile, esito.Outcome);
        Assert.Null(esito.Snapshot);
        Assert.Contains("99", esito.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCatalogAsync_LeggeNomiLeggibiliEUnita()
    {
        using MetricsClient client = Crea(new FintoHandler(_ => Json(
            HttpStatusCode.OK,
            """
            [{"collectorId":"cpu","descriptors":[
               {"metricId":"cpu.usage.total","displayName":"Utilizzo CPU",
                "unit":{"symbol":"%"},"isPerInstance":false}]}]
            """)));

        CatalogFetch esito = await client.GetCatalogAsync(CancellationToken.None);

        Assert.True(esito.IsOk);

        MetricDescriptor? descrittore = esito.Catalog!.Find("cpu.usage.total");

        Assert.NotNull(descrittore);
        Assert.Equal("Utilizzo CPU", descrittore.DisplayName);
        Assert.Equal("%", descrittore.Unit.Symbol);
    }

    [Fact]
    public async Task GetCatalogAsync_QuandoIlTokenEsbagliato_RestituisceLoStessoEsitoDelloSnapshot()
    {
        using MetricsClient client =
            Crea(new FintoHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        CatalogFetch esito = await client.GetCatalogAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.TokenRifiutato, esito.Outcome);
        Assert.Null(esito.Catalog);
    }

    private static MetricsClient Crea(HttpMessageHandler handler) =>
        new(
            new ObserverClientOptions(new Uri("http://localhost:5057/"), "il-token", "dai test"),
            handler);

    private static HttpResponseMessage Json(HttpStatusCode codice, string corpo) =>
        new(codice)
        {
            Content = new StringContent(corpo, Encoding.UTF8, "application/json"),
        };

    private sealed class FintoHandler(Func<HttpRequestMessage, HttpResponseMessage> risposta) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(risposta(request));
    }
}
