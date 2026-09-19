using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// Whether the directory that will hold the machine token can be trusted.
/// </summary>
/// <remarks>
/// A PURE function over the observed facts, for two reasons. The first is that the cases that
/// matter cannot all be built on just any machine: a directory owned by SYSTEM needs an
/// administrative session. The second is that this is the store's load-bearing security decision,
/// and it has to be verified as a table and not by sampling.
/// </remarks>
public class DirectoryTrustTests
{
    private const string SystemSid = "S-1-5-18";
    private const string AdministratorsSid = "S-1-5-32-544";
    private const string UserSid = "S-1-5-21-1-2-3-1001";
    private const string EveryoneSid = "S-1-1-0";
    private const string BuiltinUsersSid = "S-1-5-32-545";

    [Fact]
    public void AMissingDirectoryCanBeCreated()
    {
        Assert.Equal(DirectoryVerdict.Missing, DirectoryTrust.Evaluate(Facts(exists: false)));
    }

    [Fact]
    public void AReparsePointOutweighsEVERYTHINGElse()
    {
        // A junction is created by a standard user with NO privileges. If this check did not come
        // first, the owner and the ACL of the ATTACKER's directory would be repaired, and the
        // token would be stored inside it. The rest of the facts here are perfect, on purpose.
        DirectoryFacts perfectButAJunction = Facts(
            isReparsePoint: true,
            owner: SystemSid,
            daclProtected: true,
            daclSids: [SystemSid, AdministratorsSid]);

        Assert.Equal(DirectoryVerdict.ReparsePoint, DirectoryTrust.Evaluate(perfectButAJunction));
    }

    [Fact]
    public void AnUnreadableDescriptorIsVerdictUnknown()
    {
        Assert.Equal(
            DirectoryVerdict.Unknown,
            DirectoryTrust.Evaluate(Facts(securityDescriptorReadable: false)));
    }

    [Fact]
    public void APerfectDaclOwnedByAUSERIsNOTSafe()
    {
        // This is the FALSELY PROTECTED case: the DACL does not name the user in any way, but the
        // owner has implicit WRITE_DAC and rewrites it whenever it likes. Measured: a single call
        // and access is complete again. Whoever looks only at the ACEs says "safe" and is wrong.
        DirectoryFacts falselyProtected = Facts(
            owner: UserSid,
            daclProtected: true,
            daclSids: [SystemSid, AdministratorsSid]);

        Assert.Equal(DirectoryVerdict.UntrustedOwner, DirectoryTrust.Evaluate(falselyProtected));
    }

    [Theory]
    [InlineData(SystemSid)]
    [InlineData(AdministratorsSid)]
    public void SystemAndAdministratorsAreSafeOwners(string owner)
    {
        Assert.Equal(
            DirectoryVerdict.Safe,
            DirectoryTrust.Evaluate(Facts(owner: owner, daclProtected: true, daclSids: [SystemSid, AdministratorsSid])));
    }

    [Fact]
    public void AnUnprotectedDaclIsNOTSafeEvenIfTheAcesAreRight()
    {
        // Not protected means it inherits: and the system directory that holds the store grants
        // BUILTIN\Users inheritable read access. Inheriting is enough to lose the secret, with no
        // attacker needed at all.
        Assert.Equal(
            DirectoryVerdict.OpenDacl,
            DirectoryTrust.Evaluate(Facts(owner: SystemSid, daclProtected: false, daclSids: [SystemSid, AdministratorsSid])));
    }

    [Theory]
    [InlineData(EveryoneSid)]
    [InlineData(BuiltinUsersSid)]
    [InlineData(UserSid)]
    public void OneExtraAceIsEnoughToMakeItUnsafe(string intruder)
    {
        Assert.Equal(
            DirectoryVerdict.OpenDacl,
            DirectoryTrust.Evaluate(Facts(owner: SystemSid, daclProtected: true, daclSids: [SystemSid, AdministratorsSid, intruder])));
    }

    [Fact]
    public void TheZeroVerdictIsNotTheOneThatAuthorizes()
    {
        // A forgotten field or an uninitialised struct must not produce "Safe".
        Assert.Equal(DirectoryVerdict.Unknown, default(DirectoryVerdict));
        Assert.NotEqual(DirectoryVerdict.Safe, default(DirectoryVerdict));
    }

    [Fact]
    public void OnlySafeCanHoldTheSecret()
    {
        // Whoever uses the verdict must be able to tell "go ahead" from "stop", without having
        // to list the negative cases by hand and without forgetting one.
        Assert.True(DirectoryVerdict.Safe.CanHoldSecret());
        Assert.False(DirectoryVerdict.Missing.CanHoldSecret());

        foreach (DirectoryVerdict verdict in Enum.GetValues<DirectoryVerdict>())
        {
            if (verdict != DirectoryVerdict.Safe)
            {
                Assert.False(verdict.CanHoldSecret(), verdict.ToString());
            }
        }
    }

    private static DirectoryFacts Facts(
        bool exists = true,
        bool isReparsePoint = false,
        bool securityDescriptorReadable = true,
        string? owner = SystemSid,
        bool daclProtected = true,
        IReadOnlyList<string>? daclSids = null) =>
        new(exists, isReparsePoint, securityDescriptorReadable, owner, daclProtected, daclSids ?? [SystemSid, AdministratorsSid]);
}