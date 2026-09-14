using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// Il token di macchina, la sua rotazione e la finestra in cui la chiave precedente vale ancora.
/// </summary>
public class MachineCredentialsTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AGeneratedTokenIsNewEveryTime()
    {
        // Due chiamate non devono mai coincidere: un generatore che ripete si nota solo il
        // giorno in cui due macchine hanno la stessa chiave.
        HashSet<string> seen = [];

        for (int i = 0; i < 200; i++)
        {
            Assert.True(seen.Add(TokenGenerator.Generate()), "token ripetuto");
        }
    }

    [Fact]
    public void ATokenContainsNoCharactersThatNeedEncodingInAHeader()
    {
        // Finisce dentro "Authorization: Bearer ...". Base64 normale userebbe + / =, che in un
        // header vanno codificati e che chiunque copi-incolli sbaglierebbe.
        string token = TokenGenerator.Generate();

        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.DoesNotContain(' ', token);
        Assert.True(token.Length >= 40, "token troppo corto: " + token.Length);
    }

    [Fact]
    public void TheCurrentKeyIsAccepted()
    {
        MachineCredentials credentials = MachineCredentials.Create();

        Assert.True(credentials.Accepts(credentials.Current, Now));
    }

    [Fact]
    public void AWrongKeyIsRejected()
    {
        MachineCredentials credentials = MachineCredentials.Create();

        Assert.False(credentials.Accepts("non-e-il-token", Now));
        Assert.False(credentials.Accepts(string.Empty, Now));
    }

    [Fact]
    public void AfterARotationBOTHKeysAreAccepted_UntilTheOldOneExpires()
    {
        // Senza questa finestra, ruotare taglierebbe fuori ogni client remoto all'ISTANTE, e la
        // rotazione diventerebbe un'operazione che nessuno osa fare.
        MachineCredentials before = MachineCredentials.Create();
        string oldKey = before.Current;

        MachineCredentials after = before.Rotate(Now, MachineCredentials.GracePeriod);

        Assert.NotEqual(oldKey, after.Current);
        Assert.True(after.Accepts(after.Current, Now));
        Assert.True(after.Accepts(oldKey, Now));
    }

    [Fact]
    public void ThePreviousKeyStopsBeingAcceptedAtItsExpiry()
    {
        MachineCredentials before = MachineCredentials.Create();
        string oldKey = before.Current;

        MachineCredentials after = before.Rotate(Now, TimeSpan.FromHours(24));

        Assert.True(after.Accepts(oldKey, Now.AddHours(23)));
        Assert.False(after.Accepts(oldKey, Now.AddHours(25)));

        // La corrente non scade con lei.
        Assert.True(after.Accepts(after.Current, Now.AddHours(25)));
    }

    [Fact]
    public void TwoRotationsInARowForgetTheOldestKey()
    {
        // Si conserva UNA sola chiave precedente. Tenerne una catena significherebbe che una
        // chiave compromessa resta valida finche' qualcuno non ruota abbastanza volte.
        MachineCredentials before = MachineCredentials.Create();
        string oldestKey = before.Current;

        MachineCredentials after = before
            .Rotate(Now, TimeSpan.FromHours(24))
            .Rotate(Now, TimeSpan.FromHours(24));

        Assert.False(after.Accepts(oldestKey, Now));
    }

    [Fact]
    public void WithNoPreviousKeyNothingIsAccepted_NotEvenAnEmptyString()
    {
        // Il caso in cui Previous e' null non deve degenerare in "accetta tutto": e' il ramo
        // che un confronto scritto male trasforma in un passaggio libero.
        MachineCredentials credentials = MachineCredentials.Create();

        Assert.Null(credentials.Previous);
        Assert.False(credentials.Accepts(string.Empty, Now));
    }
}