using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// Se ci si puo' fidare della cartella che ospitera' il token di macchina.
/// </summary>
/// <remarks>
/// Funzione PURA sui fatti osservati, per due motivi. Il primo e' che i casi che contano non si
/// possono costruire tutti su una macchina qualsiasi: una cartella posseduta da SYSTEM richiede
/// una sessione amministrativa. Il secondo e' che questa e' la decisione di sicurezza portante
/// del deposito, e va verificata a tabella e non per campione.
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
        // Una giunzione la crea un utente standard SENZA privilegi. Se il controllo non venisse
        // per primo, si correggerebbero proprietario e ACL della cartella dell'ATTACCANTE, e ci
        // si depositerebbe dentro il token. Qui i fatti sono per il resto perfetti, apposta.
        DirectoryFacts perfectButAJunction = Facts(
            isReparsePoint: true,
            owner: SystemSid,
            daclProtected: true,
            daclSids: [SystemSid, AdministratorsSid]);

        Assert.Equal(DirectoryVerdict.ReparsePoint, DirectoryTrust.Evaluate(perfectButAJunction));
    }

    [Fact]
    public void AnUnreadableDescriptorMeansUnsafeNotJustUnknown()
    {
        Assert.Equal(
            DirectoryVerdict.Unknown,
            DirectoryTrust.Evaluate(Facts(securityDescriptorReadable: false)));
    }

    [Fact]
    public void APerfectDaclOwnedByAUSERIsNOTSafe()
    {
        // E' il "finto protetto": la DACL non nomina l'utente in alcun modo, ma il proprietario
        // ha WRITE_DAC implicito e se la riscrive quando vuole. Misurato: una sola chiamata e
        // l'accesso torna completo. Chi guarda solo le ACE dice "sicura" e sbaglia.
        DirectoryFacts fakeProtected = Facts(
            owner: UserSid,
            daclProtected: true,
            daclSids: [SystemSid, AdministratorsSid]);

        Assert.Equal(DirectoryVerdict.UntrustedOwner, DirectoryTrust.Evaluate(fakeProtected));
    }

    [Theory]
    [InlineData(SystemSid)]
    [InlineData(AdministratorsSid)]
    public void TheOnlyTwoOwnersAllowed(string owner)
    {
        Assert.Equal(
            DirectoryVerdict.Safe,
            DirectoryTrust.Evaluate(Facts(owner: owner, daclProtected: true, daclSids: [SystemSid, AdministratorsSid])));
    }

    [Fact]
    public void AnUnprotectedDaclIsNOTSafeEvenIfTheAcesAreRight()
    {
        // Non protetta significa che eredita: e la cartella di sistema che ospita il deposito
        // concede a BUILTIN\Users la lettura ereditabile. Ereditare basta a perdere il segreto,
        // senza bisogno di alcun attaccante.
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
        // Un campo dimenticato o una struct non inizializzata non devono produrre "Sicura".
        Assert.Equal(DirectoryVerdict.Unknown, default(DirectoryVerdict));
        Assert.NotEqual(DirectoryVerdict.Safe, default(DirectoryVerdict));
    }

    [Fact]
    public void OnlySafeCanHoldTheSecret()
    {
        // Chiunque usi il verdetto deve poter distinguere "vai avanti" da "fermati", senza
        // dover elencare a mano i casi negativi e senza dimenticarne uno.
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