using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json;
using Observer.Core.Metrics;
using Observer.Core.Processes;

namespace Observer.App.Services;

/// <summary>
/// Reads the metrics from the service. The interface is separate from the HTTP implementation
/// only so that the view model can also be built with a fake client.
/// </summary>
public interface IMetricsClient
{
    /// <summary>The endpoint being queried, to show on screen. It never prints the token.</summary>
    ObserverEndpoint Endpoint { get; }

    /// <summary>Reads the latest sample.</summary>
    Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken);

    /// <summary>Reads the metric catalog.</summary>
    Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken);

    /// <summary>Reads the history of one series.</summary>
    /// <param name="query">Which series, from when, at what resolution.</param>
    /// <param name="cancellationToken">Cancelled on shutdown.</param>
    /// <returns>The points, or the reason why there are none.</returns>
    Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken);

    /// <summary>Who is consuming a resource on that machine.</summary>
    /// <param name="by">Which resource: <c>cpu</c> or <c>memory</c>.</param>
    /// <param name="top">How many rows at most.</param>
    /// <param name="cancellationToken">Cancelled on shutdown.</param>
    /// <returns>The rows, or the reason why there are none.</returns>
    /// <remarks>
    /// It has a default implementation so that the test doubles that exist for other reasons are
    /// not forced to fake this one too: a fake that cannot list processes says so, instead of
    /// returning an empty list that looks like an answer.
    /// </remarks>
    Task<ProcessFetch> GetProcessesAsync(string by, int top, CancellationToken cancellationToken) =>
        Task.FromResult(new ProcessFetch(
            ServiceOutcome.Unknown, "this client cannot list processes", []));

    /// <summary>Terminates a process on that machine.</summary>
    /// <param name="pid">The process identifier.</param>
    /// <param name="name">
    /// The name shown for that pid. It travels with the request and the service compares it with
    /// the live process before signalling anything: the pid on screen is as old as the last
    /// list, and by then the system may have given the number to something else.
    /// </param>
    /// <param name="cancellationToken">Cancelled on shutdown.</param>
    /// <returns>How it went.</returns>
    Task<KillFetch> KillProcessAsync(int pid, string name, CancellationToken cancellationToken) =>
        Task.FromResult(new KillFetch(
            ServiceOutcome.Unknown, "this client cannot terminate processes"));
}

/// <summary>Which piece of the history is wanted.</summary>
/// <param name="Collector">The collector identifier.</param>
/// <param name="Metric">The metric identifier.</param>
/// <param name="Instance">The instance, when the metric has more than one.</param>
/// <param name="From">The start of the window.</param>
/// <param name="Resolution">"raw", "1m", "5m". <b>Never "auto"</b>: see the remarks.</param>
/// <remarks>
/// The resolution must always be stated. With "auto" the service picks by the width of the
/// window, and over an hour it picks raw: three thousand six hundred points to draw sixty,
/// that is half a megabyte on the wire at every refresh to throw 98 per cent of it away.
/// </remarks>
public sealed record HistoryQuery(
    string Collector,
    string Metric,
    string? Instance,
    DateTimeOffset From,
    string Resolution);

/// <summary>One interval of the history, as it arrives from the service.</summary>
/// <param name="Timestamp">The start of the interval.</param>
/// <param name="Count">How many samples fell inside it.</param>
/// <param name="Avg">The average of the samples present.</param>
/// <param name="Min">The minimum.</param>
/// <param name="Max">The maximum.</param>
/// <param name="Last">The last sample of the interval.</param>
/// <remarks>
/// <b>Intervals with no samples do not arrive at all</b>: there is no point with <c>Count</c>
/// at zero. Whoever draws has to build their own time grid and look these points up inside
/// it — see <see cref="HistoryStrip"/>.
/// </remarks>
public sealed record HistoryPoint(
    DateTimeOffset Timestamp,
    int Count,
    double Avg,
    double Min,
    double Max,
    double Last);

