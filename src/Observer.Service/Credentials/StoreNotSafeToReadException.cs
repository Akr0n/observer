namespace Observer.Service.Credentials;

/// <summary>Why root will not read a file in the credential folder.</summary>
/// <remarks>
/// The kind is what lets a message say the truth. A link and a FIFO are things somebody put in the
/// folder; a file this machine could not check is nothing of the sort, and telling its owner to
/// delete the store because of it would be advice that does harm.
/// </remarks>
public enum StoreRefusalKind
{
    /// <summary>A FIFO, a device, a directory or a socket where a file belongs.</summary>
    NotAPlainFile,

    /// <summary>A symbolic link where a file belongs.</summary>
    SymbolicLink,

    /// <summary>A file with a second name, that belongs to an account other than the folder's.</summary>
    SecondName,

    /// <summary>A file larger than root will read.</summary>
    TooLarge,

    /// <summary>A file that changed while it was being read.</summary>
    Unstable,

    /// <summary>A file another process holds for the moment, which may well be the service writing it.</summary>
    Busy,

    /// <summary>Nothing is known to be wrong with the file: this machine could not check it.</summary>
    Machine,
}

/// <summary>What the kernel says a file in the credential folder is.</summary>
/// <param name="Type">The kind of file, the bits of the mode under <see cref="UnixSafeRead.FileTypeMask"/>.</param>
/// <param name="Links">How many names it has.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Owner">The uid it belongs to.</param>
public readonly record struct StoreShape(uint Type, uint Links, ulong Size, uint Owner);

/// <summary>A file in the credential folder that root will not read, because of what it is.</summary>
/// <remarks>
/// It derives from <see cref="InvalidOperationException"/> on purpose: that is what
/// <see cref="CredentialStore.Read"/> already throws for a store that exists and cannot be used, so
/// every caller that knows how to refuse that knows how to refuse this.
/// </remarks>
public sealed class StoreNotSafeToReadException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="path">The file that was refused.</param>
    /// <param name="reason">What it is, finishing the sentence "The file '...' ...".</param>
    /// <param name="kind">Why it was refused.</param>
    public StoreNotSafeToReadException(string path, string reason, StoreRefusalKind kind = StoreRefusalKind.NotAPlainFile)
        : base("The file '" + path + "' " + reason + ".")
    {
        FilePath = path;
        Kind = kind;
    }

    /// <summary>The file that was refused.</summary>
    public string FilePath { get; }

    /// <summary>Why it was refused.</summary>
    public StoreRefusalKind Kind { get; }
}