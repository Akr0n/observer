using System.Text;
using Observer.Core.Composition;
using Observer.Core.Metrics;
using Observer.Core.Platform;
using Observer.Core.Processes;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Observer.Service;
using Observer.Service.Credentials;
using Observer.Service.LocalChannel;
using Observer.Service.Persistence;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// CreateBuilder loads appsettings.json and appsettings.{Environment}.json, and "Local" is not
// an environment name: without this line appsettings.Local.json is NEVER read, and whoever
// follows the error message below gets back the very sentence that tells them to do
// what they have just done.
if (LocalConfigurationFile.ShouldLoad(
    Path.Combine(builder.Environment.ContentRootPath, LocalConfigurationFile.FileName)))
{
    // The check exists because optional:true tolerates a MISSING file and not an EMPTY
    // one: zero bytes fail the start-up with a stack trace on "The input does not
    // contain any JSON tokens". And emptying that file is exactly what one does to
    // remove the token it holds, now that the service generates its own.
    builder.Configuration.AddJsonFile(LocalConfigurationFile.FileName, optional: true, reloadOnChange: true);
}

// The two lines below are not redundant: they re-add the environment and the command line AFTER
// the file, to put them back on top of the precedence. Without them the file just added would
// win over Observer__ApiToken, and an old token forgotten in the file would silently
// overwrite the new one passed through the environment.
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

// Lets the same executable run as a system service: registered in the Service Control Manager
// on Windows, as a systemd unit on Linux. Neither call does anything when the process is
// started normally from a terminal, so there are no two modes to keep separate. It is what
// makes the Session 0 constraint real, the constraint the two-process architecture comes from:
// the service collects without anyone keeping a window open.
builder.Host.UseWindowsService();
builder.Host.UseSystemd();

builder.Services.AddObserverMetrics();
builder.Services.AddSingleton<MetricSnapshotCache>();

// Response compression, with ONE single option, and that line is a security decision taken,
// not a configuration detail.
//
// WHY TURN IT ON OVER HTTPS. The ASP.NET Core default is EnableForHttps = false, and it is
// there to keep BREACH away. BREACH, however, wants THREE things together: a secret in the
// response, attacker input reflected in the same response, and the chance to observe many
// of them. Here only ONE holds. The reflection is there and it is total - /metrics/history
// sends collector, metric and instance back verbatim - but there is no secret in the bodies:
// the token appears in no response, and the certificate fingerprint is not a secret, it is
// exactly what the client expects to see. Above all: NOBODY can make the service produce a
// compressible body without already holding the token - without it the response is a 401 with
// Content-Length 0, measured on every route. The BREACH attacker here is someone who already
// has the credential, and with that reads everything in the clear and can even kill processes.
// There is no browser, there are no cookies, there is no ambient authority to steal.
// What really does open up, and is accepted in writing: TLS record lengths become a
// function of the content instead of nearly constant, so whoever sits in the middle can infer
// something about the shape of the traffic. It is a modest side channel, against a measured gain.
//
// And leaving the default would not be "more cautious", it would be the WRONG way round: without
// this line only the local channel would be compressed - named pipe and unix socket are HTTP, not
// HTTPS - that is, the measured machine's CPU would be spent for zero bytes of network, leaving
// uncovered the one path where bytes really cost.
//
// GZIP BEFORE BROTLI, and it is measured on the wire, not on a buffer. At equal preference the
// service picks the FIRST registered provider, and the default puts Brotli in front.
// Compressing a body all at once Brotli wins, and that is the comparison that comes by instinct;
// but this service SERIALISES - Results.Ok pushes the JSON out of the writer in pieces, with one
// flush per segment - and flushes punish Brotli far more than Gzip. Measured from the bench on
// real TLS, on the same body: gzip 2 720 bytes, brotli 3 223. Eighteen per cent more, and exactly
// on the responses that weigh. Registering them explicitly only inverts the precedence at equal
// preference: Brotli stays available for a client that accepts only that one.
//
// The level stays Fastest, which is the default of both: Optimal costs 4 to 30 times as much for
// a handful of bytes, and it is the one choice capable of showing up in the number this
// service publishes about itself. The bulk is not /metrics/latest (3.2 kB) but
// /metrics/history: the raw tail weighs 76 kB at one hour and 114 kB at twenty-four, and it is
// asked for once per gauge.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<GzipCompressionProvider>();
    options.Providers.Add<BrotliCompressionProvider>();
});

