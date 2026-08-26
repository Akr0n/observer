using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using Observer.Core.Composition;
using Observer.Core.Metrics;
using Observer.Service;
using Observer.Service.Persistence;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// CreateBuilder carica appsettings.json e appsettings.{Environment}.json, e "Local" non e'
// un nome di ambiente: senza questa riga appsettings.Local.json non viene MAI letto, e chi
// segue il messaggio d'errore qui sotto si ritrova la stessa frase che gli dice di fare
// quello che ha appena fatto.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Le due righe seguenti non sono ridondanti: riaggiungono ambiente e riga di comando DOPO
// il file, per rimetterli in cima alla precedenza. Senza, il file appena aggiunto vincerebbe
// su Observer__ApiToken, e un token vecchio dimenticato nel file sovrascriverebbe in
// silenzio quello nuovo passato dall'ambiente.
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Services.AddObserverMetrics();
builder.Services.AddSingleton<MetricSnapshotCache>();
builder.Services.AddHostedService<MetricSamplingService>();

// Lo storico. Le opzioni si convalidano QUI, prima di aprire la porta: una ritenzione a zero
// non farebbe fallire niente, cancellerebbe solo tutto in silenzio, e il guasto si
// scoprirebbe il giorno in cui a qualcuno serve un grafico di ieri.
StorageOptions storage =
    builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();

storage.Validate();

builder.Services.AddSingleton(storage);

// Magazzino e coda si registrano SEMPRE, anche a storico spento: costruirli non tocca il
// disco, e cosi' gli endpoint possono rispondere "disattivato" invece di non esistere.
builder.Services.AddSingleton(new MetricStore(storage.DatabasePath));
builder.Services.AddSingleton(new SnapshotBuffer(storage.QueueCapacity));

if (storage.Enabled)
{
    builder.Services.AddSingleton<IMetricSnapshotSink>(
        provider => provider.GetRequiredService<SnapshotBuffer>());
    builder.Services.AddSingleton<MetricWriter>();
    builder.Services.AddHostedService<MetricPersistenceService>();
}
else
{
    builder.Services.AddSingleton<IMetricSnapshotSink, NullMetricSnapshotSink>();
}

// Il servizio ascolta anche fuori da localhost (vedi appsettings.json) ed espone telemetria
// della macchina: senza token sarebbe leggibile da chiunque sia sulla stessa rete. Quindi si
// rifiuta di partire, invece di partire in chiaro. Un servizio che non parte si nota subito;
// uno che parte aperto no.
string? apiToken = builder.Configuration["Observer:ApiToken"];

if (string.IsNullOrWhiteSpace(apiToken))
{
    throw new InvalidOperationException(
        "Observer:ApiToken non e' configurato. Il servizio espone telemetria della macchina su " +
        "tutta la rete e non parte senza autenticazione. Impostalo in appsettings.Local.json " +
        "(gia' escluso da git) oppure nella variabile d'ambiente Observer__ApiToken.");
}

byte[] expectedToken = Encoding.UTF8.GetBytes(apiToken);

WebApplication app = builder.Build();

app.Use(async (context, next) =>
{
    if (!IsAuthorized(context.Request.Headers.Authorization, expectedToken))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return;
    }

    await next(context).ConfigureAwait(false);
});

// Il catalogo descrive le metriche esistenti, comprese quelle non misurabili qui: e' cio'
// che permette al client di disegnare una metrica che non conosceva a tempo di compilazione.
app.MapGet("/metrics/catalog", (IReadOnlyList<IMetricCollector> collectors) =>
    collectors.Select(c => new { collectorId = c.Id, descriptors = c.Descriptors }));

// Legge SOLO dalla cache: gli endpoint non campionano mai, altrimenti due richieste
// simultanee falserebbero il calcolo della percentuale CPU.
app.MapGet("/metrics/latest", (MetricSnapshotCache cache) =>
    cache.Latest is { } snapshot
        ? Results.Ok(snapshot)
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

// Mappati DOPO il middleware qui sopra, come gli altri due: lo storico dice quando la
// macchina e' accesa e quanto lavora, cioe' piu' di quanto dica un singolo campionamento.
app.MapStorageEndpoints();

app.Run();

// Confronto a tempo costante: un confronto normale esce al primo byte diverso, e quella
// differenza di tempo permette di indovinare il token un carattere alla volta.
static bool IsAuthorized(StringValues header, byte[] expectedToken)
{
    string? value = header.Count == 1 ? header[0] : null;

    if (value is null || !value.StartsWith("Bearer ", StringComparison.Ordinal))
    {
        return false;
    }

    byte[] presented = Encoding.UTF8.GetBytes(value["Bearer ".Length..]);

    return CryptographicOperations.FixedTimeEquals(presented, expectedToken);
}
