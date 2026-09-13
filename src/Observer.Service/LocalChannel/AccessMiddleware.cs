using Microsoft.Extensions.Primitives;
using Observer.Service.Credentials;

namespace Observer.Service.LocalChannel;

/// <summary>Il controllo d'accesso del servizio, applicato a ogni richiesta.</summary>
/// <remarks>
/// Sta in una classe e non nei top-level statements di Program.cs per una ragione precisa: cosi'
/// i test possono montarlo su un host Kestrel VERO ed esercitare il codice di produzione, invece
/// di verificare una copia riscritta nel banco di prova.
/// </remarks>
public static class AccessMiddleware
{
    /// <summary>Installa l'istradamento e il controllo d'accesso, in quest'ordine.</summary>
    /// <param name="app">L'applicazione.</param>
    /// <param name="credentials">Le credentials di macchina in uso.</param>
    /// <remarks>
    /// UseRouting lo chiama QUESTO metodo, di proposito. Il controllo legge la scope
    /// dell'endpoint da <c>GetEndpoint()</c>, che prima dell'istradamento e' null: e con null
    /// ogni endpoint risulterebbe raggiungibile da ovunque, cioe' la restrizione sparirebbe in
    /// silenzio invece di fallire. Tenere le due chiamate insieme rende quell'errore
    /// impossibile da commettere.
    /// </remarks>
    public static void UseObserverAccessControl(this WebApplication app, MachineCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(credentials);

        app.UseRouting();

        app.Use(async (context, next) =>
        {
            CallerOrigin caller = LocalCaller.Classify(context);
            EndpointScope scope = EndpointScopeExtensions.ScopeOf(context);
            bool tokenIsValid = IsTokenValid(context.Request.Headers.Authorization, credentials, DateTimeOffset.UtcNow);

            switch (AccessPolicy.Decide(caller.Kind, scope, tokenIsValid))
            {
                case AccessDecision.Allowed:
                    break;

                case AccessDecision.NotFound:
                    // 404 e non 403: chi rubasse il token non deve poter scoprire che esistono
                    // endpoint capaci di ruotare le chiavi, ne' usarli per chiudere fuori il
                    // proprietario della macchina.
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;

                default:
                    // Il ramo predefinito e' il RIFIUTO, non il passaggio: se un giorno
                    // qualcuno aggiungesse un value all'enum senza gestirlo qui, cadrebbe
                    // nel 401.
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>Se l'header Authorization porta una chiave che il servizio accetta.</summary>
    /// <param name="header">Il value dell'header, eventualmente assente.</param>
    /// <param name="credentials">Le credentials di macchina in uso.</param>
    /// <param name="now">L'istante corrente, per la scadenza della chiave precedente.</param>
    /// <returns>Vero se corrisponde alla corrente o alla precedente non ancora scaduta.</returns>
    /// <remarks>
    /// Le credentials sono una FOTOGRAFIA presa all'avvio: una rotazione fatta dalla riga di
    /// comando riscrive il deposito, e il servizio comincia a usare la chiave nuova solo al
    /// riavvio. E' voluto - rileggere il deposito a ogni richiesta significherebbe toccare il
    /// disco una volta al secondo per macchina collegata - ed e' documentato nel verbo che ruota.
    /// </remarks>
    public static bool IsTokenValid(StringValues header, MachineCredentials credentials, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        string? value = header.Count == 1 ? header[0] : null;

        return value is not null
            && value.StartsWith("Bearer ", StringComparison.Ordinal)
            && credentials.Accepts(value["Bearer ".Length..], now);
    }
}