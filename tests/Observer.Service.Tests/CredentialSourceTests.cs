using System.Globalization;
using Microsoft.Extensions.Primitives;
using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// The credentials the service is serving right now, and what replacing them does.
/// </summary>
/// <remarks>
/// The behaviour under test is the whole reason <c>observer rotate-key --now</c> can promise
/// anything: before this type, rewriting the store left the running service accepting the old
/// key until somebody restarted it.
/// </remarks>
public sealed class CredentialSourceTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "observer-source-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    public CredentialSourceTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AReloadMakesTheNewKeyAcceptedAndTheOldOneREFUSED()
    {
        // The whole point, in one test. Anything weaker - "the reload returned Applied", "the
        // file changed" - would pass on a service that went on honouring the leaked key, which
        // is the exact failure this type exists to remove.
        string path = StoreAt();
        CredentialStore.Write(path, new MachineCredentials("old-key", null, null));

        CredentialSource source = Reading(path);

        Assert.True(Accepts(source, "old-key"));

        CredentialStore.Write(path, new MachineCredentials("new-key", null, null));

        Assert.Equal(ReloadResult.Applied, source.Reload().Result);
        Assert.True(Accepts(source, "new-key"));
        Assert.False(Accepts(source, "old-key"));
    }

    [Fact]
    public void TheStampReportedIsTheOneOfTheFileThatWasREAD()
    {
        // It is the only thing that lets the caller believe the answer: it wrote the store and
        // knows its stamp, so an equal stamp here says the running service read those bytes,
        // rather than that some service re-read something.
        string path = StoreAt();
        CredentialStore.Write(path, new MachineCredentials("k", null, null));

        ReloadOutcome outcome = Reading(path).Reload();

        Assert.Equal(ReloadResult.Applied, outcome.Result);
        Assert.Equal(File.GetLastWriteTimeUtc(path), outcome.StoreWrittenAt);
    }

    [Fact]
    public void AServiceGivenItsTokenInConfigurationHasNothingToReload()
    {
        // Observer:ApiToken wins over the store by design - it is what CI and the tests use - so
        // rewriting the store must NOT quietly replace a token the service was told explicitly.
        // The caller has to hear that, or it would read the reload as done.
        CredentialSource source = new(new ProvisionedCredentials(
            new MachineCredentials("configured", null, null), CredentialOrigin.Configuration, null));

        Assert.Equal(ReloadResult.NoStore, source.Reload().Result);
        Assert.True(Accepts(source, "configured"));
    }

    [Fact]
    public void AStoreThatDisappearedLeavesTheKeyInForceAlone()
    {
        // Wiping the credentials because the file went missing would lock the owner out of a
        // machine that is otherwise healthy - the opposite of what the rotation asked for. The
        // service keeps serving what it has, and says what happened.
        string path = StoreAt();
        CredentialStore.Write(path, new MachineCredentials("still-good", null, null));

        CredentialSource source = Reading(path);
        File.Delete(path);

        ReloadOutcome outcome = source.Reload();

        Assert.Equal(ReloadResult.StoreMissing, outcome.Result);
        Assert.Null(outcome.StoreWrittenAt);
        Assert.True(Accepts(source, "still-good"));
    }

    [Fact]
    public void AStoreThatCannotBeUnderstoodLeavesTheKeyInForceAlone()
    {
        // A hand-edited or damaged store. Same rule as a missing one, and it matters more here:
        // this is the shape where a naive implementation deserialises to null and swaps in
        // nothing at all.
        string path = StoreAt();
        CredentialStore.Write(path, new MachineCredentials("still-good", null, null));

        CredentialSource source = Reading(path);
        File.WriteAllText(path, "{ this is not json");

        ReloadOutcome outcome = source.Reload();

        Assert.Equal(ReloadResult.StoreUnusable, outcome.Result);
        Assert.True(Accepts(source, "still-good"));
    }

    [Fact]
    public void TheDescriptionSaysWhetherAPreviousKeySurvivesAndCarriesNoKey()
    {
        // What the endpoint sends back to the operator, so it must be secret-free by
        // construction - and it is the line that tells a graceful rotation apart from an
        // immediate one, which is the difference the whole --now flag is about.
        DateTimeOffset now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

        string graceful = Describing(new MachineCredentials("fresh", "leaked", now.AddHours(24)));
        string immediate = Describing(new MachineCredentials("fresh", null, null));

        Assert.DoesNotContain("fresh", graceful, StringComparison.Ordinal);
        Assert.DoesNotContain("leaked", graceful, StringComparison.Ordinal);
        Assert.Contains("previous key", graceful, StringComparison.Ordinal);

        Assert.DoesNotContain("fresh", immediate, StringComparison.Ordinal);
        Assert.Contains("no previous key", immediate, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeaderThatIsNotABearerTokenIsNeverAccepted()
    {
        // The parsing moved here from the middleware when the credentials became replaceable, so
        // the shapes that used to be checked there are checked here. The empty-string row is the
        // one a badly written comparison turns into a free pass.
        CredentialSource source = new(new ProvisionedCredentials(
            new MachineCredentials("k", null, null), CredentialOrigin.Configuration, null));

        Assert.False(Accepts(source, string.Empty));
        Assert.False(source.IsTokenValid(StringValues.Empty, DateTimeOffset.UtcNow));
        Assert.False(source.IsTokenValid("k", DateTimeOffset.UtcNow));
        Assert.False(source.IsTokenValid("bearer k", DateTimeOffset.UtcNow));
        Assert.False(source.IsTokenValid(new StringValues(["Bearer k", "Bearer k"]), DateTimeOffset.UtcNow));
        Assert.True(source.IsTokenValid("Bearer k", DateTimeOffset.UtcNow));
    }

    private static string Describing(MachineCredentials credentials) =>
        new CredentialSource(
            new ProvisionedCredentials(credentials, CredentialOrigin.Configuration, null)).Describe();

    private static bool Accepts(CredentialSource source, string token) =>
        source.IsTokenValid("Bearer " + token, DateTimeOffset.UtcNow);

    private static CredentialSource Reading(string path) =>
        new(new ProvisionedCredentials(
            CredentialStore.Read(path) ?? throw new InvalidOperationException("no store to read"),
            CredentialOrigin.Stored,
            path));

    private string StoreAt() => Path.Combine(directory, CredentialDirectory.FileName);
}
