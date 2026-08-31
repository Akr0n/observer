using Observer.Core.Metrics.Disk;
using Observer.Core.Platform.Windows;

namespace Observer.Core.Tests;

/// <summary>Un fatto che fuori da Windows viene SALTATO invece che fallire.</summary>
/// <remarks>
/// Gemello di quello in <c>Observer.Service.Tests/SoloSu.cs</c>: duplicato e non condiviso
/// perche' i due progetti di test non si referenziano, ed e' venti righe contro una
/// dipendenza fra assembly di prova.
/// </remarks>
public sealed class SoloSuWindowsAttribute : FactAttribute
{
    /// <summary>Salta se il sistema non e' Windows.</summary>
    public SoloSuWindowsAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "IOCTL_DISK_PERFORMANCE esiste solo su Windows: eseguito solo su windows-latest.";
        }
    }
}

/// <summary>
/// Il provider Windows contro i dischi VERI di questa macchina.
/// </summary>
/// <remarks>
/// Esiste per un guasto che nessun test costruito a tavolino puo' vedere. La struct
/// <c>DISK_PERFORMANCE</c> misura 88 byte, e i suoi ultimi 16 sono un nome che a noi non
/// serve: scriverli come stringa ANSI invece che Unicode ne fa una da 80, e allora l'IOCTL
/// risponde ERROR_INSUFFICIENT_BUFFER e non legge <b>niente</b>. Il provider degrada in
/// silenzio — zero dischi, nessun errore — e la dashboard mostrerebbe una sezione vuota che
/// si legge come "questa macchina non ha dischi".
/// <para>
/// E' successo davvero, al primo tentativo, su tutti i dischi. La misura che lo ha scoperto
/// era a mano; questo test la rende ripetibile.
/// </para>
/// </remarks>
public class WindowsDiskActivityTests
{
    [SoloSuWindows]
    public void IlProviderVedeIDischiFisiciDiQuestaMacchina()
    {
        WindowsDiskActivityProvider provider = new();

        Assert.True(provider.TryRead(out IReadOnlyList<DiskActivityReading> letture));

        // Se la struct fosse della dimensione sbagliata, qui ci sarebbe zero — senza errori,
        // senza eccezioni, senza niente da cui accorgersene.
        Assert.NotEmpty(letture);
    }

    [SoloSuWindows]
    public void IContatoriLettiSonoQuelliDiUnaMacchinaCheHaGiaLavorato()
    {
        WindowsDiskActivityProvider provider = new();

        Assert.True(provider.TryRead(out IReadOnlyList<DiskActivityReading> letture));

        DiskActivityReading primo = letture[0];

        // Il tempo di INATTIVITA' e' quello che conta Windows, e su un disco acceso da un
        // po' non puo' essere zero. Se lo fosse, vorrebbe dire che il campo letto non e'
        // quello giusto — cioe' che gli offset della struct sono spostati, che e' il modo
        // silenzioso in cui un P/Invoke sbaglia.
        Assert.True(
            primo.Idle > TimeSpan.Zero,
            $"il tempo di inattivita' di {primo.Instance} e' {primo.Idle}, che su una macchina accesa non ha senso");

        Assert.Null(primo.Busy);
        Assert.StartsWith("Disk ", primo.Instance, StringComparison.Ordinal);
    }
}