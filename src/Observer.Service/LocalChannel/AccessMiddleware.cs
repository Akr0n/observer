using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

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
    /// <param name="expectedToken">Il token atteso, gia' in byte UTF-8.</param>
    /// <remarks>
    /// UseRouting lo chiama QUESTO metodo, di proposito. Il controllo legge la portata
    /// dell'endpoint da <c>GetEndpoint()</c>, che prima dell'istradamento e' null: e con null
    /// ogni endpoint risulterebbe raggiungibile da ovunque, cioe' la restrizione sparirebbe in
    /// silenzio invece di fallire. Tenere le due chiamate insieme rende quell'errore
    /// impossibile da commettere.
    /// </remarks>
    public static void UseObserverAccessControl(this WebApplication app, byte[] expectedToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(expectedToken);

        app.UseRouting();

        app.Use(async (context, next) =>
        {
            CallerOrigin chiamante = LocalCaller.Classifica(context);
            EndpointScope portata = EndpointScopeExtensions.PortataDi(context);
            bool tokenValido = TokenValido(context.Request.Headers.Authorization, expectedToken);

            switch (AccessPolicy.Decidi(chiamante.Kind, portata, tokenValido))
            {
                case AccessDecision.Consentito:
                    break;

                case AccessDecision.NonEsiste:
                    // 404 e non 403: chi rubasse il token non deve poter scoprire che esistono
                    // endpoint capaci di ruotare le chiavi, ne' usarli per chiudere fuori il
                    // proprietario della macchina.
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;

                default:
                    // Il ramo predefinito e' il RIFIUTO, non il passaggio: se un giorno
                    // qualcuno aggiungesse un valore all'enum senza gestirlo qui, cadrebbe
                    // nel 401.
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>Se l'header Authorization porta esattamente il token atteso.</summary>
    /// <param name="header">Il valore dell'header, eventualmente assente.</param>
    /// <param name="expectedToken">Il token atteso, in byte UTF-8.</param>
    /// <returns>Vero se corrisponde.</returns>
    /// <remarks>
    /// Confronto a tempo costante: un confronto normale esce al primo byte diverso, e quella
    /// differenza di tempo permette di indovinare il token un carattere alla volta.
    /// </remarks>
    public static bool TokenValido(StringValues header, byte[] expectedToken)
    {
        ArgumentNullException.ThrowIfNull(expectedToken);

        string? valore = header.Count == 1 ? header[0] : null;

        if (valore is null || !valore.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        byte[] presentato = Encoding.UTF8.GetBytes(valore["Bearer ".Length..]);

        return CryptographicOperations.FixedTimeEquals(presentato, expectedToken);
    }
}