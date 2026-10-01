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

    // ---- Who owns the store on Linux. The decision and the words are pure, so none of the
    // tests below touches a file system except the one that says so.

    private const string Store = "/etc/observer/credentials.json";

    private static readonly StoreOwner Root = new(0, 0, "root", "root");

    private static readonly StoreOwner ServiceAccount = new(998, 998, "observer", "observer");

    [Theory]
    [InlineData(998u, 998u, 998u, 998u, OwnershipVerdict.Matches)] // a healthy .deb
    [InlineData(0u, 0u, 998u, 998u, OwnershipVerdict.Mismatch)] // what rotate-key as root left
    [InlineData(0u, 998u, 998u, 998u, OwnershipVerdict.Mismatch)] // the account alone is enough
    [InlineData(998u, 998u, 0u, 0u, OwnershipVerdict.Mismatch)] // any other account, either way round
    [InlineData(998u, 0u, 998u, 998u, OwnershipVerdict.Matches)] // the group alone is not: 0600 closes it
    [InlineData(0u, 0u, 0u, 0u, OwnershipVerdict.Matches)] // a service started by hand as root
    public void OnlyAFileOfAnotherAccountThanItsDirectoryIsAMismatch(
        uint fileUid, uint fileGid, uint directoryUid, uint directoryGid, OwnershipVerdict expected)
    {
        Assert.Equal(
            expected,
            Diagnosis.JudgeOwnership(new StoreOwner(fileUid, fileGid), new StoreOwner(directoryUid, directoryGid)));
    }

    [Fact]
    public void TheNumberDecidesAndTheNameNever()
    {
        // The names come from /etc/passwd and may be missing for one side only (an account
        // deleted after it created the file): that must not make two equal uids differ, nor
        // two equal names with different uids agree.
        Assert.Equal(
            OwnershipVerdict.Matches,
            Diagnosis.JudgeOwnership(new StoreOwner(998, 998, User: null), new StoreOwner(998, 998, "observer")));
        Assert.Equal(
            OwnershipVerdict.Mismatch,
            Diagnosis.JudgeOwnership(new StoreOwner(0, 0, "observer"), new StoreOwner(998, 998, "observer")));
    }

    [Fact]
    public void WhatCannotBeReadIsUnknownAndNeverAMismatch()
    {
        // A zero uid is root, not "nothing": an absent answer must not be told apart from it.
        Assert.Equal(OwnershipVerdict.Unknown, Diagnosis.JudgeOwnership(null, ServiceAccount));
        Assert.Equal(OwnershipVerdict.Unknown, Diagnosis.JudgeOwnership(Root, null));
        Assert.Equal(OwnershipVerdict.Unknown, Diagnosis.JudgeOwnership(null, null));
    }

    [Fact]
    public void AMismatchNamesBothOwnersAndGivesTheTwoCommandsThatRepairIt()
    {
        IReadOnlyList<string> lines = Diagnosis.DescribeOwnership(Store, Root, ServiceAccount);

        Assert.StartsWith("WRONG OWNER", lines[0], StringComparison.Ordinal);
        Assert.Contains("file      : uid 0 (root), gid 0 (root)", lines);
        Assert.Contains("directory : uid 998 (observer), gid 998 (observer)", lines);

        // The exact text, in this order: these are typed by somebody whose service is down, and
        // they were run by hand on an installed .deb before being written here.
        Assert.Equal(
            [
                "sudo chown -h observer:observer /etc/observer/credentials.json",
                "sudo systemctl restart observer",
            ],
            lines.Skip(lines.Count - 2));
    }

    [Fact]
    public void TheAccountToHandTheFileToIsTheOwnerOfTheDirectoryNotAShippedName()
    {
        IReadOnlyList<string> lines = Diagnosis.DescribeOwnership(
            "/srv/obs/credentials.json", Root, new StoreOwner(4242, 4343, "svc", "svcgroup"));

        Assert.Equal("sudo chown -h svc:svcgroup /srv/obs/credentials.json", lines[^2]);
    }

    [Fact]
    public void AnOwnerWithNoNameInTheSystemDatabasesIsHandedOverByNumber()
    {
        IReadOnlyList<string> lines = Diagnosis.DescribeOwnership(
            Store, Root, new StoreOwner(4242, 4343));

        Assert.Equal("sudo chown -h 4242:4343 /etc/observer/credentials.json", lines[^2]);
        Assert.Contains("directory : uid 4242, gid 4343", lines);
    }

    [Fact]
    public void AStoreThatIsRightSaysSoAndOffersNothingToRun()
    {
        IReadOnlyList<string> lines = Diagnosis.DescribeOwnership(Store, ServiceAccount, ServiceAccount);

        Assert.StartsWith("OK", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, line => line.Contains("sudo chown", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("systemctl", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOrdinaryAccountThatCannotSeeInsideTheDirectoryIsToldWhoCanAndWhatIsKnown()
    {
        // /etc/observer is 0700: from any other account the directory's owner is readable and
        // the file's is not. That is the COMMON way to run doctor, so it gets a sentence of its
        // own, with the half that is known, instead of a silence or a false "absent".
        IReadOnlyList<string> lines = Diagnosis.DescribeOwnership(Store, null, ServiceAccount);

        Assert.StartsWith("UNKNOWN", lines[0], StringComparison.Ordinal);
        Assert.Contains("file      : not readable from here", lines);
        Assert.Contains("directory : uid 998 (observer), gid 998 (observer)", lines);
        Assert.Contains("\"sudo observer doctor\" can read them.", lines);
        Assert.DoesNotContain(lines, line => line.Contains("sudo chown", StringComparison.Ordinal));
    }

    [Fact]
    public void NoOwnershipLineIsWiderThanWhatFitsBesideTheLabel()
    {
        // doctor prints the lines after the first under an 18-column label, and a terminal is
        // often 80 wide: a longer line wraps back to column 0 in the middle of a sentence.
        foreach (IReadOnlyList<string> lines in new[]
        {
            Diagnosis.DescribeOwnership(Store, Root, ServiceAccount),
            Diagnosis.DescribeOwnership(Store, ServiceAccount, ServiceAccount),
            Diagnosis.DescribeOwnership(Store, null, ServiceAccount),
            Diagnosis.DescribeOwnership(Store, null, null),
        })
        {
            Assert.All(lines, line => Assert.True(line.Length <= 62, line.Length + " columns: " + line));
        }
    }

    [Fact]
    public void EveryOwnershipSentenceIsNonEmpty_AndNoneQuotesAVerbTheCliDoesNotHave()
    {
        // "observer diagnose" was printed to users once, and is not a verb. Every word that
        // follows "observer " in what doctor says must be one the dispatcher knows. The list is
        // written out here because running the verbs to find out would run them (rotate-key).
        int quotedVerbs = 0;

        foreach (IReadOnlyList<string> lines in new[]
        {
            Diagnosis.DescribeOwnership(Store, Root, ServiceAccount),
            Diagnosis.DescribeOwnership(Store, ServiceAccount, ServiceAccount),
            Diagnosis.DescribeOwnership(Store, null, ServiceAccount),
            Diagnosis.DescribeOwnership(Store, null, null),
        })
        {
            Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));

            foreach (string line in lines)
            {
                foreach (System.Text.RegularExpressions.Match quoted in
                    System.Text.RegularExpressions.Regex.Matches(line, "observer ([a-z-]+)"))
                {
                    string verb = quoted.Groups[1].Value;
                    quotedVerbs++;

                    Assert.True(
                        verb is "doctor" or "rotate-key" or "share" or "token",
                        "Not a verb of the CLI: " + verb);
                }
            }
        }

        // A pattern that stops matching must not turn this into a loop over nothing.
        Assert.True(quotedVerbs >= 2, "no verb was found in the sentences");
    }

    [Fact]
    public void ANameIsFoundByItsNumberAndTheFirstLineThatHasItWins()
    {
        string database = Path.Combine(Path.GetTempPath(), "obs-db-" + Guid.NewGuid().ToString("N")[..10]);

        try
        {
            File.WriteAllLines(
                database,
                [
                    "# a comment line, with 5 in it:x:5:5",
                    "root:x:0:0:root:/root:/bin/bash",
                    "no colons at all",
                    "short:x",
                    "observer:x:998:998::/var/lib/observer:/usr/sbin/nologin",
                    "alias:x:998:998::/:",
                    "group:x:1234:",
                ]);

            Assert.Equal("root", Diagnosis.NameIn(database, 0));
            Assert.Equal("observer", Diagnosis.NameIn(database, 998));
            Assert.Equal("group", Diagnosis.NameIn(database, 1234));
            Assert.Null(Diagnosis.NameIn(database, 5));
            Assert.Null(Diagnosis.NameIn(database, 77));
        }
        finally
        {
            File.Delete(database);
        }
    }

    [Fact]
    public void ANameInADatabaseThatIsNotThereIsSimplyUnknown()
    {
        Assert.Null(Diagnosis.NameIn(Path.Combine(Path.GetTempPath(), "obs-no-such-" + Guid.NewGuid().ToString("N")), 0));
    }

    [OnLinuxFact]
    public void AStoreThatIsASymbolicLinkIsNeverCalledAFileAndNeverGetsAChown()
    {
        // The directory belongs to the service account, which faces the network, and whoever
        // follows the advice printed here is root. A link put where the store is, pointing at a
        // file of root's, reads as "owned by another account", and a chown BY PATH would hand
        // its target to the service account. doctor must say what it is looking at instead.
        string directory = Path.Combine(Path.GetTempPath(), "obs-lnk-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);

        try
        {
            string target = Path.Combine(directory, "elsewhere");
            File.WriteAllText(target, "not the store");
            string store = Path.Combine(directory, "credentials.json");
            File.CreateSymbolicLink(store, target);

            IReadOnlyList<string> lines = Diagnosis.DescribeOwnership(store);

            Assert.StartsWith("NOT A PLAIN FILE", lines[0], StringComparison.Ordinal);
            Assert.DoesNotContain(lines, line => line.Contains("chown", StringComparison.Ordinal));
            // The last line is the command to look at it, which carries the path.
            Assert.All(lines.SkipLast(1), line => Assert.True(line.Length <= 62, line));
            Assert.Equal("sudo ls -l " + store, lines[^1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [OnLinuxFact]
    public void AFifoWhereTheStoreBelongsIsNamedAndNeverGetsAChown()
    {
        // What the read refuses, doctor says in the same terms: it is the command an operator
        // runs to find out why share did not work, and "OK" or a chown would both be wrong.
        string directory = Path.Combine(Path.GetTempPath(), "obs-fifo-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);

        try
        {
            string store = Path.Combine(directory, "credentials.json");
            Tool.Run("mkfifo", store);

            IReadOnlyList<string> lines = Diagnosis.DescribeOwnership(store);

            Assert.StartsWith("NOT A PLAIN FILE", lines[0], StringComparison.Ordinal);
            Assert.DoesNotContain(lines, line => line.Contains("chown", StringComparison.Ordinal));
            Assert.All(lines.SkipLast(1), line => Assert.True(line.Length <= 62, line));
            Assert.Equal("sudo ls -li " + store, lines[^1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [OnLinuxFact]
    public void AStoreTooLargeToBeOneIsNamedAndNeverGetsAChown()
    {
        string directory = Path.Combine(Path.GetTempPath(), "obs-big-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);

        try
        {
            string store = Path.Combine(directory, "credentials.json");
            // One byte more than the 64 KiB root will read: the number is pinned here on purpose.
            File.WriteAllBytes(store, new byte[(64 * 1024) + 1]);

            IReadOnlyList<string> lines = Diagnosis.DescribeOwnership(store);

            Assert.StartsWith("TOO LARGE", lines[0], StringComparison.Ordinal);
            Assert.DoesNotContain(lines, line => line.Contains("chown", StringComparison.Ordinal));
            Assert.Equal("sudo ls -li " + store, lines[^1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [OnLinuxFact]
    public void ASecondNameOfTheFolderOwnersOwnFileIsStillOk()
    {
        // The service's own file with a backup name next to it (cp -al) is not a trap: only a
        // second name of a file that belongs to ANOTHER account is, and this is not one.
        string directory = Path.Combine(Path.GetTempPath(), "obs-own-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);

        try
        {
            string store = Path.Combine(directory, "credentials.json");
            File.WriteAllText(store, "{}");
            Tool.Run("ln", store, Path.Combine(directory, "credentials.json.bak"));

            Assert.StartsWith("OK", Diagnosis.DescribeOwnership(store)[0], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [RootOnlyFact]
    public void ASecondNameOfAFileOfAnotherAccountIsNamedAndNeverGetsAChown()
    {
        // Root's own file with a second name in a folder the service account owns. Without this
        // verdict doctor reads "belongs to another account", prints a chown of the store's name,
        // and a chown changes the INODE: it would hand root's file to the service account.
        string directory = Path.Combine(Path.GetTempPath(), "obs-two-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);
        string rootsOwn = Path.Combine(Path.GetTempPath(), "obs-roots-" + Guid.NewGuid().ToString("N")[..10]);

        try
        {
            File.WriteAllText(rootsOwn, "{}");

            string store = Path.Combine(directory, "credentials.json");
            Tool.Run("ln", rootsOwn, store);
            Tool.Run("chown", "1655:1655", directory);

            IReadOnlyList<string> lines = Diagnosis.DescribeOwnership(store);

            Assert.StartsWith("NOT A PLAIN FILE", lines[0], StringComparison.Ordinal);
            Assert.DoesNotContain(lines, line => line.Contains("chown", StringComparison.Ordinal));
            Assert.Contains("sudo find / -xdev -samefile " + store, lines);
            Assert.All(
                lines.Where(line => !line.StartsWith("sudo ", StringComparison.Ordinal)),
                line => Assert.True(line.Length <= 62, line));
            Assert.Equal("sudo ls -li " + store, lines[^1]);
        }
        finally
        {
            File.Delete(rootsOwn);
            Directory.Delete(directory, recursive: true);
        }
    }

    [OnLinuxFact]
    public void ARealStoreWrittenByThisAccountInItsOwnDirectoryIsOk()
    {
        // "uid 10" must not be taken for "uid 100": the number ends at a space or a comma.
        static bool Says(string label, string uid, string line) =>
            line.StartsWith(label + ": uid " + uid + " ", StringComparison.Ordinal)
            || line.StartsWith(label + ": uid " + uid + ",", StringComparison.Ordinal);

        string directory = Path.Combine(Path.GetTempPath(), "obs-doc-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);

        try
        {
            string store = Path.Combine(directory, "credentials.json");
            File.WriteAllText(store, "{}");

            IReadOnlyList<string> lines = Diagnosis.DescribeOwnership(store);

            Assert.StartsWith("OK", lines[0], StringComparison.Ordinal);

            // Both paths answering the same wrong number would still say OK: what pins the number
            // is the kernel's own account of who this process is (the effective uid, field 2).
            string uid = File.ReadLines("/proc/self/status")
                .First(line => line.StartsWith("Uid:", StringComparison.Ordinal))
                .Split('\t')[2];

            Assert.Contains(lines, line => Says("file      ", uid, line));
            Assert.Contains(lines, line => Says("directory ", uid, line));

            // And a file that is not there is not a mismatch, whatever the reason it is missing.
            // The half that is known must be the DIRECTORY's and the half that is not the FILE's:
            // with the two lookups swapped, everything above still holds.
            File.Delete(store);
            lines = Diagnosis.DescribeOwnership(store);

            Assert.StartsWith("UNKNOWN", lines[0], StringComparison.Ordinal);
            Assert.Contains("file      : not readable from here", lines);
            Assert.Contains(lines, line => Says("directory ", uid, line));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

/// <summary>A fact that runs only on Linux, where statx and /etc/passwd are.</summary>
public sealed class OnLinuxFactAttribute : FactAttribute
{
    /// <summary>Skips everywhere else.</summary>
    public OnLinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Reads the owner of real files through statx: run only on Linux.";
        }
    }
}