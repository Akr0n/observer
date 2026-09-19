using Observer.Cli;

namespace Observer.Cli.Tests;

/// <summary>
/// How the verbs read their arguments, where that can be checked without touching a real store.
/// </summary>
public class CommandsTests
{
    [Fact]
    public void ANameWithASpaceMustBeQuotedInsteadOfBeingCutAtTheFirstWord()
    {
        // "observer token set My Laptop" used to keep the token under "My" without a word, while
        // machines.json says "My Laptop": the dashboard then reported the token missing and
        // suggested the very same command. The test host redirects standard input, so the
        // command reads Console.In, which is empty here: even a regression cannot write
        // anything, it would stop at "No token was given" and exit with 1, not 2.
        TextReader original = Console.In;
        Console.SetIn(new StringReader(string.Empty));

        try
        {
            Assert.Equal(2, Commands.Run(["token", "set", "My", "Laptop"]));
        }
        finally
        {
            Console.SetIn(original);
        }
    }
}