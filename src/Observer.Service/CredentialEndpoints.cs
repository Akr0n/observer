using Observer.Service.Credentials;
using Observer.Service.LocalChannel;

namespace Observer.Service;

/// <summary>What <c>POST /credentials/reload</c> answers when it worked.</summary>
/// <param name="StorePath">
/// The store the service actually READ, reported by the thing that read it. Not resolved from
/// configuration: an earlier version did that and CI caught it naming the default path while the
/// service had adopted another file, which is the one claim this answer exists to make.
/// </param>
/// <param name="StoreWrittenAt">
/// When that file had last been written. This is the field the whole endpoint exists for: the
/// caller has just written the store and knows its stamp, so an equal stamp here means the
/// running service read those exact bytes. Without it the answer would only say that a service
/// re-read something.
/// </param>
/// <param name="Keys">
/// The keys now in force, described without any of them in it - and it says in words whether a
/// previous key survives, which is what tells a graceful rotation apart from an immediate one.
/// </param>
public sealed record CredentialReloadResponse(string StorePath, DateTimeOffset StoreWrittenAt, string Keys);

/// <summary>
/// The endpoint that makes the running service adopt the credential store as it is now.
/// </summary>
/// <remarks>
/// <b>It is the first endpoint in this service marked local-only, and the marker was written for
/// it.</b> The comment on <see cref="AccessPolicy.Decide"/>'s <c>NotFound</c> branch has said
/// since that rule existed that whoever steals the token must not be able to rotate the keys and
/// lock the owner out: from the network this route does not exist, not even with a valid token,
/// and the answer is 404 rather than 403 so that its existence is not something a stolen token
/// can discover.
/// <para>
/// WHY IT EXISTS AT ALL. Rewriting the store does not revoke anything by itself, because the
/// service reads it once at start-up. Before this endpoint, <c>observer rotate-key</c> could only
/// print "restart the service" and hope - which is a revocation that depends on a human
/// remembering a second step, at the exact moment they have just discovered a key has leaked.
/// </para>
/// <para>
/// It answers <c>200</c> only when the store was really adopted, and everything else with the
/// service's own reason. It never reports success it has not performed: every failure leaves the
/// credentials in force untouched, and says so.
/// </para>
/// </remarks>
public static partial class CredentialEndpoints
{
    /// <summary>Maps the credential endpoints onto the application.</summary>
    /// <param name="endpoints">The route builder.</param>
    public static void MapCredentialEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Map and NOT MapPost, with the verb checked inside, and that is the difference between
        // this route being hidden and being announced. A request that matches the PATH but not
        // the METHOD does not select this endpoint at all: ASP.NET Core selects a synthetic
        // rejection endpoint whose metadata is EMPTY, so the local-only marker is not there to be
        // read, the scope comes out Anywhere, and a caller from the network is served a 405
        // carrying "Allow: POST" - while every sibling path answers 404. Measured on the real
        // pipeline. The difference is exactly the disclosure the marker exists to prevent: a
        // stolen token could not USE the route, but it could learn that the route is there.
        //
        // Marking the route GROUP does not help, which is worth knowing because the marker's own
        // remarks call that the intended way to use it: group metadata does not reach the
        // synthetic endpoint either. Measured.
        endpoints.Map("/credentials/reload", (HttpContext context, ILoggerFactory loggerFactory) =>
            HttpMethods.IsPost(context.Request.Method)
                ? Reload(context, loggerFactory)
                : Results.StatusCode(StatusCodes.Status405MethodNotAllowed)).LocalOnly();
    }

    private static IResult Reload(HttpContext context, ILoggerFactory loggerFactory)
    {
        ILogger logger = loggerFactory.CreateLogger(typeof(CredentialEndpoints));
        CallerOrigin origin = LocalCaller.Classify(context);

        // The gate FIRST, before the store is touched at all, for the same reason the kill asks
        // its rule before it validates anything: a caller who may not do this must not be able to
        // make the service open the file either.
        if (!AccessPolicy.MayReloadCredentials(origin.Kind, origin.Elevation))
        {
            LogReloadRefusedToCaller(logger, origin.Reason);

            return Results.Problem(
                detail: "this caller may not make the service re-read its credentials: the local " +
                    "channel requires a caller whose own token carries administrative rights",
                statusCode: StatusCodes.Status403Forbidden);
        }

        CredentialSource source = context.RequestServices.GetRequiredService<CredentialSource>();
        ReloadOutcome outcome = source.Reload();

        switch (outcome.Result)
        {
            case ReloadResult.Applied:
                LogReloadApplied(logger, outcome.StoreWrittenAt ?? default, origin.Reason);

                return Results.Ok(new CredentialReloadResponse(
                    outcome.StorePath ?? string.Empty,
                    outcome.StoreWrittenAt ?? default,
                    outcome.Detail));

            case ReloadResult.NoStore:
            case ReloadResult.StoreMissing:
                LogReloadFoundNothing(logger, outcome.Result, origin.Reason);

                // 409 and not 500: nothing is broken, the request simply does not apply to this
                // service as it is configured. The caller still has to be told which of the two
                // it is, because "you were given a token in configuration" and "your store is
                // gone" send the operator to completely different places.
                return Results.Problem(
                    detail: outcome.Detail,
                    statusCode: StatusCodes.Status409Conflict);

            default:
                LogReloadFailed(logger, outcome.Result, origin.Reason, outcome.Detail);

                return Results.Problem(
                    detail: outcome.Detail,
                    statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Warning,
        Message = "Credentials re-read from the store, written at {WrittenAt}, requested by {Origin}.")]
    private static partial void LogReloadApplied(ILogger logger, DateTimeOffset writtenAt, string origin);

    [LoggerMessage(
        EventId = 21,
        Level = LogLevel.Information,
        Message = "Credential reload had nothing to adopt ({Result}), requested by {Origin}.")]
    private static partial void LogReloadFoundNothing(ILogger logger, ReloadResult result, string origin);

    [LoggerMessage(
        EventId = 22,
        Level = LogLevel.Warning,
        Message = "Credential reload failed ({Result}), requested by {Origin}: {Detail}")]
    private static partial void LogReloadFailed(ILogger logger, ReloadResult result, string origin, string detail);

    [LoggerMessage(
        EventId = 23,
        Level = LogLevel.Warning,
        Message = "Credential reload refused to the caller: {Origin}")]
    private static partial void LogReloadRefusedToCaller(ILogger logger, string origin);
}
