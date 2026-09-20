using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Observer.Cli;

/// <summary>What became of the running service after the store was rewritten.</summary>
/// <remarks>
/// The zero value is the one that claims nothing, and that is the discipline of this whole type:
/// a verb whose promise is "the leaked key is dead" must never print that on a code path nobody
/// thought about. Every state that is not <see cref="Applied"/> or <see cref="NotRunning"/>
/// exits non-zero.
/// </remarks>
public enum RevocationState
{
    /// <summary>Something happened that has no sentence yet. Reported as not applied.</summary>
    Unknown = 0,

    /// <summary>The running service read the exact file that was just written.</summary>
    Applied,

    /// <summary>Nothing is running here, so nothing on this machine accepts anything.</summary>
    NotRunning,

    /// <summary>A service is running and was not reached: it still holds the old key.</summary>
    StillAccepted,

    /// <summary>The service answers but has no reload endpoint, so it predates this command.</summary>
    ServiceTooOld,

    /// <summary>The service refused this caller.</summary>
    Refused,

    /// <summary>The service has no store to adopt: it was given a token in configuration.</summary>
    NothingToAdopt,

    /// <summary>The service read a store, but not the one that was just written.</summary>
    DifferentStore,

    /// <summary>The service tried and failed, and said why.</summary>
    ServiceFailed,
}

/// <summary>The verdict, with the line to print and the exit code to use.</summary>
/// <param name="State">What happened.</param>
/// <param name="Headline">The one line that answers "is the old key dead".</param>
/// <param name="Advice">What to do about it, possibly empty.</param>
/// <param name="ExitCode">0 only when the old key is provably not accepted anywhere here.</param>
public sealed record RevocationVerdict(RevocationState State, string Headline, string Advice, int ExitCode);

/// <summary>
/// Turns what the local channel said into a verdict about the key that was just replaced.
/// </summary>
/// <remarks>
/// It is pure so that its table reads as the specification it is: these are all the ways a
/// rotation can fail to reach the running service, and each one sends the operator somewhere
/// different. The rule that shapes every row: <b>exit 0 means the old key is provably dead, or
/// provably has nothing left to answer it</b>. Anything else, including anything unforeseen,
/// says so and exits 1.
/// </remarks>
public static class Revocation
{
    /// <summary>What the service said it read: which file, and when it was written.</summary>
    /// <param name="StorePath">The store the service adopted, or null if it named none.</param>
    /// <param name="WrittenAt">When that file had last been written, or null.</param>
    public sealed record Adopted(string? StorePath, DateTimeOffset? WrittenAt);

