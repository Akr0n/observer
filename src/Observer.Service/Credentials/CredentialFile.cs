namespace Observer.Service.Credentials;

/// <summary>Creates the store's file already with the right permissions.</summary>
/// <remarks>
/// "Already" is the important word: creating the file and then applying the permissions leaves a
/// window in which the secret sits on disk with the ones inherited from the directory.
/// </remarks>
public static class CredentialFile
{
    /// <summary>Creates a new file, readable only by whoever has to read it.</summary>
    /// <param name="path">The path of the file to create.</param>
    /// <returns>The stream to write to.</returns>
    /// <exception cref="IOException">If the file already exists.</exception>
    public static Stream CreateProtected(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (OperatingSystem.IsWindows())
        {
            return WindowsCredentialFile.CreateProtected(path);
        }

        // On Unix the mode is passed at creation time, so no window exists. 0600: the owner
        // only. The service runs as root and the owner is root by construction, which saves a
        // call to chown that .NET does not offer.
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }
}