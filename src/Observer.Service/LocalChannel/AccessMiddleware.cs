using Observer.Service.Credentials;

namespace Observer.Service.LocalChannel;

/// <summary>The service's access control, applied to every request.</summary>
/// <remarks>
/// It sits in a class and not in Program.cs's top-level statements for a precise reason: this way
/// the tests can mount it on a REAL Kestrel host and exercise the production code, instead of
/// verifying a copy rewritten in the test bench.
/// <para>
/// It holds a <see cref="CredentialSource"/> and not a <see cref="MachineCredentials"/>, and asks
/// it once per request. Until 0.23.1 the credentials WERE a snapshot taken at start-up, and this
/// remark said so: rotating from the command line rewrote the store while the running service
/// went on accepting the old key. That is tolerable for a planned rotation and useless for a
/// leaked one, which is what 0.24.0 changed. The cost is one acquire load per request, and the
/// rule that comes with it - nothing may hoist the credentials out of the source and keep them -
/// is enforced by the source exposing no way to do it.
/// </para>
/// </remarks>
public static class AccessMiddleware
{
    /// <summary>Installs routing and access control, in that order.</summary>
    /// <param name="app">The application.</param>
    /// <param name="credentials">The credentials in force, asked per request.</param>
    /// <remarks>
    /// UseRouting is called by THIS method, on purpose. The check reads the endpoint's scope
    /// from <c>GetEndpoint()</c>, which before routing is null: and with null every endpoint
    /// would come out reachable from anywhere, that is, the restriction would vanish in
    /// silence instead of failing. Keeping the two calls together makes that mistake
    /// impossible to commit.
    /// </remarks>
    public static void UseObserverAccessControl(this WebApplication app, CredentialSource credentials)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(credentials);

        app.UseRouting();

        app.Use(async (context, next) =>
        {
            CallerOrigin caller = LocalCaller.Classify(context);
            EndpointScope scope = EndpointScopeExtensions.ScopeOf(context);
            bool tokenIsValid = credentials.IsTokenValid(
                context.Request.Headers.Authorization, DateTimeOffset.UtcNow);

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

}