/// <summary>The /metrics/history response, as it arrives on the wire.</summary>
/// <param name="Resolution">The resolution actually used.</param>
/// <param name="BucketSeconds">How many seconds one interval covers.</param>
/// <param name="Truncated">True when the service has cut off the oldest points.</param>
/// <param name="Points">The intervals that have at least one sample.</param>
public sealed record HistoryResponse(
    string Resolution,
    int BucketSeconds,
    bool Truncated,
    IReadOnlyList<HistoryPoint> Points);

/// <summary>The one field this client reads out of an <c>application/problem+json</c> body.</summary>
/// <param name="Detail">The service's own sentence about what it refused, and why.</param>
internal sealed record ProblemDetailWire(string? Detail);

/// <summary>The outcome of a history read.</summary>
/// <param name="Outcome">How it went.</param>
/// <param name="Problem">What to tell whoever is watching, when it went badly.</param>
/// <param name="Points">The points, when it went well.</param>
public sealed record HistoryFetch(
    ServiceOutcome Outcome,
    string Problem,
    IReadOnlyList<HistoryPoint>? Points);

/// <summary>
/// HTTP client towards Observer.Service.
/// </summary>
/// <remarks>
/// It lives in Observer.App and not in Observer.Core on purpose: a second consumer does not
/// exist yet, and moving it the day a second one appears costs little.
/// <para>
/// It NEVER throws because of a service fault. Every way of failing becomes a
/// <see cref="ServiceOutcome"/> with its own sentence for the screen, because whoever is
/// looking at the window has to read what is wrong, not find it empty.
/// </para>
/// </remarks>
public sealed class MetricsClient : IMetricsClient, IDisposable
{
    // Eight seconds, and the number is measured, not chosen. HttpClient's default 100 would
    // leave the window frozen with no explanation for a minute and a half; but the lower bound
    // is not the sampling period, it is HOW MUCH A REFUSAL COSTS.
    //
    // On Windows, .NET 10, six runs per address: a refused connection takes
    // 2018-2104 ms on 127.0.0.1, on [::1] and on this machine's LAN address — it is not
    // a loopback quirk, it is what a refusal costs. A dual-stack NAME pays it
    // twice, because .NET tries one address after another: with the earlier 3 seconds,
    // "localhost" on a closed port gave 3007-3034 ms and the refusal never arrived — the
    // window said "no answer, check the firewall" about a stopped service, which is
    // exactly the wrong advice.
    //
    // Six seconds were the first attempt and were not enough. Re-measured with no cap, a
    // dual-stack refusal costs 4035-4121 ms: less than two seconds of margin, and on a
    // busy machine the test over a real socket really did overrun. Eight gives almost twice
    // the measured cost and stays under the 10 seconds of StatusEscalation.GracePeriod, which is
    // the upper constraint: a budget longer than the grace period would skip the "Connecting"
    // phase entirely and open the window red.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient http;
    private readonly AuthenticationHeaderValue? authorization;

    /// <summary>The fingerprint comparison, or null if this endpoint has none.</summary>
    private readonly CertificatePinning? pinning;

    /// <summary>Builds the client on the endpoint read from the configuration.</summary>
    /// <param name="endpoint">The service to query.</param>
    public MetricsClient(ObserverEndpoint endpoint)
        : this(endpoint, PinningFor(endpoint))
    {
    }

    private MetricsClient(ObserverEndpoint endpoint, CertificatePinning? pinning)
        : this(endpoint, pinning?.Handler() ?? HandlerFor(endpoint), disposeHandler: true)
    {
        this.pinning = pinning;
    }

    private static CertificatePinning? PinningFor(ObserverEndpoint endpoint) =>
        endpoint.Fingerprint is { Length: > 0 } fingerprint ? new CertificatePinning(fingerprint) : null;

    /// <summary>Builds the client on a handler supplied from outside. The tests need it.</summary>
    /// <param name="endpoint">The service to query.</param>
    /// <param name="handler">The handler to use.</param>
    public MetricsClient(ObserverEndpoint endpoint, HttpMessageHandler handler)
        : this(endpoint, handler, disposeHandler: false)
    {
    }

    private MetricsClient(ObserverEndpoint endpoint, HttpMessageHandler handler, bool disposeHandler)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(handler);

        Endpoint = endpoint;

