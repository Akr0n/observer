using Observer.Core.Platform;
using Observer.Core.Platform.Linux;
using Observer.Core.Platform.Windows;
using Observer.Core.Processes;

namespace Observer.Core.Composition;

/// <summary>Quale lettore dell'I/O per processo su quale sistema.</summary>
public static class ProcessIoReaders
{
    /// <summary>Il lettore per la piattaforma indicata, o null dove non ce n'e' uno.</summary>
    /// <param name="platform">La piattaforma, come parametro e non come lettura dell'ambiente.</param>
    /// <param name="fileReader">Da dove leggere i file di sistema, su Linux.</param>
    /// <returns>Il lettore, oppure null: l'elenco dei processi funziona lo stesso, senza I/O.</returns>
    public static IProcessIoReader? Per(HostPlatform platform, IFileTextReader fileReader) => platform switch
    {
        HostPlatform.Windows => new WindowsProcessIoReader(),
        HostPlatform.Linux => new LinuxProcessIoReader(fileReader),
        _ => null,
    };
}