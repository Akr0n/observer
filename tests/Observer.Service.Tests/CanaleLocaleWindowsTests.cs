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
[Collection(AmbienteDelProcesso.Nome)]
public class CanaleLocaleWindowsTests
{
    [SoloSuWindows]
    [SupportedOSPlatform("windows")]
    public void LaSicurezzaDellaPipeConcedeAgliInterattiviENonAdAuthenticatedUsers()
    {
        // Authenticated Users comprende OGNI principal autenticato che raggiunga la macchina,
        // anche via SMB sulla porta 445. INTERACTIVE comprende solo chi ha una sessione qui.
        string sddl = WindowsNamedPipe.Sicurezza()
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        Assert.Contains(";;;IU)", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;AU)", sddl, StringComparison.Ordinal);
    }

    [SoloSuWindows]
    [SupportedOSPlatform("windows")]
    public void CurrentUserOnlyRestaSpentoSOLOInsiemeAllaSicurezza()
    {
        // Regressione su un guasto che parte SENZA errori. Misurato: CurrentUserOnly = false da
        // solo produce una pipe con DACL (A;;FR;;;WD)(A;;FR;;;AN), cioe' leggibile da Everyone e
        // da ANONYMOUS LOGON, e l'host parte normalmente. Questo test esiste proprio perche'
        // quel guasto non ha alcun sintomo visibile.
        NamedPipeTransportOptions opzioni = new();

        WindowsNamedPipe.ConfiguraTrasporto(opzioni);

        Assert.False(opzioni.CurrentUserOnly);
        Assert.NotNull(opzioni.PipeSecurity);
    }

    [SoloSuWindows]
    public async Task PipeETcpConvivonoNelloStessoHostEServonoGliStessiEndpoint()
    {
        // La convivenza dei due trasporti e' la premessa dell'intero progetto: se
        // ListenNamedPipe sostituisse il trasporto socket invece di affiancarlo servirebbero
        // due host, e il piano cambierebbe forma.
        string pipe = NomeUnico();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(opzioni =>
        {
            opzioni.Listen(IPAddress.Loopback, 0);
            opzioni.ListenNamedPipe(pipe);
        });

        Assert.Equal(2, banco.Indirizzi.Count);

        string tcp = banco.Indirizzi.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient suTcp = new() { BaseAddress = new Uri(tcp) };
        Assert.Equal("pong", await suTcp.GetStringAsync("ping", CancellationToken.None));

        using HttpClient suPipe = BancoKestrelReale.ClientSu(HandlerVersoLaPipe(pipe));
        Assert.Equal("pong", await suPipe.GetStringAsync("ping", CancellationToken.None));
    }

    [SoloSuWindows]
    public async Task LaPipeAccettaPiuDiUnaConnessione()
    {
        // La PRIMA istanza si crea sempre: e' dalla SECONDA che serve FILE_CREATE_PIPE_INSTANCE,
        // e Kestrel ne apre piu' d'una. Una DACL che concede troppo poco fa fallire il bind con
        // il fuorviante "address already in use", quindi il caso da provare e' proprio questo.
        string pipe = NomeUnico();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenNamedPipe(pipe));

