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
        // only. And the owner is NOT necessarily whoever is writing: the service runs as the
        // "observer" account, but "sudo observer rotate-key" writes as root, and a new file
        // belongs to its creator. So the file is handed to its directory's owner while it is
        // still empty, before a byte of the secret goes in.
        FileStream stream = new(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });

        if (OperatingSystem.IsLinux())
        {
            try
            {
                UnixOwnership.FollowDirectory(stream);
            }
            catch
            {
                // The caller never gets the stream, so it cannot close it: it would hold its
                // descriptor until the finalizer ran.
                stream.Dispose();
                throw;
            }
        }

        return stream;
    }
}