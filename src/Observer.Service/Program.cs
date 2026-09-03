using System.Text;
using Observer.Core.Composition;
using Observer.Core.Metrics;
using Observer.Core.Platform;
using Observer.Core.Processes;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Observer.Service;
using Observer.Service.Credentials;
using Observer.Service.LocalChannel;
using Observer.Service.Persistence;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// CreateBuilder carica appsettings.json e appsettings.{Environment}.json, e "Local" non e'
// un nome di ambiente: senza questa riga appsettings.Local.json non viene MAI letto, e chi
// segue il messaggio d'errore qui sotto si ritrova la stessa frase che gli dice di fare
// quello che ha appena fatto.
if (ConfigurazioneLocale.VaCaricato(
    Path.Combine(builder.Environment.ContentRootPath, ConfigurazioneLocale.NomeFile)))
{
    // Il controllo esiste perche' optional:true tollera un file ASSENTE e non un file
    // VUOTO: zero byte fanno fallire l'avvio con uno stack trace su "The input does not
    // contain any JSON tokens". E svuotare quel file e' esattamente cio' che si fa per
    // togliere il token che contiene, adesso che il servizio se lo genera da solo.
    builder.Configuration.AddJsonFile(ConfigurazioneLocale.NomeFile, optional: true, reloadOnChange: true);
}

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

// Singleton e non transient, per la stessa ragione dei collector: la classifica dei processi
// conserva il campione precedente per PID, e ricrearla a ogni richiesta lascerebbe la CPU di
// ogni processo eternamente sconosciuta.
builder.Services.AddSingleton<IProcessLister>(sp => new SystemProcessLister(
    ProcessIoReaders.Per(HostPlatformDetector.Current, sp.GetRequiredService<IFileTextReader>())));
builder.Services.AddSingleton<ProcessRanking>();
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
// della macchina: sul percorso di RETE il token resta obbligatorio, e non averlo significa
// non poter essere interrogati da un altro computer.
// Ma NON viene piu' preteso in configurazione: il servizio se lo genera e se lo custodisce.
// E' cio' che rende possibile un installer - finche' il token andava configurato, chi
// installava doveva generarlo, cioe' conoscerlo, registrarlo nel proprio log e lasciarselo
// dietro se falliva a meta'.
bool giraComeServizio = WindowsServiceHelpers.IsWindowsService() || SystemdHelpers.IsSystemdService();

string percorsoDeposito =
    builder.Configuration["Observer:CredentialStorePath"] ?? CredentialDirectory.PercorsoPredefinito();

ProvisionedCredentials credenziali = CredentialProvisioning.Provvedi(
    builder.Configuration["Observer:ApiToken"],
    percorsoDeposito,
    giraComeServizio);

if (credenziali.Origin == CredentialOrigin.Effimero)
{
    // Console e non il logger: questa riga serve a chi ha appena lanciato il servizio da un
    // terminale, e va vista subito. Come servizio di sistema questo ramo non si raggiunge
    // nemmeno, perche' li' il rifiuto di partire e' l'unica risposta accettabile.
    Console.WriteLine(
        "Observer could not secure a credential store, so this run uses a throwaway machine " +
        "token that is never written to disk. To let another computer query this one during " +
        "this run, export it:");
    Console.WriteLine("    Observer__ApiToken=" + credenziali.Credentials.Current);
}

// HTTPS verso le ALTRE macchine. Il certificato se lo genera e se lo custodisce il
// servizio, nello stesso perimetro del token e per la stessa ragione: cosi' l'installer
// non conosce niente. La fiducia non viene da un'autorita' ne' da una catena - il
// certificato e' autofirmato - ma dall'impronta, che si prende a mano da questa macchina
// con "observer share" e si fissa nel client.
NetworkOptions rete =
    builder.Configuration.GetSection(NetworkOptions.SectionName).Get<NetworkOptions>() ?? new NetworkOptions();

rete.Validate();

if (rete.Https)
{
    ProvisionedCertificate certificato = CertificateProvisioning.Provvedi(
        percorsoDeposito,
        Environment.MachineName,
        DateTimeOffset.UtcNow,
        giraComeServizio);

    if (certificato.Origin == CertificateOrigin.Effimero)
    {
        // Come per il token effimero: Console e non il logger, perche' questa riga serve a
        // chi ha appena lanciato il servizio da un terminale e va vista subito.
        Console.WriteLine(
            "Observer could not secure a machine certificate, so this run uses a throwaway one. " +
            "Its fingerprint changes at every start, so no dashboard that pinned the previous " +
            "one will connect.");
    }

    // ListenAnyIP e non ListenLocalhost: il senso di questa porta e' che la usino le altre
    // macchine. Chi guarda quella su cui e' seduto passa dal canale locale e non di qui.
    builder.WebHost.ConfigureKestrel(kestrel =>
        kestrel.ListenAnyIP(rete.HttpsPort, porta => porta.UseHttps(certificato.Certificate)));
}

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

app.UseObserverAccessControl(credenziali.Credentials);

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

// Chi sta consumando la macchina, e come fermarlo. E' l'unico gruppo di endpoint che non si
// limita a leggere: /processes/{pid}/kill distrugge stato, e per questo registra ogni
// tentativo con provenienza del chiamante.
app.MapProcessEndpoints();

app.Run();
