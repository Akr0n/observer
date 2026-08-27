using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Observer.Service.LocalChannel;

namespace Observer.Service.Tests;

/// <summary>Il canale locale su Linux.</summary>
/// <remarks>
/// L attributo sulla CLASSE e non sui singoli metodi: CA1416 guarda il sito di chiamata, e
/// [SoloSuLinux] e un salto a RUNTIME che l analyzer non vede. Tutti i test qui dentro sono
/// comunque Linux, quindi annotare la classe e la forma piu onesta.
/// </remarks>
[Collection(AmbienteDelProcesso.Nome)]
[SupportedOSPlatform("linux")]
public class CanaleLocaleLinuxTests
{
    [SoloSuLinux]
    public async Task IlSocketUnixServeGliStessiEndpointDelTcp()
    {
        string percorso = PercorsoBreve();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenUnixSocket(percorso));

        using HttpClient client = BancoKestrelReale.ClientSu(HandlerVersoIlSocket(percorso));

        Assert.Equal("pong", await client.GetStringAsync("ping", CancellationToken.None));
    }

    [SoloSuLinux]
    public async Task UnaChiusuraPulitaCancellaIlFileDelSocket()
    {
        // Contro l'idea diffusa che su Linux il file sopravviva sempre: .NET fa l'unlink
        // esplicito, perche' UnixDomainSocketEndPoint porta un boundFileName. La bonifica serve
        // SOLO dopo una morte violenta.
        string percorso = PercorsoBreve();

        await using (BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenUnixSocket(percorso)))
        {
            Assert.True(File.Exists(percorso));
        }

        Assert.False(File.Exists(percorso));
    }

    [SoloSuLinux]
    public async Task LaBonificaNonRubaIlSocketAUnIstanzaViva()
    {
        // "Se il file esiste, cancellalo" permette a una seconda istanza di scippare il socket
        // a una prima istanza sana. La bonifica deve sondare, e sondare con un TIMEOUT.
        string percorso = PercorsoBreve();

        await using BancoKestrelReale vivo = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenUnixSocket(percorso));

        bool bonificato = await LinuxUnixSocket.BonificaSocketOrfanoAsync(
            percorso, TimeSpan.FromMilliseconds(500));

        Assert.False(bonificato);
        Assert.True(File.Exists(percorso));
    }

    [SoloSuLinux]
    public async Task UnSocketOrfanoVieneRimossoDallaBonifica()
    {
        string percorso = PercorsoBreve();

        await using (BancoKestrelReale morto = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenUnixSocket(percorso)))
        {
            Assert.True(File.Exists(percorso));
        }

        // Simula la morte violenta: il file resta a terra senza nessuno in ascolto.
        await File.WriteAllTextAsync(percorso, string.Empty, CancellationToken.None);

        Assert.True(await LinuxUnixSocket.BonificaSocketOrfanoAsync(percorso, TimeSpan.FromSeconds(2)));
        Assert.False(File.Exists(percorso));
    }

    [SoloSuLinux]
    public void IlModoDellaDirectoryVieneImpostatoAncheSeLaDirectoryEsisteGia()
    {
        // Directory.CreateDirectory(percorso, modo) NON applica il modo a una directory che
        // esiste gia': misurato, e' un no-op silenzioso. Quindi la protezione non esisterebbe
        // dal secondo avvio in poi, ne' su una /run/observer creata da systemd col suo 0755.
        string cartella = Path.Combine(Path.GetTempPath(), "obs-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(cartella);
        File.SetUnixFileMode(
            cartella,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        try
        {
            LinuxUnixSocket.PreparaPercorso(Path.Combine(cartella, "o.sock"));

            UnixFileMode modo = File.GetUnixFileMode(cartella);

            Assert.Equal(UnixFileMode.None, modo & UnixFileMode.OtherRead);
            Assert.Equal(UnixFileMode.None, modo & UnixFileMode.OtherWrite);
            Assert.Equal(UnixFileMode.None, modo & UnixFileMode.OtherExecute);
        }
        finally
        {
            Directory.Delete(cartella, recursive: true);
        }
    }

    [SoloSuLinux]
    public async Task IlChiamanteSuSocketUnixVieneIdentificatoDalSuoUid()
    {
        string percorso = PercorsoBreve();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenUnixSocket(percorso),
            app => app.MapGet("/chi", (HttpContext contesto) =>
            {
                CallerOrigin origine = LocalCaller.Classifica(contesto);
                return origine.Kind + "|" + (origine.Sid ?? "(nessuno)");
            }));

        using HttpClient client = BancoKestrelReale.ClientSu(HandlerVersoIlSocket(percorso));
        string esito = await client.GetStringAsync("chi", CancellationToken.None);

        // Su un socket unix il chiamante e' SEMPRE sulla stessa macchina: non esiste la via SMB
        // che c'e' su Windows. L'unica domanda e' se l'uid sia leggibile.
        string[] parti = esito.Split('|');

        Assert.Equal(nameof(CallerKind.LocaleIdentificato), parti[0]);
        Assert.True(uint.TryParse(parti[1], out _), "uid non numerico: " + parti[1]);
    }

    internal static string PercorsoBreve()
    {
        // Il limite e' 107 BYTE per l'intero percorso, e il temp di un runner di CI puo' essere
        // lungo: il percorso viene verificato, non sperato.
        string percorso = Path.Combine(
            Path.GetTempPath(),
            "o-" + Guid.NewGuid().ToString("N")[..8] + ".sock");

        Assert.Null(EndpointUrl.Problema("http://unix:" + percorso));

        return percorso;
    }

    internal static SocketsHttpHandler HandlerVersoIlSocket(string percorso) =>
        new()
        {
            ConnectCallback = async (_, annulla) =>
            {
                Socket presa = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await presa.ConnectAsync(new UnixDomainSocketEndPoint(percorso), annulla).ConfigureAwait(false);
                return new NetworkStream(presa, ownsSocket: true);
            },
        };
}