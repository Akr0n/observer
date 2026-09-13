namespace Observer.Core.Units;

/// <summary>
/// A quantity of bytes. It exists to make it impossible to confuse the source units:
/// /proc/meminfo writes "kB" but means KiB (1024 bytes), and getting that factor wrong produces
/// credible, wrong numbers instead of an error.
/// </summary>
public readonly record struct ByteSize
{
    private ByteSize(long bytes) => Bytes = bytes;

    /// <summary>Quantity in bytes.</summary>
    public long Bytes { get; }

    /// <summary>Builds from a value already expressed in bytes.</summary>
    public static ByteSize FromBytes(long bytes) => new(bytes);

    /// <summary>
    /// Builds from kibibytes (1024 bytes). This is the factory to use for /proc/meminfo,
    /// which labels its values "kB" even though they are KiB.
    /// </summary>
    public static ByteSize FromKibibytes(long kibibytes) => new(kibibytes * 1024L);

    /// <summary>
    /// Subtraction that stops at zero instead of going negative. It is needed because on
    /// some virtual machines "available" momentarily exceeds "total": without
    /// saturation the used amount would become negative and the chart would go wrong silently.
    /// </summary>
    public ByteSize SaturatingSubtract(ByteSize other) =>
        new(Bytes > other.Bytes ? Bytes - other.Bytes : 0L);
}
