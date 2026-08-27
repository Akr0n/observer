using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
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