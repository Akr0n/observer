using System.Globalization;
using Observer.Core.Platform;
using Observer.Core.Security;

namespace Observer.Core.Tests;

/// <summary>
/// The store for the remote machines' tokens.
/// </summary>
/// <remarks>
/// It exists because those tokens used to sit in cleartext inside <c>machines.json</c>, a file
/// written by hand and meant to be read. Since the same token also authorizes killing
/// processes on another machine, that file is worth far more than it used to be.
/// </remarks>
public class SecretStoreTests
{
    [Theory]
    [InlineData("workstation")]
    [InlineData("Spare Laptop")]
    [InlineData("nas-01.local")]
    public void AnOrdinaryNameIsAcceptedUnchanged(string name) => Assert.Equal(name, SecretName.Validate(name));

    [Theory]
    [InlineData("../../id_rsa")]
    [InlineData("..\\elsewhere")]
    [InlineData("/etc/shadow")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("with*asterisk")]
    public void ANameThatTriesToLeaveTheFolderIsRejected(string name)
    {
        // The name comes from machines.json, which a person writes, and it is used to build a
        // file path: without this check an entry called "../../id_rsa" would cause a read - and a
        // rewrite - of a file outside the secrets folder.
        Assert.Throws<SecretStoreException>(() => SecretName.Validate(name));
    }

    [Fact]
    public void SurroundingSpacesDoNotMakeTwoDifferentSecrets() =>
        Assert.Equal("workstation", SecretName.Validate("  workstation  "));

    [Fact]
    public void OnAnUnknownPlatformTheStoreSaysSo()
    {
        // Not an empty store: an empty store would make you conclude you had forgotten to store
        // the token, and would send you looking for the problem in the wrong place.
        ISecretStore store = SecretStores.For(HostPlatform.Unknown);

        Assert.Throws<SecretStoreException>(() => store.TryRead("workstation", out _));
    }

    [WindowsOnly]
    public void OnWindowsTheSecretRoundTripsThroughCredentialManager()
    {
        // The one thing that cannot be known by reasoning alone: that advapi32 accepts the struct
        // as we declared it and gives back the same bytes. The name carries a GUID, so this test
        // cannot touch a real credential.
        string name = "observer-test-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        const string Secret = "a-token-that-is-good-for-nothing";

        ISecretStore store = SecretStores.For(HostPlatform.Windows);

        Assert.False(store.TryRead(name, out _), "the store already held a name with that GUID");

        try
        {
            store.Write(name, Secret);

            Assert.True(store.TryRead(name, out string readBack));
            Assert.Equal(Secret, readBack);
        }
        finally
        {
            store.Delete(name);
        }

        Assert.False(store.TryRead(name, out _), "the secret survived the delete");
    }

    [WindowsOnly]
    public void OnWindowsDeletingAnAbsentSecretReturnsFalseInsteadOfThrowing()
    {
        string name = "observer-never-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        Assert.False(SecretStores.For(HostPlatform.Windows).Delete(name));
    }
}