// Singleton and not transient, for the same reason as the collectors: the process ranking
// keeps the previous sample per PID, and rebuilding it at every request would leave the CPU of
// every process forever unknown.
builder.Services.AddSingleton<IProcessLister>(sp => new SystemProcessLister(
    ProcessIoReaders.For(HostPlatformDetector.Current, sp.GetRequiredService<IFileTextReader>())));
builder.Services.AddSingleton<ProcessRanking>();
builder.Services.AddHostedService<MetricSamplingService>();

// History. The options are validated HERE, before opening the port: a retention of zero
// would make nothing fail, it would just delete everything silently, and the fault would be
// discovered the day someone needs a chart of yesterday.
StorageOptions storage =
    builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();

storage.Validate();

// The endpoint URLs are validated HERE, for the same reason retention is validated:
// not every way of getting it wrong fails. A socket path written Windows-style
// inside "http://unix:" throws nothing and makes Kestrel listen on port 80 of
// EVERY interface, with the machine's telemetry behind it. Better not to start.
foreach (IConfigurationSection endpoint in builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren())
{
    if (endpoint["Url"] is { } url && EndpointUrl.Problem(url) is { } problem)
    {
        throw new InvalidOperationException(
            $"Kestrel endpoint '{endpoint.Key}' is misconfigured. {problem}");
    }
}

// The local channel: named pipe on Windows, unix socket on Linux. The name and the path are
// configurable because an endpoint that fails to bind brings down the WHOLE host, TCP endpoint
// included: with fixed values, launching this service by hand on a machine where the installed
// one is running would no longer fail "only on the port", it would not start at all.
LocalChannelOptions localChannelOptions =
    builder.Configuration.GetSection(LocalChannelOptions.SectionName).Get<LocalChannelOptions>()
        ?? new LocalChannelOptions();

localChannelOptions.Validate();

string? socketPath = await LocalChannelSetup.ConfigureAsync(builder, localChannelOptions);

builder.Services.AddSingleton(storage);

// Store and queue are ALWAYS registered, even with history off: building them does not touch
// the disk, and this way the endpoints can answer "disabled" instead of not existing.
// RESOLVED path, never the raw one: a service has no predictable working directory,
// and a relative path would make the database appear in different places depending on how it
// was started, giving the impression that the history had been lost.
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

// The service also listens outside localhost (see appsettings.json) and exposes the machine's
// telemetry: on the NETWORK path the token stays mandatory, and not having one means
// not being able to be queried from another computer.
// But it is NOT demanded in configuration any more: the service generates it and keeps it itself.
// That is what makes an installer possible - as long as the token had to be configured, whoever
// installed had to generate it, that is to know it, record it in their own log and leave it
// behind if they failed halfway.
bool runningAsService = WindowsServiceHelpers.IsWindowsService() || SystemdHelpers.IsSystemdService();

string credentialStorePath =
    builder.Configuration["Observer:CredentialStorePath"] ?? CredentialDirectory.DefaultPath();

ProvisionedCredentials credentials = CredentialProvisioning.Provision(
    builder.Configuration["Observer:ApiToken"],
    credentialStorePath,
    runningAsService);

