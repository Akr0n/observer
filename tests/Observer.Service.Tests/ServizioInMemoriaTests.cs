namespace Observer.Service.Tests;

/// <summary>
/// Raggruppa le prove che toccano stato GLOBALE del processo.
/// </summary>
/// <remarks>
/// xunit esegue in parallelo le classi che non dichiarano una collezione, e queste prove
/// scrivono variabili d'ambiente e svuotano i pool di SQLite: due cose che non appartengono a
/// un test ma all'intero processo. Senza questa collezione, il banco che il canale locale dovra'
/// costruire (host Kestrel veri, nomi di pipe, percorsi di socket) leggerebbe le variabili
/// impostate da un'altra classe a meta' della propria esecuzione, e il guasto comparirebbe a
/// caso su un runner di CI e non sull'altro.
/// </remarks>
/// <para>
/// La collezione porta anche <see cref="ServizioInMemoria"/>, e quindi il servizio in memoria
/// e' UNO SOLO per tutte le classi che lo usano. Con una fixture per classe ce n'erano due, e
/// su Linux la seconda non partiva: il canale locale crea <c>/run/user/N/observer/</c>
/// all'avvio e la rimuove alla chiusura, quindi il primo banco che finiva portava via la
/// cartella al secondo, che falliva con "Could not find file ... observer.sock". Senza host
/// nessuno chiamava <c>MetricStore.Initialize()</c>, e le prove sullo storico morivano con
/// "no such table: series" — un messaggio che non nomina la causa nemmeno da lontano.
/// Su Windows non si vedeva: una named pipe non ha una cartella da rimuovere.
/// </para>
[CollectionDefinition(Nome)]
public sealed class AmbienteDelProcesso : ICollectionFixture<ServizioInMemoria>
{
    /// <summary>Il nome della collezione, per non ripeterlo come stringa in giro.</summary>
    public const string Nome = "ambiente-del-processo";
}

/// <summary>
/// Prove sul banco stesso: se il banco sporca il processo, sporca i test degli altri.
/// </summary>
[Collection(AmbienteDelProcesso.Nome)]
public class ServizioInMemoriaTests
{
    [Fact]
    public void DopoIlDispose_LeVariabiliDAmbienteTornanoComeErano()
    {
        // La fixture configura il servizio dalle variabili d'ambiente perche' Program.cs legge
        // il token PRIMA di costruire l'host: e' una scelta obbligata, non un difetto. Il
        // difetto e' non rimetterle a posto, perche' quelle variabili sopravvivono alla fixture
        // e restano addosso a chiunque venga dopo.
        string? tokenPrima = Environment.GetEnvironmentVariable("Observer__ApiToken");
        string? databasePrima = Environment.GetEnvironmentVariable("Observer__Storage__DatabasePath");
        string? manutenzionePrima = Environment.GetEnvironmentVariable("Observer__Storage__MaintenanceInterval");

        using (ServizioInMemoria servizio = new())
        {
            Assert.Equal(ServizioInMemoria.Token, Environment.GetEnvironmentVariable("Observer__ApiToken"));
            Assert.Equal(servizio.DatabasePath, Environment.GetEnvironmentVariable("Observer__Storage__DatabasePath"));
        }

        Assert.Equal(tokenPrima, Environment.GetEnvironmentVariable("Observer__ApiToken"));
        Assert.Equal(databasePrima, Environment.GetEnvironmentVariable("Observer__Storage__DatabasePath"));
        Assert.Equal(manutenzionePrima, Environment.GetEnvironmentVariable("Observer__Storage__MaintenanceInterval"));
    }
}