        // No Authorization header on the local channel, and that is not an oversight: sending the
        // token where it is not needed means keeping it exposed and gaining nothing.
        authorization = endpoint.ApiToken is { Length: > 0 } token
            ? new AuthenticationHeaderValue("Bearer", token)
            : null;

        http = new HttpClient(handler, disposeHandler) { Timeout = RequestTimeout };
    }

    /// <inheritdoc />
    public ObserverEndpoint Endpoint { get; }

    private Uri BaseAddress => Endpoint.BaseAddress;

    /// <remarks>
    /// The network branch here is a FALLBACK that never runs today: you only get to it with a
    /// remote endpoint with no fingerprint, and none exist - MachineDirectory drops the entry and
    /// ClientConfiguration never produces a Remote one without it. Whoever is looking for where
    /// decompression is really turned on will find it in <see cref="CertificatePinning.Handler"/>,
    /// which is the real path, and that is where it is written down why the local channel stays out.
    /// </remarks>
    private static SocketsHttpHandler HandlerFor(ObserverEndpoint endpoint) =>
        endpoint.Kind == EndpointKind.Local
            ? LocalChannelHandler.Create()
            : new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };

    /// <inheritdoc />
    public async Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken)
    {
        (ServiceOutcome outcome, string problem, MachineSnapshot? snapshot) =
            await ReadAsync<MachineSnapshot>("metrics/latest", cancellationToken).ConfigureAwait(false);

        if (outcome != ServiceOutcome.Ok || snapshot is null)
        {
            return new SnapshotFetch(outcome, problem, null);
        }

        if (snapshot.SchemaVersion != MachineSnapshot.CurrentSchemaVersion)
        {
            // Without this check a newer service would fill the window with zeroed fields
            // marked "Ok", which is worse than an error message.
            return new SnapshotFetch(
                ServiceOutcome.IncompatibleVersion,
                $"The service on {Endpoint.Description} uses data format version " +
                snapshot.SchemaVersion.ToString(CultureInfo.InvariantCulture) +
                ", but this application only understands version " +
                MachineSnapshot.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture) +
                ". Service and client have to be updated together.",
                null);
        }

        return new SnapshotFetch(ServiceOutcome.Ok, string.Empty, snapshot);
    }

    /// <inheritdoc />
    public async Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken)
    {
        (ServiceOutcome outcome, string problem, List<CollectorCatalogEntry>? entries) =
            await ReadAsync<List<CollectorCatalogEntry>>("metrics/catalog", cancellationToken).ConfigureAwait(false);

        return outcome == ServiceOutcome.Ok && entries is not null
            ? new CatalogFetch(ServiceOutcome.Ok, string.Empty, new MetricCatalog(entries))
            : new CatalogFetch(outcome, problem, null);
    }

    /// <inheritdoc />
    public async Task<HistoryFetch> GetHistoryAsync(
        HistoryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // It goes through the same ReadAsync as latest and catalog, and that is not laziness:
        // the token, fingerprint pinning, deadlines and error translation stay one single piece
        // of code. A second route to the service would be a second route to get wrong, and
        // getting it wrong here would mean sending the token without checking who it goes to.
        // Every part is already a string, and the instant is formatted with "O" and the
        // invariant culture: no number goes through here that a culture could write differently.
        string path = "metrics/history?collector=" + Uri.EscapeDataString(query.Collector)
            + "&metric=" + Uri.EscapeDataString(query.Metric)
            + "&from=" + Uri.EscapeDataString(query.From.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
            + "&resolution=" + Uri.EscapeDataString(query.Resolution);

        if (!string.IsNullOrEmpty(query.Instance))
        {
            path += "&instance=" + Uri.EscapeDataString(query.Instance);
        }

        (ServiceOutcome outcome, string problem, HistoryResponse? response) =
            await ReadAsync<HistoryResponse>(path, cancellationToken).ConfigureAwait(false);

        return outcome == ServiceOutcome.Ok && response is not null
            ? new HistoryFetch(ServiceOutcome.Ok, string.Empty, response.Points)
            : new HistoryFetch(outcome, problem, null);
    }

    /// <inheritdoc />
    public async Task<ProcessFetch> GetProcessesAsync(
        string by, int top, CancellationToken cancellationToken)
    {
        string path = "processes?by=" + Uri.EscapeDataString(by ?? "cpu")
            + "&top=" + top.ToString(CultureInfo.InvariantCulture);

        (ServiceOutcome outcome, string problem, ProcessListWire? response) =
            await ReadAsync<ProcessListWire>(
                path,
                cancellationToken,
                $"The service on {Endpoint.Description} doesn't know how to list processes: it " +
                "is older than this dashboard. Update Observer on that machine.")
            .ConfigureAwait(false);

        // An older service does not know "io": it answers with the CPU list and without the
        // "by" field. Showing it under the I/O title would be a lie, and the remedy is the
        // same as for the 404: update Observer on that machine.
        if (outcome == ServiceOutcome.Ok
            && response is { By: null }
            && string.Equals(by, "io", StringComparison.Ordinal))
        {
            return new ProcessFetch(
                ServiceOutcome.IncompatibleVersion,
                $"The service on {Endpoint.Description} cannot rank processes by I/O: it is " +
                "older than this dashboard. Update Observer on that machine.",
                []);
        }

        return outcome == ServiceOutcome.Ok && response is not null
            ? new ProcessFetch(
                ServiceOutcome.Ok,
                string.Empty,
                [.. response.Processes.Select(ProcessRowState.From)])
            : new ProcessFetch(outcome, problem, []);
    }

    /// <inheritdoc />
    public async Task<KillFetch> KillProcessAsync(
        int pid, string name, CancellationToken cancellationToken)
    {
        // The same rule the service applies, applied here first, and NOT as an exception. Two
        // reasons. A name that cannot be sent is not a version problem, and the 400 arm below
        // would call it one - telling somebody to update a program that would behave exactly the
        // same afterwards. And throwing would not be caught anywhere: this runs under an
        // AsyncRelayCommand with the default options, which rethrows a faulted task onto the UI
        // thread. The name comes from Process.ProcessName, which on Linux is the kernel's comm
        // and can be anything the process wrote there, so this is data, not a programming error.
        // The same rule the service applies, applied here first, and NOT as an exception. Two
        // reasons. A name that cannot be sent is not a version problem, and the 400 arm below
        // would call it one - telling somebody to update a program that would behave exactly the
        // same afterwards. And throwing would not be caught anywhere: this runs under an
        // AsyncRelayCommand with the default options, which rethrows a faulted task onto the UI
        // thread. The name comes from Process.ProcessName, which on Linux is the kernel's comm
        // and can be anything the process wrote there, so this is data, not a programming error.
        if (!ProcessNameRule.IsUsable(name))
        {
            return new KillFetch(
                ServiceOutcome.UnexpectedResponse,
                "That process's name cannot be carried in a request, so it cannot be ended from " +
                "here. Stop it on that machine instead.");
        }

        // In the query string, not in a body: this request has no body at all, and no route in
        // this service reads one. Escaped, because a process name may carry a space, a plus or
        // an ampersand, and an unescaped ampersand would arrive as a truncated name - which the
        // service would then refuse as a mismatch, reporting a conflict of names where there is
        // only a badly built URL.
        Uri address = new(
            BaseAddress,
            "processes/" + pid.ToString(CultureInfo.InvariantCulture) + "/kill?name=" +
            Uri.EscapeDataString(name));

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, address);

            if (authorization is not null)
            {
                request.Headers.Authorization = authorization;
            }

            using HttpResponseMessage response =
                await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return new KillFetch(ServiceOutcome.Ok, string.Empty);
            }

            // The ONE answer on this route whose body is read, and only because this status now
            // means two things with opposite remedies: "the operating system protects that
            // process", which is answered by picking another one, and "this caller may not stop
            // processes here", which is answered by starting the dashboard as an administrator.
            // Nothing this side knows tells them apart - the service is the only one that can,
            // and it says so in the problem detail.
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                string? detail = await ReadProblemDetailAsync(response, cancellationToken)
                    .ConfigureAwait(false);

                return new KillFetch(
                    ServiceOutcome.UnexpectedResponse,
                    detail is { Length: > 0 }
                        ? $"The service on {Endpoint.Description} refused: {detail}."
                        : $"The service on {Endpoint.Description} refused to terminate it: the " +
                          "operating system protects that process.");
            }

            return response.StatusCode switch
            {
                // The service answers 404 when that PID is gone. The same code would come
                // from a service too old to have this endpoint: that ambiguity is accepted,
                // because client and service are updated together and the frequent case is by
                // far the first one — a process can end on its own between the moment it
                // appears in the list and the click.
                HttpStatusCode.NotFound => new KillFetch(
                    ServiceOutcome.UnexpectedResponse,
                    "That process is no longer running."),

                // The number is still in use, but not by what was on screen: that process ended
                // and the system handed the pid to another one. Nothing was terminated, and the
                // list is read again immediately after, so this sentence only has to say why.
                HttpStatusCode.Conflict => new KillFetch(
                    ServiceOutcome.UnexpectedResponse,
                    "Pid " + pid.ToString(CultureInfo.InvariantCulture) + " is no longer " + name +
                    ": it ended, and that number now belongs to another process. Nothing was stopped."),

                // This build names its target, and refuses above to send a name the service would
                // not accept, so a 400 arriving here is a service asking for something this
                // dashboard does not know how to give: it is newer. Saying "replied 400" would
                // send whoever reads it to look at the network for a problem an update solves.
                HttpStatusCode.BadRequest => new KillFetch(
                    ServiceOutcome.IncompatibleVersion,
                    $"The service on {Endpoint.Description} refused a kill that did not name its " +
                    "target the way it expects: it is newer than this dashboard. Update Observer here."),

                HttpStatusCode.Unauthorized => new KillFetch(
                    ServiceOutcome.TokenRejected,
                    $"The service on {Endpoint.Description} rejected the token."),

                _ => new KillFetch(
                    ServiceOutcome.UnexpectedResponse,
                    $"The service on {Endpoint.Description} replied " +
                    ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) +
                    ", which this application doesn't know how to interpret."),
            };
        }
        catch (HttpRequestException ex)
        {
            ServiceOutcome outcome = TransportFailure.Classify(ex);

            return new KillFetch(outcome, DescribeTransportFailure(outcome, ex.Message));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new KillFetch(ServiceOutcome.TimedOut, DescribeTimeout());
        }
    }

    /// <summary>The <c>detail</c> of a problem+json answer, or null if there is not one.</summary>
    /// <remarks>
    /// Every way of not finding one returns null and the caller falls back to its own sentence:
    /// a body that is not JSON, a JSON without that field, an older service that sends no body
    /// at all. A refusal is not the moment to add a second way of failing.
    /// <para>
    /// The text is TRUNCATED. It comes from the machine being watched - trusted, since its
    /// certificate is pinned - but it lands in a status bar with one line, and a service that
    /// answered with a megabyte of prose would make the window useless rather than wrong.
    /// </para>
    /// </remarks>
    private static async Task<string?> ReadProblemDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        const int LongestUsefulDetail = 300;

        try
        {
            ProblemDetailWire? problem = await response.Content
                .ReadFromJsonAsync<ProblemDetailWire>(WireOptions, cancellationToken)
                .ConfigureAwait(false);

            if (problem?.Detail is not { Length: > 0 } detail)
            {
                return null;
            }

            string trimmed = detail.Trim();

            return trimmed.Length <= LongestUsefulDetail ? trimmed : trimmed[..LongestUsefulDetail] + "…";
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            // The body is not JSON at all: ReadFromJsonAsync refuses the content type.
            return null;
        }
        catch (HttpRequestException)
        {
            // The connection died while the body was being read. The status code already
            // arrived, so the refusal stands; only its explanation is missing.
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => http.Dispose();

    /// <summary>Reads an endpoint and turns every way of failing into an outcome with its sentence.</summary>
    /// <param name="relativePath">The path to read.</param>
    /// <param name="cancellationToken">Cancelled on shutdown.</param>
    /// <param name="notFoundExplanation">
    /// What a 404 means on THIS endpoint, when it means anything. On a recently added endpoint
    /// it means the service is older than the dashboard: that is a precise diagnosis with a
    /// precise remedy, and without it whoever is watching reads "I don't know how to interpret
    /// it" and goes looking for a defect that is not there.
    /// </param>
    private async Task<(ServiceOutcome Outcome, string Problem, T? Value)> ReadAsync<T>(
        string relativePath,
        CancellationToken cancellationToken,
        string? notFoundExplanation = null)
        where T : class
    {
        Uri address = new(BaseAddress, relativePath);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, address);
            if (authorization is not null)
            {
                request.Headers.Authorization = authorization;
            }

            // A deadline that covers the READ as well, not only the headers. HttpClient's
            // Timeout stops at the headers when you read with ResponseHeadersRead: measured,
            // a service that sends the headers and then stops writing held the loop frozen for
            // twenty-five seconds with nobody cancelling anything, and the window did not say
            // "Timed out" - it silently stopped updating. With compression that phase stretches
            // further still: the body arrives in pieces and is decompressed in there.
            using CancellationTokenSource deadline =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            deadline.CancelAfter(RequestTimeout);

            using HttpResponseMessage response =
                await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                    .ConfigureAwait(false);

            string code = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (
                    ServiceOutcome.TokenRejected,
                    Endpoint.Kind == EndpointKind.Local
                        ? DescribeLocalRejection(code)
                        : DescribeRejectedToken(code),
                    null);
            }

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return (
                    ServiceOutcome.NotReadyYet,
                    $"The service on {Endpoint.Description} is listening but hasn't produced its first " +
                    "reading yet. This usually clears on its own after a second or two.",
                    null);
            }

            // A 404 on an endpoint this version of the client knows and that service does not
            // is NOT an unexpected answer: it is an older service, and waiting does not update
            // it. So the outcome is IncompatibleVersion, which the status bar shows red right
            // away instead of leaving it on "Connecting".
            if (response.StatusCode == HttpStatusCode.NotFound && notFoundExplanation is { } explanation)
            {
                return (ServiceOutcome.IncompatibleVersion, explanation, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return (
                    ServiceOutcome.UnexpectedResponse,
                    $"The service on {Endpoint.Description} replied {code} ({response.ReasonPhrase}), " +
                    "which this application doesn't know how to interpret.",
                    null);
            }

            T? value = await response.Content
                .ReadFromJsonAsync<T>(WireOptions, deadline.Token)
                .ConfigureAwait(false);

            return value is null
                ? (ServiceOutcome.UnreadableResponse, DescribeUnreadableResponse("the response was empty"), null)
                : (ServiceOutcome.Ok, string.Empty, value);
        }
        catch (JsonException ex)
        {
            return (ServiceOutcome.UnreadableResponse, DescribeUnreadableResponse(ex.Message), null);
        }
        catch (NotSupportedException ex)
        {
            // A Content-Type other than JSON: it happens when you point at another service by mistake.
            return (ServiceOutcome.UnreadableResponse, DescribeUnreadableResponse(ex.Message), null);
        }
        catch (HttpRequestException ex)
        {
            // A TLS failure on an endpoint with a pinned fingerprint is NOT "unreachable",
            // and confusing the two would be the worse of the two errors: the first one is
            // expected, this one is not. The machine answers all right - it is the identity
            // that does not match. HasRejected and not just "a fingerprint is pinned": an
            // AuthenticationException can come from many TLS faults that have nothing to do
            // with the certificate, and telling the user about those as "somebody is in the
            // middle" would be a serious accusation made without evidence.
            if (pinning is { HasRejected: true } && ex.InnerException is AuthenticationException)
            {
                return (
                    ServiceOutcome.FingerprintMismatch,
                    pinning.DescribeMismatch(Endpoint.Description),
                    null);
            }

            ServiceOutcome outcome = TransportFailure.Classify(ex);

            return (outcome, DescribeTransportFailure(outcome, ex.Message), null);
        }
        catch (IOException ex)
        {
            // A connection dropped WHILE the body is arriving - the remote machine rebooting,
            // a service update, a Wi-Fi hiccup - throws IOException, which is not an
            // HttpRequestException. Without this branch it climbed up to the loop's general
            // catch, which does NOT retry: the window said "Updates stopped, close and reopen"
            // and stayed dead for the whole session, over a fault that would have cleared
            // itself on the next round. Classify walks down the InnerExceptions hunting for
            // the SocketException, so a reset becomes Unreachable and a socket timeout
            // TimedOut, instead of one single diagnosis, wrong for all of them.
            ServiceOutcome outcome = TransportFailure.Classify(ex);

            return (outcome, DescribeTransportFailure(outcome, ex.Message), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The request timeout expired, not an application shutdown: telling the two cases
            // apart here is what keeps an error from being shown while quitting.
            return (ServiceOutcome.TimedOut, DescribeTimeout(), null);
        }
    }

    /// <summary>Picks the sentence according to HOW the connection failed.</summary>
    private string DescribeTransportFailure(ServiceOutcome outcome, string detail) => outcome switch
    {
        ServiceOutcome.ConnectionRefused => DescribeConnectionRefused(detail),
        ServiceOutcome.TimedOut => DescribeTimeout(),
        _ => DescribeUnreachable(detail),
    };

    // A refusal is the most informative answer a fault can give: the packet arrived, the
    // machine replied, and all that is missing is somebody listening on that port. Saying so
    // avoids sending people off to look at the firewall, which is where the generic sentence
    // would lead.
    private string DescribeConnectionRefused(string detail) =>
        Endpoint.Kind == EndpointKind.Local
            ? "The Observer service isn't running on this machine: the local channel refused the " +
              "connection. Start the service, or run \"observer doctor\". Technical detail: " + detail
            : $"{Endpoint.Description} answered, but nothing is listening on port " +
              Endpoint.BaseAddress.Port.ToString(CultureInfo.InvariantCulture) +
              ". The machine is reachable, so Observer is stopped there or it is on another port. " +
              $"Technical detail: {detail}";

    // The opposite twin, and it is the case that cost an afternoon. A stopped service
    // REFUSES, so silence is saying something else: a machine that is off, or something
    // dropping the packets. On Windows a firewall rule applies to one profile at a time, and
    // a domain-joined machine on a home network classifies it as public.
    private string DescribeTimeout() =>
        Endpoint.Kind == EndpointKind.Local
            ? "The Observer service on this machine didn't answer within " +
              RequestTimeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) +
              " seconds. It is listening but not replying: run \"observer doctor\"."
            : $"{Endpoint.Description} didn't answer within " +
              RequestTimeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) +
              " seconds, and nothing refused the connection either. Either that machine is off, " +
              "or something is dropping the packets: check that inbound TCP " +
              Endpoint.BaseAddress.Port.ToString(CultureInfo.InvariantCulture) +
              " is allowed there, on the profile that network is classified as.";

    private string DescribeUnreachable(string detail) =>
        Endpoint.Kind == EndpointKind.Local
            ? "The Observer service isn't answering on this machine. Check that it is running, " +
              "or start it by hand. Technical detail: " + detail
            : $"Can't reach the service on {Endpoint.Description}. Check that the machine is on, " +
              "that Observer is running there, and that the address is correct. " +
              $"Technical detail: {detail}";

    private string DescribeUnreadableResponse(string detail) =>
        $"Something on {Endpoint.Description} responded, but not with a sample this application " +
        $"can read. It probably isn't Observer. Technical detail: {detail}";

    /// <summary>The 401 on the local channel: there is no token to correct.</summary>
    /// <remarks>
    /// The text for the remote path would send the user looking for a token that does not exist
    /// on their own machine. This case should not happen: when it does, the right place to look
    /// is the service's diagnostics, not a configuration file.
    /// </remarks>
    private static string DescribeLocalRejection(string code) =>
        $"The Observer service on this machine refused the request ({code}), even though it " +
        "came in on the local channel. It should not: the service serves local, identified " +
        "callers without any credential. Run \"observer doctor\" to see what it reports.";

    private string DescribeRejectedToken(string code) =>
        $"The service on {Endpoint.Description} rejected the token ({code}). The token in use " +
        $"comes {Endpoint.Origin}, and it has to be the one that machine reports when you run " +
        "\"observer share\" on it.";
}