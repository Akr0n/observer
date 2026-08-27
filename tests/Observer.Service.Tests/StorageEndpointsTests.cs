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
[Collection(AmbienteDelProcesso.Nome)]
public class StorageEndpointsTests : IClassFixture<ServizioInMemoria>
{
    private readonly ServizioInMemoria servizio;

    public StorageEndpointsTests(ServizioInMemoria servizio)
    {
        this.servizio = servizio;
    }

    private static DateTimeOffset T(string istanteIso) =>
        DateTimeOffset.Parse(istanteIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [Theory]
    [InlineData("/metrics/series")]
    [InlineData("/metrics/history?collector=cpu&metric=cpu.usage.total")]
    [InlineData("/metrics/storage")]
    public async Task EndpointNuovi_SenzaTokenRispondono401(string percorso)
    {
        using HttpClient anonimo = servizio.CreateClient();

        using HttpResponseMessage risposta = await anonimo.GetAsync(new Uri(percorso, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, risposta.StatusCode);
    }

    [Fact]
    public async Task Serie_ElencaCioCheEStatoScritto()
    {
        Semina("seriegia", 5d);

        using HttpClient client = servizio.CreateAuthorizedClient();
        using JsonDocument documento = await Leggi(client, "/metrics/series");

        bool trovata = documento.RootElement.EnumerateArray().Any(elemento =>
            elemento.GetProperty("metricId").GetString() == "seriegia");

        Assert.True(trovata, "la serie appena scritta deve comparire nell'elenco");
    }

    [Fact]
    public async Task Storico_RestituisceIPuntiGrezziSeminati()
    {
        Semina("storicogrezzo", 42d);

        using HttpClient client = servizio.CreateAuthorizedClient();
        using JsonDocument documento = await Leggi(
            client,
            "/metrics/history?collector=prova&metric=storicogrezzo" +
            "&from=2026-08-26T12:00:00Z&to=2026-08-26T12:01:00Z&resolution=raw");

        Assert.Equal("raw", documento.RootElement.GetProperty("resolution").GetString());
        Assert.Equal(1, documento.RootElement.GetProperty("bucketSeconds").GetInt32());

        JsonElement punto = Assert.Single(documento.RootElement.GetProperty("points").EnumerateArray());

        // Anche sul grezzo la forma e' quella degli aggregati: conteggio 1 e i quattro
        // valori uguali. E' cio' che permette al client di cambiare risoluzione senza
        // cambiare codice di disegno.
        Assert.Equal(1, punto.GetProperty("count").GetInt32());
        Assert.Equal(42d, punto.GetProperty("avg").GetDouble());
        Assert.Equal(42d, punto.GetProperty("min").GetDouble());
        Assert.Equal(42d, punto.GetProperty("max").GetDouble());
        Assert.Equal(42d, punto.GetProperty("last").GetDouble());
    }

    [Fact]
    public async Task Storico_ConRisoluzioneAutomaticaScendeAiMinutiSuUnaFinestraLunga()
    {
        using HttpClient client = servizio.CreateAuthorizedClient();
        using JsonDocument documento = await Leggi(
            client,
            "/metrics/history?collector=prova&metric=qualsiasi" +
            "&from=2026-08-26T00:00:00Z&to=2026-08-26T12:00:00Z");

        // Dodici ore a un punto al secondo sarebbero 43200 punti in una sola risposta.
        Assert.NotEqual("raw", documento.RootElement.GetProperty("resolution").GetString());
    }

    [Fact]
    public async Task Storico_ConFinestraRovesciataRisponde400()
    {
        using HttpClient client = servizio.CreateAuthorizedClient();
        using HttpResponseMessage risposta = await client.GetAsync(new Uri(
            "/metrics/history?collector=cpu&metric=cpu.usage.total" +
            "&from=2026-08-26T12:00:00Z&to=2026-08-26T11:00:00Z",
            UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, risposta.StatusCode);
    }

    [Fact]
    public async Task Storico_ConRisoluzioneInventataRisponde400()
    {
        // Deve dire che la domanda e' sbagliata, non restituire zero punti: zero punti si
        // legge come "la macchina non era monitorata".
        using HttpClient client = servizio.CreateAuthorizedClient();
        using HttpResponseMessage risposta = await client.GetAsync(new Uri(
            "/metrics/history?collector=cpu&metric=cpu.usage.total&resolution=ogni-tanto",
            UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, risposta.StatusCode);
    }

    [Fact]
    public async Task Storico_SenzaMetricaRisponde400()
    {
        using HttpClient client = servizio.CreateAuthorizedClient();
        using HttpResponseMessage risposta =
            await client.GetAsync(new Uri("/metrics/history?collector=cpu", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, risposta.StatusCode);
    }

    [Fact]
    public async Task Statistiche_DiconoDoveScriveEQuantoScarta()
    {
        using HttpClient client = servizio.CreateAuthorizedClient();
        using JsonDocument documento = await Leggi(client, "/metrics/storage");

        Assert.True(documento.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(
            Path.GetFullPath(servizio.DatabasePath),
            documento.RootElement.GetProperty("databasePath").GetString());

        // Gli scarti devono essere misurabili: uno storico con buchi che non li dichiara e'
        // indistinguibile da uno storico completo.
        Assert.True(documento.RootElement.GetProperty("droppedSnapshots").GetInt64() >= 0L);

        JsonElement ritenzione = documento.RootElement.GetProperty("retention");
        Assert.Equal("06:00:00", ritenzione.GetProperty("raw").GetString());
        Assert.Equal("7.00:00:00", ritenzione.GetProperty("minute").GetString());
        Assert.Equal("90.00:00:00", ritenzione.GetProperty("fiveMinute").GetString());
    }

    [Fact]
    public async Task EndpointVecchi_ContinuanoARispondere()
    {
        // La persistenza e' un'aggiunta: se rompesse cio' che c'era prima, sarebbe un
        // peggioramento netto.
        using HttpClient client = servizio.CreateAuthorizedClient();
        using HttpResponseMessage catalogo =
            await client.GetAsync(new Uri("/metrics/catalog", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, catalogo.StatusCode);
    }

    private void Semina(string metrica, double valore) =>
        servizio.Store().WriteSamples(
        [
            new SeriesSample(
                new SeriesKey("prova", metrica, string.Empty),
                MetricValueKind.Number,
                T("2026-08-26T12:00:30Z").ToUnixTimeMilliseconds(),
                valore),
        ]);

    private static async Task<JsonDocument> Leggi(HttpClient client, string percorso)
    {
        using HttpResponseMessage risposta = await client.GetAsync(new Uri(percorso, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, risposta.StatusCode);

        return JsonDocument.Parse(
            await risposta.Content.ReadAsStringAsync(),
            new JsonDocumentOptions());
    }
}