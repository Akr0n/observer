using Observer.Core.Platform;
using Observer.Core.Platform.Linux;
using Observer.Core.Platform.Windows;
using Observer.Core.Processes;

namespace Observer.Core.Composition;

/// <summary>Which per-process I/O reader on which system.</summary>
public static class ProcessIoReaders
{
    /// <summary>The reader for the given platform, or null where there is none.</summary>
    /// <param name="platform">The platform, as a parameter and not as a read of the environment.</param>
    /// <param name="fileReader">Where to read the system files from, on Linux.</param>
    /// <returns>The reader, or null: the process list works all the same, without I/O.</returns>
    public static IProcessIoReader? For(HostPlatform platform, IFileTextReader fileReader) => platform switch
    {
        HostPlatform.Windows => new WindowsProcessIoReader(),
        HostPlatform.Linux => new LinuxProcessIoReader(fileReader),
        _ => null,
    };
}