        for (int i = 0; i < 3; i++)
        {
            using HttpClient client = BancoKestrelReale.ClientSu(HandlerVersoLaPipe(pipe));
            Assert.Equal("pong", await client.GetStringAsync("ping", CancellationToken.None));
        }
    }

    [SoloSuWindows]
    public async Task ChiArrivaDalPuntoEClassificatoLocaleEIdentificato()
    {
        string pipe = NomeUnico();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenNamedPipe(pipe),
            app => app.MapGet("/chi", (HttpContext contesto) =>
            {
                CallerOrigin origine = LocalCaller.Classifica(contesto);
                return origine.Kind + "|" + (origine.Sid ?? "(nessuno)");
            }));

        using HttpClient client = BancoKestrelReale.ClientSu(HandlerVersoLaPipe(pipe));
        string esito = await client.GetStringAsync("chi", CancellationToken.None);

        Assert.StartsWith(nameof(CallerKind.LocaleIdentificato) + "|S-1-", esito, StringComparison.Ordinal);
    }

    [SoloSuWindows]
    public async Task ChiSceglieAnonymousNonEIdentificabile_ENonProduceUn500()
    {
        // Il livello di impersonation lo sceglie il CLIENT: con Anonymous la richiesta arriva lo
        // stesso ma il server non riesce a leggere il token. E' il caso di ATTACCO, non un caso
        // limite. Misurato: l'eccezione e' SecurityException con HRESULT 0x80070543, NON
        // IOException. Una guardia che catturasse solo IOException lascerebbe uscire un 500
        // proprio sul percorso che si sta cercando di chiudere, e un 500 e' il segnale che dice
        // a chi sonda di aver toccato qualcosa.
        string pipe = NomeUnico();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenNamedPipe(pipe),
            app => app.MapGet("/chi", (HttpContext contesto) => LocalCaller.Classifica(contesto).Kind.ToString()));

        using HttpClient client = BancoKestrelReale.ClientSu(
            HandlerVersoLaPipe(pipe, TokenImpersonationLevel.Anonymous));

        using HttpResponseMessage risposta = await client.GetAsync("chi", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, risposta.StatusCode);
        Assert.Equal(
            nameof(CallerKind.NonIdentificabile),
            await risposta.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [SoloSuWindows]
    public async Task LocalhostNonEUnaViaLocale()
    {
        // Misurato: con serverName "localhost" GetNamedPipeClientComputerName RIESCE e
        // restituisce "[::1]", cioe' la connessione e' passata da SMB. Solo "." e' locale.
        // E' la trappola che farebbe perdere ore a chi scrivera' il client.
        string pipe = NomeUnico();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenNamedPipe(pipe),
            app => app.MapGet("/chi", (HttpContext contesto) => LocalCaller.Classifica(contesto).Kind.ToString()));

        using HttpClient client = BancoKestrelReale.ClientSu(
            HandlerVersoLaPipe(pipe, TokenImpersonationLevel.Identification, server: "localhost"));

        Assert.Equal(
            nameof(CallerKind.ArrivatoDallaRete),
            await client.GetStringAsync("chi", CancellationToken.None));
    }

    [SoloSuWindows]
    public async Task ChiArrivaDalTcpNonEMaiLocale()
    {
        string pipe = NomeUnico();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni =>
            {
                opzioni.Listen(IPAddress.Loopback, 0);
                opzioni.ListenNamedPipe(pipe);
            },
            app => app.MapGet("/chi", (HttpContext contesto) => LocalCaller.Classifica(contesto).Kind.ToString()));

        string tcp = banco.Indirizzi.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient client = new() { BaseAddress = new Uri(tcp) };

        Assert.Equal(
            nameof(CallerKind.ArrivatoDallaRete),
            await client.GetStringAsync("chi", CancellationToken.None));
    }

    [SoloSuWindows]
    public async Task SulCanaleLocaleIlTokenNonServePiu()
    {
        // E' l'obiettivo dell'intero progetto, e il primo cambiamento di comportamento
        // visibile: sulla macchina il sistema operativo sa gia' chi chiama.
        string pipe = NomeUnico();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni =>
            {
                opzioni.Listen(IPAddress.Loopback, 0);
                opzioni.ListenNamedPipe(pipe);
            },
            middleware: app => app.UseObserverAccessControl(Token));

        using HttpClient suPipe = BancoKestrelReale.ClientSu(HandlerVersoLaPipe(pipe));
        Assert.Equal("pong", await suPipe.GetStringAsync("ping", CancellationToken.None));

        // Sul TCP invece non cambia niente: rendere facoltativo il token in locale non lo
        // rende facoltativo in rete.
        string tcp = banco.Indirizzi.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient suTcp = new() { BaseAddress = new Uri(tcp) };
        using HttpResponseMessage senzaToken = await suTcp.GetAsync("ping", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, senzaToken.StatusCode);
    }

    [SoloSuWindows]
    public async Task UnChiamanteAnonimoVieneRifiutatoANCHEColTokenGiusto()
    {
        // La regola "l'identita' non determinabile rifiuta" non deve avere una scappatoia. Il
        // livello di impersonation lo sceglie il CLIENT: con Anonymous un chiamante si rende
        // unilateralmente non identificabile pur restando capace di presentare il token. Se il
        // token bastasse, la regola sarebbe vuota.
        string pipe = NomeUnico();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenNamedPipe(pipe),
            middleware: app => app.UseObserverAccessControl(Token));

        using HttpClient client = BancoKestrelReale.ClientSu(
            HandlerVersoLaPipe(pipe, TokenImpersonationLevel.Anonymous));

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TokenTestuale);

        using HttpResponseMessage risposta = await client.GetAsync("ping", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, risposta.StatusCode);
    }

    [SoloSuWindows]
    public async Task UnEndpointSoloLocaleNonEsistePerChiArrivaDallaRete()
    {
        // 404 e non 403: gli endpoint di appaiamento ruoteranno le chiavi, e chi rubasse il
        // token non deve nemmeno poter confermare che esistano.
        string pipe = NomeUnico();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni =>
            {
                opzioni.Listen(IPAddress.Loopback, 0);
                opzioni.ListenNamedPipe(pipe);
            },
            app => app.MapGet("/riservato", () => "segreto").SoloDaLocale(),
            middleware: app => app.UseObserverAccessControl(Token));

        string tcp = banco.Indirizzi.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient suTcp = new() { BaseAddress = new Uri(tcp) };
        suTcp.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TokenTestuale);

        using HttpResponseMessage dallaRete = await suTcp.GetAsync("riservato", CancellationToken.None);

        // Col token GIUSTO, e comunque 404.
        Assert.Equal(HttpStatusCode.NotFound, dallaRete.StatusCode);

        using HttpClient suPipe = BancoKestrelReale.ClientSu(HandlerVersoLaPipe(pipe));
        Assert.Equal("segreto", await suPipe.GetStringAsync("riservato", CancellationToken.None));
    }

    /// <summary>Il token usato dai test di controllo d'accesso.</summary>
    internal const string TokenTestuale = "token-del-banco";

    internal static MachineCredentials Token =>
        new(TokenTestuale, null, null);

    internal static string NomeUnico() =>
        "observer-test-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    internal static SocketsHttpHandler HandlerVersoLaPipe(
        string nome,
        TokenImpersonationLevel livello = TokenImpersonationLevel.Identification,
        string server = ".") =>
        new()
        {
            ConnectCallback = async (_, annulla) =>
            {
                // "." e NON "localhost": misurato, localhost passa da SMB e verrebbe
                // classificato come chiamante remoto.
                NamedPipeClientStream flusso = new(
                    server, nome, PipeDirection.InOut, PipeOptions.Asynchronous, livello);

                await flusso.ConnectAsync(annulla).ConfigureAwait(false);
                return flusso;
            },
        };
}