    /// <summary>Reads what the service reported, if the answer carries it.</summary>
    /// <param name="body">The body of a 200.</param>
    /// <returns>The path and the stamp, either of which may be null.</returns>
    /// <remarks>
    /// A body that cannot be parsed yields nulls, which the judgement below turns into "did not
    /// apply" rather than into an exception: an older or unexpected shape must degrade into a
    /// warning, never into a stack trace on the verb someone runs during an incident.
    /// </remarks>
    public static Adopted Read(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new Adopted(null, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Adopted(null, null);
            }

            string? path = document.RootElement.TryGetProperty("storePath", out JsonElement store)
                && store.ValueKind == JsonValueKind.String
                    ? store.GetString()
                    : null;

            DateTimeOffset? stamp =
                document.RootElement.TryGetProperty("storeWrittenAt", out JsonElement written)
                && written.TryGetDateTimeOffset(out DateTimeOffset value)
                    ? value
                    : null;

            return new Adopted(path, stamp);
        }
        catch (JsonException)
        {
            return new Adopted(null, null);
        }
    }

    /// <summary>Judges what happened to the running service.</summary>
    /// <param name="answer">What the local channel said.</param>
    /// <param name="storePath">The store this command wrote.</param>
    /// <param name="written">When it was written.</param>
    /// <param name="reported">What the service said it adopted.</param>
    /// <param name="somethingOnThePort">Whether the machine's HTTPS port accepted a connection.</param>
    /// <returns>The verdict.</returns>
    /// <remarks>
    /// BOTH halves of the answer are compared, the path and the stamp, and the path is not
    /// decoration. The command writes to the store path it knows; the service reports the one it
    /// actually read. A service configured with a different <c>Observer:CredentialStorePath</c>
    /// answers happily about a file this command never touched - the rotation would then have
    /// rewritten a store nobody is using, while the leaked key went on being accepted. Comparing
    /// only the stamp would let that pass whenever the two files happened to share a modification
    /// time, and would report success for a rotation that achieved nothing.
    /// </remarks>
    public static RevocationVerdict Judge(
        LocalChannelAnswer answer,
        string storePath,
        DateTimeOffset written,
        Adopted reported,
        bool somethingOnThePort)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(reported);
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        if (answer.Silent)
        {
            // A silent local channel has two causes with opposite meanings, and printing one
            // sentence for both is how an operator ends up believing a running service is gone.
            return somethingOnThePort
                ? new RevocationVerdict(
                    RevocationState.StillAccepted,
                    "STILL ACCEPTS THE OLD KEY - nothing answered on the local channel, but " +
                    "something is listening on this machine's port, so a service is running and " +
                    "still holds the old key in memory.",
                    RestartCommand,
                    1)
                : new RevocationVerdict(
                    RevocationState.NotRunning,
                    "NOT FOUND - nothing answered on the DEFAULT local channel and nothing is " +
                    "listening on the default port. On a standard installation that means " +
                    "nothing here accepts the old key, because nothing here is answering, and " +
                    "the service will read the new store when it starts. On a service moved to " +
                    "another pipe, socket or port, it means only that it was not found where " +
                    "this command looked.",
                    string.Empty,
                    0);
        }

        return answer.Status switch
        {
            HttpStatusCode.OK when SamePlace(storePath, reported.StorePath) && reported.WrittenAt == written =>
                new RevocationVerdict(
                RevocationState.Applied,
                "APPLIED - the running service re-read that exact file. The old key is refused " +
                "from now on.",
                string.Empty,
                0),

            HttpStatusCode.OK => new RevocationVerdict(
                RevocationState.DifferentStore,
                "DID NOT APPLY - the service re-read a store, but not the one just written" +
                    Describe(reported) + ". It may still hold the old key.",
                "Run the command again, or " + RestartCommand,
                1),

            // On the local channel a 404 cannot mean "hidden from you": the access rule answers
            // NotFound only to a caller that is not local and identified, and this one is. So
            // the route is genuinely absent, which means the service predates this command.
            HttpStatusCode.NotFound => new RevocationVerdict(
                RevocationState.ServiceTooOld,
                "TOO OLD - the service answers but has no way to re-read its credentials, so it " +
                "is still holding the old key.",
                RestartCommand,
                1),

            HttpStatusCode.Forbidden => new RevocationVerdict(
                RevocationState.Refused,
                "REFUSED - the service would not let this caller replace its credentials.",
                "Run this from a terminal started as an administrator, then " + RestartCommand,
                1),

            HttpStatusCode.Conflict => new RevocationVerdict(
                RevocationState.NothingToAdopt,
                "NOTHING TO ADOPT - the running service is not serving the stored token: " +
                Detail(answer.Body),
                "The store on disk has been rewritten all the same; whatever the service is " +
                "actually using has not changed.",
                1),

            _ => new RevocationVerdict(
                RevocationState.ServiceFailed,
                "DID NOT APPLY - the service would not adopt the store: " + Detail(answer.Body),
                RestartCommand,
                1),
        };
    }

    /// <summary>The command that restarts the service on this system.</summary>
    /// <remarks>
    /// Named for the system it is running on, because printing one that does not exist here
    /// sends the reader looking for why it does not work.
    /// </remarks>
    private static string RestartCommand =>
        OperatingSystem.IsWindows()
            ? "restart it NOW: Restart-Service Observer"
            : "restart it NOW: sudo systemctl restart observer";

    /// <summary>Whether two paths name the same file, as this system decides it.</summary>
    /// <remarks>
    /// Resolved before comparing, so a relative or unnormalised configuration value does not read
    /// as a different file; and case-insensitive on Windows only, because on Linux two paths
    /// differing in case really are two files - and two credential stores.
    /// </remarks>
    private static bool SamePlace(string written, string? reported) =>
        reported is { Length: > 0 } other
        && string.Equals(
            Path.GetFullPath(written),
            Path.GetFullPath(other),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Describe(Adopted reported) =>
        (reported.StorePath, reported.WrittenAt) switch
        {
            (null or "", null) => " (it named no file and reported no stamp)",
            (null or "", _) => " (it named no file at all)",
            (string path, null) => " (it read " + path + ", and reported no stamp)",
            (string path, DateTimeOffset stamp) => string.Create(
                CultureInfo.InvariantCulture, $" (it read {path}, written at {stamp:u})"),
        };

    /// <summary>The service's own sentence, pulled out of the problem body it answered with.</summary>
    private static string Detail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "it gave no reason.";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("detail", out JsonElement detail)
                && detail.GetString() is { Length: > 0 } text
                    ? text
                    : "it gave no reason.";
        }
        catch (JsonException)
        {
            return "it gave no reason.";
        }
    }
}