if (credentials.Origin == CredentialOrigin.Ephemeral)
{
    // Console and not the logger: this line is for whoever has just launched the service from a
    // terminal, and it has to be seen at once. As a system service this branch is not even
    // reached, because there refusing to start is the only acceptable answer.
    Console.WriteLine(
        "Observer could not secure a credential store, so this run uses a throwaway machine " +
        "token that is never written to disk. To let another computer query this one during " +
        "this run, export it:");
    Console.WriteLine("    Observer__ApiToken=" + credentials.Credentials.Current);
}

// HTTPS towards the OTHER machines. The certificate is generated and kept by the
// service itself, in the same perimeter as the token and for the same reason: this way the
// installer knows nothing. Trust comes neither from an authority nor from a chain - the
// certificate is self-signed - but from the fingerprint, taken by hand from this machine
// with "observer share" and pinned in the client.
NetworkOptions network =
    builder.Configuration.GetSection(NetworkOptions.SectionName).Get<NetworkOptions>() ?? new NetworkOptions();

network.Validate();

if (network.Https)
{
    ProvisionedCertificate certificate = CertificateProvisioning.Provision(
        credentialStorePath,
        Environment.MachineName,
        DateTimeOffset.UtcNow,
        runningAsService);

    if (certificate.Origin == CertificateOrigin.Ephemeral)
    {
        // As with the throwaway token: Console and not the logger, because this line is for
        // whoever has just launched the service from a terminal and has to be seen at once.
        Console.WriteLine(
            "Observer could not secure a machine certificate, so this run uses a throwaway one. " +
            "Its fingerprint changes at every start, so no dashboard that pinned the previous " +
            "one will connect.");
    }

    // ListenAnyIP and not ListenLocalhost: the point of this port is that the other machines
    // use it. Whoever watches the one they are sitting at goes through the local channel, not here.
    builder.WebHost.ConfigureKestrel(kestrel =>
        kestrel.ListenAnyIP(network.HttpsPort, listenOptions => listenOptions.UseHttps(certificate.Certificate)));
}

WebApplication app = builder.Build();

if (OperatingSystem.IsLinux() && socketPath is { } localSocketPath)
{
    // The file mode has to be imposed AFTER the start: before that the file does not exist, and
    // a chmod next to the directory creation would fail.
    // Which path was chosen need not be printed here: /run/observer is not creatable
    // by a normal user and the fallback changes the path, but Kestrel already says so itself
    // in its "Now listening on: http://unix:/..." line.
    LinuxUnixSocket.RestrictAfterStart(app.Lifetime, localSocketPath);
}

app.UseObserverAccessControl(credentials.Credentials);

// AFTER the access control, and the order is measured. This way the responses the middleware
// short-circuits - 401 and 404 - do not go through the compressor: there is no point spending CPU
// on a caller without the credential, and it is also free to honour, because those bodies
// are zero bytes. What gets compressed stays all of it: the endpoint middleware is
// appended at the end by app.Run(), so anything registered here runs before it.
app.UseResponseCompression();

// The catalog describes the metrics that exist, including the ones not measurable here: it is
// what lets the client draw a metric it did not know at compile time.
app.MapGet("/metrics/catalog", (IReadOnlyList<IMetricCollector> collectors) =>
    collectors.Select(c => new { collectorId = c.Id, descriptors = c.Descriptors }));

// Reads ONLY from the cache: the endpoints never sample, otherwise two simultaneous
// requests would skew the CPU percentage computation.
app.MapGet("/metrics/latest", (MetricSnapshotCache cache) =>
    cache.Latest is { } snapshot
        ? Results.Ok(snapshot)
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

// Mapped AFTER the middleware above, like the other two: history says when the
// machine is on and how hard it works, that is more than a single sample says.
app.MapStorageEndpoints();

// Who is consuming the machine, and how to stop them. It is the only endpoint group that does
// not just read: /processes/{pid}/kill destroys state, and for that reason it logs every
// attempt with the caller's origin.
app.MapProcessEndpoints();

app.Run();
