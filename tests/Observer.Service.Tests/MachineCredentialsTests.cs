using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// The machine token, its rotation, and the window in which the previous key is still accepted.
/// </summary>
public class MachineCredentialsTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AGeneratedTokenIsNewEveryTime()
    {
        // Two calls must never match: a generator that repeats is only noticed the day two
        // machines end up with the same key.
        HashSet<string> seen = [];

        for (int i = 0; i < 200; i++)
        {
            Assert.True(seen.Add(TokenGenerator.Generate()), "token ripetuto");
        }
    }

    [Fact]
    public void ATokenContainsNoCharactersThatNeedEncodingInAHeader()
    {
        // It ends up inside "Authorization: Bearer ...". Plain Base64 would use + / =, which
        // have to be encoded in a header and which anyone copying and pasting would get wrong.
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
        // Without this window, rotating would cut off every remote client INSTANTLY, and
        // rotation would become an operation nobody dares to perform.
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

        // The current key does not expire along with it.
        Assert.True(after.Accepts(after.Current, Now.AddHours(25)));
    }

    [Fact]
    public void TwoRotationsInARowForgetTheOldestKey()
    {
        // Exactly ONE previous key is kept. Keeping a chain of them would mean a compromised
        // key stays valid until someone rotates enough times.
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
        // The case where Previous is null must not degenerate into "accept anything": it is
        // the branch a badly written comparison turns into a free pass.
        MachineCredentials credentials = MachineCredentials.Create();

        Assert.Null(credentials.Previous);
        Assert.False(credentials.Accepts(string.Empty, Now));
    }
}