using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using Observer.Core.Composition;
using Observer.Core.Metrics;
using Observer.Service;
using Observer.Service.LocalChannel;
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

// Permette allo stesso eseguibile di girare come servizio di sistema: registrato nel Service
// Control Manager su Windows, come unit systemd su Linux. Entrambe le chiamate non fanno nulla
// quando il processo e' avviato normalmente da terminale, quindi non esistono due modalita' da
// mantenere separate. E' cio' che rende reale il vincolo Session 0 da cui nasce l'architettura
// a due processi: il servizio raccoglie senza che nessuno tenga aperta una finestra.
builder.Host.UseWindowsService();
builder.Host.UseSystemd();

builder.Services.AddObserverMetrics();
builder.Services.AddSingleton<MetricSnapshotCache>();
builder.Services.AddHostedService<MetricSamplingService>();

// Lo storico. Le opzioni si convalidano QUI, prima di aprire la porta: una ritenzione a zero
// non farebbe fallire niente, cancellerebbe solo tutto in silenzio, e il guasto si
// scoprirebbe il giorno in cui a qualcuno serve un grafico di ieri.
StorageOptions storage =
    builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();

storage.Validate();

// Gli URL degli endpoint si convalidano QUI, per lo stesso motivo per cui si convalida la
// ritenzione: non tutti i modi di sbagliare falliscono. Un percorso di socket scritto in stile
// Windows dentro "http://unix:" non fa lanciare niente e fa ascoltare Kestrel sulla porta 80 di
// OGNI interfaccia, con la telemetria della macchina dietro. Meglio non partire.
foreach (IConfigurationSection endpoint in builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren())
{
    if (endpoint["Url"] is { } url && EndpointUrl.Problema(url) is { } problema)
    {
        throw new InvalidOperationException(
            $"Kestrel endpoint '{endpoint.Key}' is misconfigured. {problema}");
    }
}

// Il canale locale: named pipe su Windows, socket unix su Linux. Il nome e il percorso sono
// configurabili perche' un endpoint che non si binda abbatte l'INTERO host, endpoint TCP
// compreso: con valori fissi, lanciare questo servizio a mano su una macchina dove quello
// installato gira non fallirebbe piu' "solo sulla porta", non partirebbe affatto.
LocalChannelOptions canaleLocale =
    builder.Configuration.GetSection(LocalChannelOptions.SectionName).Get<LocalChannelOptions>()
        ?? new LocalChannelOptions();

canaleLocale.Validate();

string? percorsoDelSocket = await LocalChannelSetup.ConfiguraAsync(builder, canaleLocale);

builder.Services.AddSingleton(storage);

// Magazzino e coda si registrano SEMPRE, anche a storico spento: costruirli non tocca il
// disco, e cosi' gli endpoint possono rispondere "disattivato" invece di non esistere.
// Percorso RISOLTO, mai quello grezzo: un servizio non ha una cartella di lavoro prevedibile,
// e un percorso relativo farebbe comparire il database in posti diversi a seconda di come e'
// stato avviato, dando l'impressione di aver perso lo storico.
builder.Services.AddSingleton(new MetricStore(storage.ResolveDatabasePath()));
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
        "Observer:ApiToken is not configured. This service exposes machine telemetry over the " +
        "whole network and will not start without authentication. Set it in " +
        "appsettings.Local.json (already git-ignored) or in the Observer__ApiToken " +
        "environment variable.");
}

byte[] expectedToken = Encoding.UTF8.GetBytes(apiToken);

WebApplication app = builder.Build();

if (OperatingSystem.IsLinux() && percorsoDelSocket is { } socketLocale)
{
    // Il modo del file va imposto DOPO l'avvio: prima quel file non esiste, e un chmod
    // accanto alla creazione della directory fallirebbe.
    // Quale percorso sia stato scelto non serve stamparlo qui: /run/observer non e' creabile
    // da un utente normale e il ripiego cambia il percorso, ma Kestrel lo dice gia' da se'
    // nella sua riga "Now listening on: http://unix:/...".
    LinuxUnixSocket.RestringiDopoAvvio(app.Lifetime, socketLocale);
}

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
