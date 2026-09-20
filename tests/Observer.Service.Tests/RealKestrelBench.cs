using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Observer.Service.Tests;

/// <summary>
/// A REAL Kestrel host, with real transports, started inside the test.
/// </summary>
/// <remarks>
/// It is needed because WebApplicationFactory replaces Kestrel with an in-memory TestServer:
/// verified, and it means that none of the pre-existing tests exercises a transport. A named
/// pipe or a unix socket do not exist at all under TestServer, and not even the Kestrel section
/// of appsettings.json gets parsed: it is the reason a wrong endpoint URL used to pass CI green.
/// </remarks>
public sealed class RealKestrelBench : IAsyncDisposable
{
    private readonly WebApplication app;

    private RealKestrelBench(WebApplication app, IReadOnlyList<string> addresses)
    {
        this.app = app;
        Addresses = addresses;
    }

    /// <summary>The addresses the host is really listening on.</summary>
    public IReadOnlyList<string> Addresses { get; }

    /// <summary>The configuration the host really has, for checking what it did NOT inherit.</summary>
    public IConfiguration Configuration => app.Configuration;

    /// <summary>Starts the host with the given listeners, plus a test endpoint.</summary>
    /// <param name="listen">The endpoints to open.</param>
    /// <param name="map">Extra endpoints, for the tests that need them.</param>
    /// <param name="middleware">
    /// Middleware to install BEFORE the endpoints. It is there to mount the service's REAL
    /// access control, so the tests exercise it instead of checking a copy of it.
    /// </param>
    /// <returns>The bench, already started.</returns>
    /// <param name="services">
    /// Services to register before the host is built. It is needed by any test that mounts a
    /// REAL endpoint group rather than a lambda, and the failure without it is worth knowing:
    /// endpoints are built lazily on the first request, and one endpoint whose parameters cannot
    /// be bound takes the whole build down - so EVERY route answers 500, including routes that
    /// have nothing to do with the missing service and including <c>/ping</c>. It looks like a
    /// broken host, not like a missing registration.
    /// </param>
    public static async Task<RealKestrelBench> StartAsync(
        Action<KestrelServerOptions> listen,
        Action<WebApplication>? map = null,
        Action<WebApplication>? middleware = null,
        Action<IServiceCollection>? services = null)
    {
        ArgumentNullException.ThrowIfNull(listen);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        // Mandatory. The test output contains appsettings.json, and alongside it whatever
        // appsettings.Development.json / appsettings.Local.json exist, COPIED from
        // Observer.Service. Without this line the bench inherits the real service's storage
        // path, pipe name and HTTPS port, plus any Observer__* variables set in the process,
        // and the tests would collide with the instance installed on the machine running them.
        // RealKestrelBenchTests asserts on the configuration itself, so deleting this line
        // turns that test red.
        builder.Configuration.Sources.Clear();

        builder.WebHost.ConfigureKestrel(listen);
        builder.Logging.ClearProviders();

        services?.Invoke(builder.Services);

        WebApplication app = builder.Build();

        middleware?.Invoke(app);

        app.MapGet("/ping", () => "pong");
        map?.Invoke(app);

        await app.StartAsync().ConfigureAwait(false);

        IReadOnlyList<string> addresses =
            app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];

        return new RealKestrelBench(app, addresses);
    }

    /// <summary>A client that talks to this host through the given handler.</summary>
    /// <param name="handler">The handler, typically with a ConnectCallback.</param>
    /// <returns>The client, for the caller to dispose.</returns>
    public static HttpClient ClientOn(HttpMessageHandler handler) =>
        // The host in the URI is arbitrary: measured, it ends up only in the Host header and DNS
        // is never consulted. A name under .invalid makes it explicit that it must not resolve.
        new(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://local-channel.invalid/"),
            Timeout = TimeSpan.FromSeconds(10),
        };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }
}