using Observer.Core.Platform.Windows;

namespace Observer.Core.Tests;

/// <summary>
/// <c>GetProcessIoCounters</c> sul processo vero che sta eseguendo il test.
/// </summary>
/// <remarks>
/// Non un finto: la cosa da verificare e' che il P/Invoke sia dichiarato giusto - diritto di
/// accesso, struttura da 48 byte - e quello lo dice solo Windows. Il contatore deve CRESCERE
/// dopo una scrittura: leggerlo una volta sola proverebbe che la chiamata non fallisce, non che
/// legge il numero giusto.
/// </remarks>
public class WindowsProcessIoTests
{
    [SoloSuWindows]
    public void IlProprioContatoreCresceDopoUnaScrittura()
    {
        WindowsProcessIoReader lettore = new();
        int pid = Environment.ProcessId;

        Assert.True(lettore.TryRead(pid, out ulong prima));

        string percorso = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            File.WriteAllBytes(percorso, new byte[1 << 20]);
        }
        finally
        {
            File.Delete(percorso);
        }

        Assert.True(lettore.TryRead(pid, out ulong dopo));
        Assert.True(dopo >= prima + (1UL << 20), $"prima {prima}, dopo {dopo}");
    }

    [SoloSuWindows]
    public void UnPidCheNonEsisteNonSiLegge()
    {
        Assert.False(new WindowsProcessIoReader().TryRead(2147483646, out ulong byteIo));
        Assert.Equal(0UL, byteIo);
    }
}