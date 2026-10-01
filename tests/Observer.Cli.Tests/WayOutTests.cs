using Observer.Service.Credentials;

namespace Observer.Cli.Tests;

/// <summary>What the operator is told to do, for each thing root can refuse to read.</summary>
/// <remarks>
/// One sentence for every refusal would be wrong for most of them. A link and a FIFO are things
/// somebody put in the folder, and the way out is to look and then remove them. A file this
/// machine could not check is nothing of the sort, and telling its owner to delete the store
/// because of it would be advice that costs them their token for a fault that is not theirs.
/// </remarks>
public sealed class WayOutTests
{
    private const string Store = "/etc/observer/credentials.json";

    private static StoreNotSafeToReadException Refused(StoreRefusalKind kind) =>
        new(Store, "is something", kind);

    private static string WayOut(StoreRefusalKind kind) =>
        string.Join('\n', Diagnosis.DescribeWayOut(Refused(kind)));

    [Fact]
    public void ALinkIsReadAtItsTargetAndRemovedOnlyIfItIsNotYours()
    {
        // Where it points is in the sentence before this one ("sudo ls -l" shows the target).
        string text = WayOut(StoreRefusalKind.SymbolicLink);

        Assert.Contains("sudo cat", text, StringComparison.Ordinal);
        Assert.Contains("sudo systemctl stop observer", text, StringComparison.Ordinal);
        Assert.Contains("NEW token", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondNameIsSoughtAndItsOwnerIsTheOneToRemoveIt()
    {
        string text = WayOut(StoreRefusalKind.SecondName);

        Assert.Contains("sudo find / -xdev -samefile " + Store, text, StringComparison.Ordinal);
        Assert.Contains("copy", text, StringComparison.Ordinal);
        Assert.Contains("sudo systemctl stop observer", text, StringComparison.Ordinal);
        Assert.Contains("NEW token", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StoreRefusalKind.NotAPlainFile)]
    [InlineData(StoreRefusalKind.TooLarge)]
    [InlineData(StoreRefusalKind.Unstable)]
    public void AnythingElseSomebodyPutThereIsRemovedAfterTheServiceIsStopped(StoreRefusalKind kind)
    {
        string text = WayOut(kind);

        Assert.Contains("sudo systemctl stop observer", text, StringComparison.Ordinal);
        Assert.Contains("NEW token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-samefile", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileTheMachineCouldNotCheckIsNeverToBeDeleted()
    {
        string text = WayOut(StoreRefusalKind.Machine);

        // Bounded, because "could not check" also means it is not known that this is not a FIFO
        // or a file of any size, and a plain cat would wait on one and print the other.
        Assert.Contains("sudo ls -li " + Store, text, StringComparison.Ordinal);
        Assert.Contains("sudo timeout 5 head -c 4096 " + Store, text, StringComparison.Ordinal);
        Assert.DoesNotContain("sudo cat", text, StringComparison.Ordinal);
        Assert.Contains("uname", text, StringComparison.Ordinal);
        Assert.DoesNotContain("stop observer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NEW token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("remove", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFileAnotherProcessHoldsIsTriedAgain()
    {
        string text = WayOut(StoreRefusalKind.Busy);

        Assert.Contains("try again", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stop observer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NEW token", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StoreRefusalKind.Machine)]
    [InlineData(StoreRefusalKind.Busy)]
    public void WhenNothingIsKnownToBeWrongTheSentenceDoesNotAccuseTheServiceAccount(StoreRefusalKind kind)
    {
        string text = Diagnosis.DescribeRefusal(Refused(kind));

        Assert.Contains("sudo ls -l " + Store, text, StringComparison.Ordinal);
        Assert.DoesNotContain("can put anything in it", text, StringComparison.Ordinal);
        Assert.DoesNotContain("elevated", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(StoreRefusalKind.SymbolicLink)]
    [InlineData(StoreRefusalKind.SecondName)]
    [InlineData(StoreRefusalKind.NotAPlainFile)]
    [InlineData(StoreRefusalKind.TooLarge)]
    [InlineData(StoreRefusalKind.Unstable)]
    public void WhatSomebodyPutThereIsSaidToBeTheirs(StoreRefusalKind kind)
    {
        string text = Diagnosis.DescribeRefusal(Refused(kind));

        Assert.Contains("can put anything in it", text, StringComparison.Ordinal);
        Assert.Contains("sudo ls -l " + Store, text, StringComparison.Ordinal);
        Assert.DoesNotContain("elevated", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACertificateRefusedForSomethingPutThereSaysWhatRemovingItCosts()
    {
        string text = Diagnosis.CostOfRemovingCertificate(Refused(StoreRefusalKind.SymbolicLink));

        Assert.Contains("fingerprint", text, StringComparison.Ordinal);
        Assert.Contains("observer share", text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, Diagnosis.CostOfRemovingCertificate(Refused(StoreRefusalKind.Machine)));
        Assert.Equal(string.Empty, Diagnosis.CostOfRemovingCertificate(Refused(StoreRefusalKind.Busy)));
    }

    [Fact]
    public void OnLinuxAnUnreadableStoreIsNotBlamedOnAnAdministratorTerminal()
    {
        string text = string.Join(
            '\n',
            Diagnosis.DescribeUnreadable(new UnauthorizedAccessException("denied"), Store, windows: false));

        Assert.Contains("sudo", text, StringComparison.Ordinal);
        Assert.Contains("denied", text, StringComparison.Ordinal);
        Assert.DoesNotContain("elevated terminal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Run as administrator", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SYSTEM", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAccessDeniedThatTheStoreReaderWrappedIsStillAnAccessDenied()
    {
        // CredentialStore.Read does not let the bare exception out: it throws an
        // InvalidOperationException that carries it. An ordinary account that has no right to the
        // folder reaches the text in exactly this shape, and must be told to use sudo - not to
        // delete a store that is perfectly fine.
        string text = string.Join(
            '\n',
            Diagnosis.DescribeUnreadable(
                new InvalidOperationException("exists but can't be read", new UnauthorizedAccessException("denied")),
                Store,
                windows: false));

        Assert.Contains("run the same command with sudo", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NEW token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("remove the file", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WhatTheErrorSaysIsNeverCarriedWithTheControlCharactersItHad(bool windows, bool accessDenied)
    {
        // One per branch of the text: each of them prints the message as "Detail".
        string hostile = "Path: $['\u001b[2J']";
        Exception error = accessDenied ? new UnauthorizedAccessException(hostile) : new InvalidOperationException(hostile);

        string text = string.Join('\n', Diagnosis.DescribeUnreadable(error, Store, windows));

        Assert.False(text.Any(letter => char.IsControl(letter) && letter != '\n'), "a control character reached the text");
        Assert.Contains("Path: $['?[2J']", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnLinuxADamagedStoreIsSaidToBeDamagedAndTheCostOfStartingOverIsSaid()
    {
        string text = string.Join(
            '\n',
            Diagnosis.DescribeUnreadable(new InvalidOperationException("not a usable store"), Store, windows: false));

        Assert.Contains("not a usable store", text, StringComparison.Ordinal);
        Assert.Contains("NEW token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("elevated terminal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Run as administrator", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnWindowsTheAdministratorAdviceStaysAsItWas()
    {
        string text = string.Join(
            '\n',
            Diagnosis.DescribeUnreadable(new UnauthorizedAccessException("denied"), Store, windows: true));

        Assert.Contains("an elevated terminal is required", text, StringComparison.Ordinal);
        Assert.Contains("Run as administrator", text, StringComparison.Ordinal);
    }
}