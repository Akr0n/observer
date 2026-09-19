using Observer.Cli;
using Observer.Service.Credentials;

namespace Observer.Cli.Tests;

/// <summary>
/// The sentences <c>doctor</c> shows to whoever is reading the screen.
/// </summary>
/// <remarks>
/// They are code in every respect: they tell the user whether their machine's token is safe
/// and what to do if it is not. A missing or ambiguous sentence counts as a defect.
/// </remarks>
public class DiagnosisTests
{
    [Fact]
    public void EveryVerdictHasItsOwnSentence_AndNoTwoAreEqual()
    {
        List<string> sentences = [];

        foreach (DirectoryVerdict verdict in Enum.GetValues<DirectoryVerdict>())
        {
            string sentence = Diagnosis.DescribeVerdict(verdict);

            Assert.False(string.IsNullOrWhiteSpace(sentence), verdict.ToString());
            sentences.Add(sentence);
        }

        Assert.Equal(sentences.Count, sentences.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void OnlyTheSafeVerdictSaysProtected()
    {
        // "PROTECTED" is the word an administrator stops reading at: it must not appear in
        // any of the other cases, and in particular not in the FALSELY PROTECTED one.
        foreach (DirectoryVerdict verdict in Enum.GetValues<DirectoryVerdict>())
        {
            bool saysProtected = Diagnosis.DescribeVerdict(verdict).StartsWith("PROTECTED", StringComparison.Ordinal);

            Assert.Equal(verdict == DirectoryVerdict.Safe, saysProtected);
        }
    }

    [Fact]
    public void FalselyProtectedExplainsWhyItIsNotProtected()
    {
        // It is the verdict nobody would write without having measured it: the DACL looks right,
        // but the owner rewrites it whenever they like. If the sentence does not explain that,
        // the reader concludes it is a false alarm.
        string sentence = Diagnosis.DescribeVerdict(DirectoryVerdict.UntrustedOwner);

        Assert.Contains("OWNER", sentence, StringComparison.Ordinal);
        Assert.Contains("looks safe", sentence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DangerousVerdictsSayTheTokenWorksFromTheNETWORK()
    {
        // Without this line, "other users can read it" sounds like a local confidentiality
        // problem, and not like permanent access from another computer.
        Assert.Contains("NETWORK", Diagnosis.DescribeVerdict(DirectoryVerdict.OpenDacl), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("help", 0)]
    [InlineData("--help", 0)]
    [InlineData("not-a-real-verb", 2)]
    public void UnknownVerbsExitWithANonZeroCode(string verb, int expectedExitCode)
    {
        Assert.Equal(expectedExitCode, Commands.Run([verb]));
    }

    [Fact]
    public void WithNoArgumentsTheHelpIsShown()
    {
        Assert.Equal(0, Commands.Run([]));
    }

    [Fact]
    public void AnABSENTStoreIsReportedAsAbsent()
    {
        string storePath = Path.Combine(
            Path.GetTempPath(), "obs-" + Guid.NewGuid().ToString("N")[..10], "credentials.json");

        Assert.StartsWith("ABSENT", Diagnosis.DescribeProtection(storePath), StringComparison.Ordinal);
    }

    [Fact]
    public void APipeThatDoesNotEXISTIsReportedAsSILENT()
    {
        // Not "error": the normal case in which doctor is run is that the service is stopped,
        // and the sentence must say what to do instead of showing an exception.
        // Invented name and path, so the outcome does not depend on what is running on the
        // machine executing the tests: with the default path, on a Linux where Observer really
        // is installed the socket exists and answers.
        string suffix = Guid.NewGuid().ToString("N")[..10];

        string result = LocalChannelProbe.Probe(
            "observer-does-not-exist-" + suffix,
            "/tmp/observer-does-not-exist-" + suffix + ".sock",
            TimeSpan.FromMilliseconds(700));

        Assert.StartsWith("SILENT", result, StringComparison.Ordinal);
    }

    [Fact]
    public void NoDoctorSentenceIsEmpty()
    {
        // They are what the user reads to tell whether their machine's token is safe:
        // an empty line or a raw enum name would count as a defect.
        Assert.False(string.IsNullOrWhiteSpace(Diagnosis.CurrentAccountName()));
        Assert.False(string.IsNullOrWhiteSpace(Diagnosis.ElevatedAsText()));
        Assert.False(string.IsNullOrWhiteSpace(LocalChannelProbe.Probe(
            "no-such-channel-" + Guid.NewGuid().ToString("N")[..8],
            "/tmp/no-such-channel-" + Guid.NewGuid().ToString("N")[..8] + ".sock",
            TimeSpan.FromMilliseconds(500))));
    }
}