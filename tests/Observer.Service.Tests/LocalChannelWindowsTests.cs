using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using Observer.Service.Credentials;
using Observer.Service.LocalChannel;

namespace Observer.Service.Tests;

/// <summary>Il canale locale su Windows: la pipe si apre, convive col TCP, e la DACL e' quella voluta.</summary>
[Collection(ProcessEnvironment.Name)]
public class LocalChannelWindowsTests
{
    [WindowsOnly]
    [SupportedOSPlatform("windows")]
    public void PipeSecurityGrantsInteractiveNotAuthenticatedUsers()
    {
        // Authenticated Users comprende OGNI principal autenticato che raggiunga la macchina,
        // anche via SMB sulla porta 445. INTERACTIVE comprende solo chi ha una sessione qui.
        string sddl = WindowsNamedPipe.SecurityDescriptor()
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        Assert.Contains(";;;IU)", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;AU)", sddl, StringComparison.Ordinal);
    }

    [WindowsOnly]
    [SupportedOSPlatform("windows")]
    public void CurrentUserOnlyStaysOffONLYWithAPipeSecurityDescriptor()
    {
        // Regressione su un guasto che parte SENZA errori. Misurato: CurrentUserOnly = false da
        // solo produce una pipe con DACL (A;;FR;;;WD)(A;;FR;;;AN), cioe' leggibile da Everyone e
        // da ANONYMOUS LOGON, e l'host parte normalmente. Questo test esiste proprio perche'
        // quel guasto non ha alcun sintomo visibile.
        NamedPipeTransportOptions options = new();

        WindowsNamedPipe.ConfigureTransport(options);

        Assert.False(options.CurrentUserOnly);
        Assert.NotNull(options.PipeSecurity);
    }

