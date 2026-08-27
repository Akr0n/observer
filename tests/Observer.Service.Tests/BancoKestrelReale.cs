using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Observer.Service.Tests;

/// <summary>
/// Un host Kestrel VERO, con trasporti veri, avviato dentro il test.
/// </summary>
/// <remarks>
/// Serve perche' WebApplicationFactory sostituisce Kestrel con un TestServer in memoria:
/// verificato, e significa che nessuno dei test preesistenti esercita un trasporto. Una named
/// pipe o un socket unix non esistono affatto sotto TestServer, e nemmeno la sezione Kestrel di
/// appsettings.json viene analizzata: e' il motivo per cui un URL di endpoint sbagliato passava
/// la CI verde.
/// </remarks>
public sealed class BancoKestrelReale : IAsyncDisposable
{
    private readonly WebApplication app;

    private BancoKestrelReale(WebApplication app, IReadOnlyList<string> indirizzi)
    {
        this.app = app;
        Indirizzi = indirizzi;
    }

    /// <summary>Gli indirizzi su cui l'host sta davvero ascoltando.</summary>
    public IReadOnlyList<string> Indirizzi { get; }

    /// <summary>Avvia l'host con gli ascolti indicati, piu' un endpoint di prova.</summary>
    /// <param name="ascolti">Gli endpoint da aprire.</param>
    /// <param name="mappa">Endpoint aggiuntivi, per i test che ne hanno bisogno.</param>
    /// <returns>Il banco gia' avviato.</returns>
    public static async Task<BancoKestrelReale> AvviaAsync(
        Action<KestrelServerOptions> ascolti,
        Action<WebApplication>? mappa = null)
    {
        ArgumentNullException.ThrowIfNull(ascolti);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        // Obbligatorio. L'output dei test contiene appsettings.json, appsettings.Development.json
        // e appsettings.Local.json COPIATI da Observer.Service. Senza questa riga il banco
        // eredita la porta 5057 del servizio vero e il token di sviluppo, e i test si
        // scontrerebbero con l'istanza installata sulla macchina di chi li esegue.
        builder.Configuration.Sources.Clear();

        builder.WebHost.ConfigureKestrel(ascolti);
        builder.Logging.ClearProviders();

        WebApplication app = builder.Build();

        app.MapGet("/ping", () => "pong");
        mappa?.Invoke(app);

        await app.StartAsync().ConfigureAwait(false);

        IReadOnlyList<string> indirizzi =
            app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];

        return new BancoKestrelReale(app, indirizzi);
    }

    /// <summary>Un client che parla con questo host attraverso l'handler indicato.</summary>
    /// <param name="handler">L'handler, tipicamente con un ConnectCallback.</param>
    /// <returns>Il client, da chiudere a cura del chiamante.</returns>
    public static HttpClient ClientSu(HttpMessageHandler handler) =>
        // L'host nell'URI e' arbitrario: misurato, finisce solo nell'header Host e il DNS non
        // viene interpellato. Un nome sotto .invalid rende esplicito che non deve risolversi.
        new(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://canale-locale.invalid/"),
            Timeout = TimeSpan.FromSeconds(10),
        };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }
}