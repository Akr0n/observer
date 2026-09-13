using Microsoft.Extensions.Primitives;
using Observer.Service.Credentials;

namespace Observer.Service.LocalChannel;

/// <summary>The service's access control, applied to every request.</summary>
/// <remarks>
/// It sits in a class and not in Program.cs's top-level statements for a precise reason: this way
/// the tests can mount it on a REAL Kestrel host and exercise the production code, instead of
/// verifying a copy rewritten in the test bench.
/// </remarks>
public static class AccessMiddleware
{
    /// <summary>Installs routing and access control, in that order.</summary>
    /// <param name="app">The application.</param>
    /// <param name="credentials">The machine credentials in use.</param>
    /// <remarks>
    /// UseRouting is called by THIS method, on purpose. The check reads the endpoint's scope
    /// from <c>GetEndpoint()</c>, which before routing is null: and with null every endpoint
    /// would come out reachable from anywhere, that is, the restriction would vanish in
    /// silence instead of failing. Keeping the two calls together makes that mistake
    /// impossible to commit.
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
                    // 404 and not 403: whoever stole the token must not be able to discover
                    // that endpoints capable of rotating the keys exist, nor use them to lock
                    // the machine's owner out.
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;

                default:
                    // The default branch is REFUSAL, not passage: if one day someone added a
                    // value to the enum without handling it here, it would fall into the
                    // 401.
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>Whether the Authorization header carries a key the service accepts.</summary>
    /// <param name="header">The header's value, possibly absent.</param>
    /// <param name="credentials">The machine credentials in use.</param>
    /// <param name="now">The current instant, for the expiry of the previous key.</param>
    /// <returns>True if it matches the current one, or the previous one not yet expired.</returns>
    /// <remarks>
    /// The credentials are a SNAPSHOT taken at start-up: a rotation done from the command line
    /// rewrites the store, and the service starts using the new key only at restart. It is
    /// deliberate - re-reading the store on every request would mean touching the disk once a
    /// second per connected machine - and it is documented in the verb that rotates.
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