    [WindowsOnly]
    public async Task PipeAndTcpCoexistInTheSameHostAndServeTheSameEndpoints()
    {
        // La convivenza dei due trasporti e' la premessa dell'intero progetto: se
        // ListenNamedPipe sostituisse il trasporto socket invece di affiancarlo servirebbero
        // due host, e il piano cambierebbe forma.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.ListenNamedPipe(pipe);
        });

        Assert.Equal(2, bench.Addresses.Count);

        string tcp = bench.Addresses.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient overTcp = new() { BaseAddress = new Uri(tcp) };
        Assert.Equal("pong", await overTcp.GetStringAsync("ping", CancellationToken.None));

        using HttpClient overPipe = RealKestrelBench.ClientOn(PipeHandler(pipe));
        Assert.Equal("pong", await overPipe.GetStringAsync("ping", CancellationToken.None));
    }

    [WindowsOnly]
    public async Task ThePipeAcceptsMoreThanOneConnection()
    {
        // La PRIMA istanza si crea sempre: e' dalla SECONDA che serve FILE_CREATE_PIPE_INSTANCE,
        // e Kestrel ne apre piu' d'una. Una DACL che concede troppo poco fa fallire il bind con
        // il fuorviante "address already in use", quindi il caso da provare e' proprio questo.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenNamedPipe(pipe));

        for (int i = 0; i < 3; i++)
        {
            using HttpClient client = RealKestrelBench.ClientOn(PipeHandler(pipe));
            Assert.Equal("pong", await client.GetStringAsync("ping", CancellationToken.None));
        }
    }

    [WindowsOnly]
    public async Task ACallerConnectingThroughDotIsLocalAndIdentified()
    {
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenNamedPipe(pipe),
            app => app.MapGet("/chi", (HttpContext context) =>
            {
                CallerOrigin origin = LocalCaller.Classify(context);
                return origin.Kind + "|" + (origin.Sid ?? "(nessuno)");
            }));

        using HttpClient client = RealKestrelBench.ClientOn(PipeHandler(pipe));
        string outcome = await client.GetStringAsync("chi", CancellationToken.None);

        Assert.StartsWith(nameof(CallerKind.LocalIdentified) + "|S-1-", outcome, StringComparison.Ordinal);
    }

    [WindowsOnly]
    public async Task AnonymousImpersonationIsUnidentifiedAndDoesNotCauseA500()
    {
        // Il livello di impersonation lo sceglie il CLIENT: con Anonymous la richiesta arriva lo
        // stesso ma il server non riesce a leggere il token. E' il caso di ATTACCO, non un caso
        // limite. Misurato: l'eccezione e' SecurityException con HRESULT 0x80070543, NON
        // IOException. Una guardia che catturasse solo IOException lascerebbe uscire un 500
        // proprio sul percorso che si sta cercando di chiudere, e un 500 e' il segnale che dice
        // a chi sonda di aver toccato qualcosa.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenNamedPipe(pipe),
            app => app.MapGet("/chi", (HttpContext context) => LocalCaller.Classify(context).Kind.ToString()));

        using HttpClient client = RealKestrelBench.ClientOn(
            PipeHandler(pipe, TokenImpersonationLevel.Anonymous));

        using HttpResponseMessage response = await client.GetAsync("chi", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            nameof(CallerKind.Unidentified),
            await response.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [WindowsOnly]
    public async Task LocalhostIsNotALocalRoute()
    {
        // Misurato: con serverName "localhost" GetNamedPipeClientComputerName RIESCE e
        // restituisce "[::1]", cioe' la connessione e' passata da SMB. Solo "." e' locale.
        // E' la trappola che farebbe perdere ore a chi scrivera' il client.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenNamedPipe(pipe),
            app => app.MapGet("/chi", (HttpContext context) => LocalCaller.Classify(context).Kind.ToString()));

        using HttpClient client = RealKestrelBench.ClientOn(
            PipeHandler(pipe, TokenImpersonationLevel.Identification, server: "localhost"));

        Assert.Equal(
            nameof(CallerKind.FromNetwork),
            await client.GetStringAsync("chi", CancellationToken.None));
    }

    [WindowsOnly]
    public async Task ACallerOnTcpIsNeverLocal()
    {
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                options.ListenNamedPipe(pipe);
            },
            app => app.MapGet("/chi", (HttpContext context) => LocalCaller.Classify(context).Kind.ToString()));

        string tcp = bench.Addresses.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient client = new() { BaseAddress = new Uri(tcp) };

        Assert.Equal(
            nameof(CallerKind.FromNetwork),
            await client.GetStringAsync("chi", CancellationToken.None));
    }

    [WindowsOnly]
    public async Task TheLocalChannelNoLongerNeedsTheToken()
    {
        // E' l'obiettivo dell'intero progetto, e il primo cambiamento di comportamento
        // visibile: sulla macchina il sistema operativo sa gia' chi chiama.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                options.ListenNamedPipe(pipe);
            },
            middleware: app => app.UseObserverAccessControl(Token));

        using HttpClient overPipe = RealKestrelBench.ClientOn(PipeHandler(pipe));
        Assert.Equal("pong", await overPipe.GetStringAsync("ping", CancellationToken.None));

        // Sul TCP invece non cambia niente: rendere facoltativo il token in locale non lo
        // rende facoltativo in rete.
        string tcp = bench.Addresses.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient overTcp = new() { BaseAddress = new Uri(tcp) };
        using HttpResponseMessage withoutToken = await overTcp.GetAsync("ping", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, withoutToken.StatusCode);
    }

    [WindowsOnly]
    public async Task AnAnonymousCallerIsRefusedEVENWithTheRightToken()
    {
        // La regola "l'identita' non determinabile rifiuta" non deve avere una scappatoia. Il
        // livello di impersonation lo sceglie il CLIENT: con Anonymous un chiamante si rende
        // unilateralmente non identificabile pur restando capace di presentare il token. Se il
        // token bastasse, la regola sarebbe vuota.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.ListenNamedPipe(pipe),
            middleware: app => app.UseObserverAccessControl(Token));

        using HttpClient client = RealKestrelBench.ClientOn(
            PipeHandler(pipe, TokenImpersonationLevel.Anonymous));

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestToken);

        using HttpResponseMessage response = await client.GetAsync("ping", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [WindowsOnly]
    public async Task ALocalOnlyEndpointDoesNotExistForACallerFromTheNetwork()
    {
        // 404 e non 403: gli endpoint di appaiamento ruoteranno le chiavi, e chi rubasse il
        // token non deve nemmeno poter confermare che esistano.
        string pipe = UniquePipeName();

        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                options.ListenNamedPipe(pipe);
            },
            app => app.MapGet("/riservato", () => "segreto").LocalOnly(),
            middleware: app => app.UseObserverAccessControl(Token));

        string tcp = bench.Addresses.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient overTcp = new() { BaseAddress = new Uri(tcp) };
        overTcp.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestToken);

        using HttpResponseMessage fromNetwork = await overTcp.GetAsync("riservato", CancellationToken.None);

        // Col token GIUSTO, e comunque 404.
        Assert.Equal(HttpStatusCode.NotFound, fromNetwork.StatusCode);

        using HttpClient overPipe = RealKestrelBench.ClientOn(PipeHandler(pipe));
        Assert.Equal("segreto", await overPipe.GetStringAsync("riservato", CancellationToken.None));
    }

    /// <summary>Il token usato dai test di controllo d'accesso.</summary>
    internal const string TestToken = "token-del-banco";

    internal static MachineCredentials Token =>
        new(TestToken, null, null);

    internal static string UniquePipeName() =>
        "observer-test-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    internal static SocketsHttpHandler PipeHandler(
        string name,
        TokenImpersonationLevel level = TokenImpersonationLevel.Identification,
        string server = ".") =>
        new()
        {
            ConnectCallback = async (_, cancel) =>
            {
                // "." e NON "localhost": misurato, localhost passa da SMB e verrebbe
                // classificato come chiamante remoto.
                NamedPipeClientStream stream = new(
                    server, name, PipeDirection.InOut, PipeOptions.Asynchronous, level);

                await stream.ConnectAsync(cancel).ConfigureAwait(false);
                return stream;
            },
        };
}