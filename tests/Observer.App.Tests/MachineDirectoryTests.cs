using System.Security.Cryptography;
using Observer.App.Services;
using Observer.Core.Security;

namespace Observer.App.Tests;

/// <summary>
/// The machine list, and what does NOT get into it.
/// </summary>
/// <remarks>
/// The rule this class defends: an entry that is configured badly does not disappear in silence.
/// A machine that simply does not show up is indistinguishable from one that was never added,
/// and whoever goes looking for it has no way of knowing what to correct.
/// <para>
/// From today it defends a second one: <b>the token is no longer in the file</b>. It lives in
/// the system store, and an entry that still carries it around is refused even when that token
/// is the right one — accepting it "for compatibility" would mean the secret can stay there for
/// ever.
/// </para>
/// </remarks>
public class MachineDirectoryTests
{
    private static readonly string Fingerprint =
        CertificateFingerprint.From(SHA256.HashData("a machine"u8.ToArray()));

    private static ClientConfigurationResult NoOtherConfiguration() =>
        new(ObserverEndpoint.LocalChannel(), null);

    private static MachineListResult Read(string json, ISecretStore? store = null) =>
        MachineDirectory.Resolve(json, NoOtherConfiguration(), store ?? FakeSecretStore.With("laptop", "the-token"));

    /// <summary>One entry of the file. The token is passed only to prove it gets refused.</summary>
    private static string Entry(
        string? address, string? fingerprint, string name = "laptop", string? tokenInFile = null) =>
        $$"""
          { "machines": [ { "name": {{JsonValue(name)}}, "baseAddress": {{JsonValue(address)}},
            {{(tokenInFile is null ? string.Empty : "\"apiToken\": " + JsonValue(tokenInFile) + ",")}}
            "fingerprint": {{JsonValue(fingerprint)}} } ] }
          """;

    private static string JsonValue(string? value) =>
        value is null ? "null" : "\"" + value + "\"";

