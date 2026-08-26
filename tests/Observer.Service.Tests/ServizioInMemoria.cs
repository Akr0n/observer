using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// Il servizio vero, avviato in memoria, con un database temporaneo tutto suo.
/// </summary>
/// <remarks>
/// La configurazione passa dalle VARIABILI D'AMBIENTE e non da UseSetting perche' Program.cs
/// legge il token PRIMA di costruire l'host: qualunque cosa aggiunta dal test in fase di
/// Build arriverebbe troppo tardi, e il servizio si rifiuterebbe di partire.
/// </remarks>
public sealed class ServizioInMemoria : WebApplicationFactory<Program>
{
    /// <summary>Il token con cui i test si autenticano.</summary>
    public const string Token = "token-di-prova";

    private readonly string directory;

    /// <summary>Prepara la cartella temporanea e la configurazione del servizio.</summary>
    public ServizioInMemoria()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "observer-http-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(directory, "storico.db");

        Environment.SetEnvironmentVariable("Observer__ApiToken", Token);
        Environment.SetEnvironmentVariable("Observer__Storage__DatabasePath", DatabasePath);

        // La manutenzione non deve partire da sola durante i test: consoliderebbe e
        // cancellerebbe sotto ai piedi delle asserzioni.
        Environment.SetEnvironmentVariable("Observer__Storage__MaintenanceInterval", "01:00:00");
    }

    /// <summary>Percorso del database usato da questa istanza del servizio.</summary>
    public string DatabasePath { get; }

    /// <summary>Un client gia' autenticato.</summary>
    /// <returns>Il client.</returns>
    public HttpClient CreateAuthorizedClient()
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        return client;
    }

    /// <summary>Il magazzino del servizio, per seminare dati prevedibili.</summary>
    /// <returns>Il magazzino registrato nel container.</returns>
    public MetricStore Store() => Services.GetRequiredService<MetricStore>();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
