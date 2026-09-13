using System.Text.Json;

namespace Observer.Service.Credentials;

/// <summary>
/// Reads and writes the machine token store.
/// </summary>
/// <remarks>
/// The write recipe is not the obvious one, and every one of its steps fixes a measured
/// failure:
/// <list type="bullet">
/// <item>the temporary file sits in the SAME directory, otherwise the replacement is not
/// atomic;</item>
/// <item>it is created ALREADY protected, because on Windows the replacement makes the
/// TEMPORARY file's DACL win: a temporary file with inherited permissions downgrades the store
/// to readable by anyone, silently;</item>
/// <item>it is created with CreateNew and never with Create, because Create on an existing file
/// IGNORES the descriptor it is handed and leaves standing the one that was there;</item>
/// <item>it is deleted in a finally, because a failed replacement would leave it on the disk
/// with the secret in the clear.</item>
/// </list>
/// </remarks>
public static class CredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>Reads the store.</summary>
    /// <param name="path">The path of the file.</param>
    /// <returns>The credentials, or null if the store does not exist yet.</returns>
    /// <exception cref="InvalidOperationException">If it exists but is not usable.</exception>
    /// <remarks>
    /// It does not use File.Exists: on a file that is genuinely protected, File.Exists returns
    /// false even when the file is there. Branching on that would regenerate the key at every
    /// start, cutting off every remote client without anyone understanding why.
    /// </remarks>
    public static MachineCredentials? Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string content;

        try
        {
            content = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException error)
        {
            // "I can't read it" is NOT "it isn't there": confusing the two would regenerate the key.
            throw new InvalidOperationException(
                $"The credential store '{path}' exists but can't be read by this process. " +
                "The machine token will not be regenerated, because that would lock out every " +
                "remote client. Run the service as LocalSystem, or repair the file permissions.",
                error);
        }

        try
        {
            return JsonSerializer.Deserialize<MachineCredentials>(content, JsonOptions)
                ?? throw new InvalidOperationException(
                    $"The credential store '{path}' is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException(
                $"The credential store '{path}' isn't valid JSON ({error.Message}). " +
                "Observer will not overwrite it: if the file was hand-edited, fix it; if it is " +
                "damaged, delete it and the service will create a new machine token.",
                error);
        }
    }

    /// <summary>Writes the store, atomically and without losing the permissions.</summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="credentials">The credentials to store.</param>
    public static void Write(string path, MachineCredentials credentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(credentials);

        // In the store's own directory: a Move across different volumes is not atomic.
        string tempPath = path + ".new";

        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            using (Stream stream = CredentialFile.CreateProtected(tempPath))
            {
                JsonSerializer.Serialize(stream, credentials, JsonOptions);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            // A failed replacement would leave the secret here in the clear, and with the
            // directory's inherited permissions instead of the store's own.
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}