    [Fact]
    public void ThisMachineIsAlwaysThereAndComesFirst()
    {
        // It is not declared in the file and cannot be removed: it needs nothing at all in
        // order to work, so there is no way to get its configuration wrong.
        MachineListResult result = MachineDirectory.Resolve(null, NoOtherConfiguration(), FakeSecretStore.Empty());

        ObserverEndpoint first = Assert.Single(result.Machines);

        Assert.Equal(EndpointKind.Local, first.Kind);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void ACompleteEntryJoinsTheList()
    {
        MachineListResult result = Read(Entry("https://laptop:5058", Fingerprint));

        Assert.Empty(result.Problems);
        Assert.Equal(2, result.Machines.Count);

        ObserverEndpoint remoteMachine = result.Machines[1];

        Assert.Equal(EndpointKind.Remote, remoteMachine.Kind);
        Assert.Equal("laptop", remoteMachine.DisplayName);
        Assert.True(remoteMachine.IsFingerprintPinned);
        Assert.EndsWith("/", remoteMachine.BaseAddress.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ATokenWrittenInTheFileIsRefused()
    {
        // The heart of the change. The token below is the right one, and that is not enough: if
        // an entry with the token in the file went on working, nobody would ever take it out.
        MachineListResult result = Read(
            Entry("https://laptop:5058", Fingerprint, tokenInFile: "the-token"),
            FakeSecretStore.Empty());

        Assert.Single(result.Machines);

        string problem = Assert.Single(result.Problems);

        Assert.Contains("observer token set laptop", problem, StringComparison.Ordinal);
        Assert.Contains("ending processes", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutATokenInTheStoreItStaysOutAndSaysHowToAddOne()
    {
        MachineListResult result = Read(Entry("https://laptop:5058", Fingerprint), FakeSecretStore.Empty());

        Assert.Single(result.Machines);
        Assert.Contains(
            "observer token set laptop", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutANameThereIsNowhereToLookForTheToken()
    {
        // The name used to be optional and a machine was called after its own address. It is now
        // the key the token is looked up under in the store, so without it there is nowhere to
        // go — and that must be said, instead of making the entry disappear.
        MachineListResult result = Read(
            $$"""
              { "machines": [ { "baseAddress": "https://laptop:5058",
                "fingerprint": "{{Fingerprint}}" } ] }
              """);

        Assert.Single(result.Machines);
        Assert.Contains("\"name\"", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUntrustedStoreDropsOnlyThatEntry()
    {
        // A secrets file that others can read must not bring the whole list down: the other
        // machines have nothing to do with it, and the window must stay usable.
        MachineListResult result = Read(
            Entry("https://laptop:5058", Fingerprint),
            FakeSecretStore.ThatFails("chmod 600 and try again"));

        Assert.Single(result.Machines);
        Assert.Contains("chmod 600", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void ACleartextAddressStaysOutAndSaysWhy()
    {
        // By far the most likely case: a configuration that was right yesterday. The service no
        // longer answers in cleartext on the network, and the reason has to be spelled out.
        MachineListResult result = Read(Entry("http://laptop:5057", Fingerprint));

        Assert.Single(result.Machines);

        string problem = Assert.Single(result.Problems);

        Assert.Contains("https://", problem, StringComparison.Ordinal);
        Assert.Contains("packet capture", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAFingerprintItStaysOut()
    {
        // Encrypted is not enough. With no fingerprint, an attacker in the middle presents their
        // own certificate and the connection succeeds all the same.
        MachineListResult result = Read(Entry("https://laptop:5058", null));

        Assert.Single(result.Machines);
        Assert.Contains("fingerprint", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedFingerprintIsRejected()
    {
        // A fingerprint with a typo in it must not be patched up: it would match no certificate
        // in the world, and the message would then talk about an attack.
        MachineListResult result = Read(Entry("https://laptop:5058", "sha256:not-hexadecimal-here"));

        Assert.Single(result.Machines);
        Assert.Contains("hex digits", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void ABrokenFileDoesNotHideThisMachine()
    {
        // The window must stay usable: a malformed list cannot stop you watching the machine
        // you are sitting at.
        MachineListResult result = Read("{ not json at all");

        Assert.Single(result.Machines);
        Assert.Equal(EndpointKind.Local, result.Machines[0].Kind);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void WithNoListTheOldSingleMachineConfigurationStillApplies()
    {
        // Anyone who had already configured a machine has nothing to redo just because several
        // of them can now be listed.
        ObserverEndpoint previous = ObserverEndpoint.Remote(
            new Uri("https://other:5058/"), "token", "from the old client.json", Fingerprint);

        MachineListResult result = MachineDirectory.Resolve(
            null, new ClientConfigurationResult(previous, null), FakeSecretStore.Empty());

        Assert.Equal(2, result.Machines.Count);
        Assert.Equal(previous, result.Machines[1]);
    }

    [Fact]
    public void TheFingerprintMismatchExplanationNamesExpectedAndReceived()
    {
        // A message that says no more than "does not match" leaves the user without the new
        // value, that is, without any way to tell a reinstallation from an attack and without
        // the value to paste in to put things right.
        CertificatePinning pinning = new(Fingerprint);

        string explanation = pinning.DescribeMismatch("laptop");

        Assert.Contains("Expected:", explanation, StringComparison.Ordinal);
        Assert.Contains("Received:", explanation, StringComparison.Ordinal);
        Assert.Contains("reinstalled", explanation, StringComparison.Ordinal);

        // No certificate has arrived yet: saying so is better than leaving the line empty.
        Assert.Contains("none", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExplanationSaysTheTokenNeverLeftThisMachine()
    {
        // It is the first question anyone who sees that message asks, and the answer is a good
        // one: the connection is refused during the handshake, before anything at all is sent.
        CertificatePinning pinning = new(Fingerprint);

        Assert.Contains("never left this machine", pinning.DescribeMismatch("laptop"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheOldClientJsonCannotReopenTheCleartextRoute()
    {
        // The back door easiest to leave open: the list refuses http://, but the single-machine
        // fallback used to get in without passing any check at all. The result would have been
        // the token sent in cleartext once a second, which is exactly what closing that door
        // was meant to prevent.
        ObserverEndpoint cleartext = ObserverEndpoint.Remote(
            new Uri("http://old-machine:5057/"), "token", "from the old client.json", Fingerprint);

        MachineListResult result = MachineDirectory.Resolve(
            null, new ClientConfigurationResult(cleartext, null), FakeSecretStore.Empty());

        Assert.Single(result.Machines);
        Assert.Contains("https", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void TheOldClientJsonWithoutAFingerprintStaysOut()
    {
        // Same hole, other half: encrypted, but towards nobody in particular.
        ObserverEndpoint withoutFingerprint = ObserverEndpoint.Remote(
            new Uri("https://old-machine:5058/"), "token", "from the old client.json");

        MachineListResult result = MachineDirectory.Resolve(
            null, new ClientConfigurationResult(withoutFingerprint, null), FakeSecretStore.Empty());

        Assert.Single(result.Machines);
        Assert.Contains("fingerprint", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithoutTheListDoesNotDropTheOldConfiguration()
    {
        // Valid JSON but with no "machines": this is not an empty file, which would be fine, it
        // is a file somebody thought they had written. Clearing everything in silence would take
        // the previous configuration away too, and whoever is watching would see a machine
        // vanish for no reason.
        ObserverEndpoint previous = ObserverEndpoint.Remote(
            new Uri("https://other:5058/"), "token", "from the old client.json", Fingerprint);

        MachineListResult result = MachineDirectory.Resolve(
            """{ "other": 1 }""", new ClientConfigurationResult(previous, null), FakeSecretStore.Empty());

        Assert.Equal(2, result.Machines.Count);
        Assert.Contains("machines", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> secrets = new(StringComparer.Ordinal);
        private readonly string? failure;

        private FakeSecretStore(string? failure) => this.failure = failure;

        public string Description => "the pretend store";

        public static FakeSecretStore Empty() => new(failure: null);

        public static FakeSecretStore With(string name, string secret)
        {
            FakeSecretStore store = new(failure: null);
            store.secrets[name] = secret;

            return store;
        }

        public static FakeSecretStore ThatFails(string reason) => new(reason);

        public bool TryRead(string name, out string secret)
        {
            if (failure is not null)
            {
                throw new SecretStoreException(failure);
            }

            if (secrets.TryGetValue(name, out string? found))
            {
                secret = found;

                return true;
            }

            secret = string.Empty;

            return false;
        }

        public void Write(string name, string secret) => secrets[name] = secret;

        public bool Delete(string name) => secrets.Remove(name);
    }
}
