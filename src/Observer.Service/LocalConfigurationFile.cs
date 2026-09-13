namespace Observer.Service;

/// <summary>The local configuration file, which may be there and empty.</summary>
/// <remarks>
/// <c>AddJsonFile(optional: true)</c> tolerates an ABSENT file, not an EMPTY one: zero bytes
/// make start-up fail with <c>The input does not contain any JSON tokens</c> and a stack
/// trace. And emptying the file is exactly what one does to take out the token it holds,
/// now that the service generates it by itself.
/// </remarks>
public static class LocalConfigurationFile
{
    /// <summary>The file name, the same on every system.</summary>
    public const string FileName = "appsettings.Local.json";

    /// <summary>Whether the file has anything to read.</summary>
    /// <param name="path">The full path of the file.</param>
    /// <returns>False if it is absent or contains nothing but whitespace.</returns>
    /// <remarks>
    /// A file with WRONG JSON inside is to be loaded all the same, and must fail: that is a
    /// real error, and hiding it would leave the user wondering why their token is not being
    /// read. The tolerance holds only for "there is nothing to read", which is indistinguishable
    /// from the file being absent.
    /// </remarks>
    public static bool ShouldLoad(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            return !string.IsNullOrWhiteSpace(File.ReadAllText(path));
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            // Unreadable for some other reason: it is left to load, so the real fault
            // surfaces from the configuration loader instead of being swallowed here.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}