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
public sealed class InMemoryService : WebApplicationFactory<Program>
{
    /// <summary>Il token con cui i test si autenticano.</summary>
    public const string Token = "token-di-prova";

    private readonly string directory;

    // Cio' che c'era PRIMA in ogni variabile toccata, per rimettercelo all'uscita. Le variabili
    // d'ambiente appartengono al PROCESSO, non a questa istanza: lasciarle addosso significa
    // che chiunque venga dopo nasce con un token e un percorso di database che non ha scelto,
    // e un guasto del genere compare a caso su un runner di CI e non sull'altro.
    private readonly List<(string Name, string? Previous)> savedVariables = [];

    /// <summary>Prepara la cartella temporanea e la configurazione del servizio.</summary>
    public InMemoryService()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "observer-http-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(directory, "storico.db");

        SetVariable("Observer__ApiToken", Token);
        SetVariable("Observer__Storage__DatabasePath", DatabasePath);

        // HTTPS spento: qui il trasporto e' finto, perche' WebApplicationFactory
        // sostituisce Kestrel con un TestServer. Generare una chiave RSA da 3072 bit e
        // provare a depositarla in una cartella di sistema costerebbe secondi a ogni
        // istanza, per una porta che non verra' mai aperta. Il TLS vero ha la sua classe
        // di prove: TrasportoHttpsTests.
        SetVariable("Observer__Network__Https", "false");

        // La manutenzione non deve partire da sola durante i test: consoliderebbe e
        // cancellerebbe sotto ai piedi delle asserzioni.
        SetVariable("Observer__Storage__MaintenanceInterval", "01:00:00");
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

    /// <summary>Imposta una variabile d'ambiente ricordando cosa c'era prima.</summary>
    private void SetVariable(string name, string? value)
    {
        savedVariables.Add((name, Environment.GetEnvironmentVariable(name)));
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        // Dopo base.Dispose: l'host e' fermo, quindi nessuno rileggera' la configurazione.
        // Prima della cancellazione della cartella, che puo' fallire: le variabili vanno
        // rimesse a posto comunque.
        foreach ((string name, string? previous) in savedVariables)
        {
            Environment.SetEnvironmentVariable(name, previous);
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
