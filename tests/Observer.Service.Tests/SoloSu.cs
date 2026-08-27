namespace Observer.Service.Tests;

/// <summary>Un fatto che fuori da Windows viene SALTATO invece che fallire.</summary>
/// <remarks>
/// xunit 2.9.3 non ha Assert.Skip: l'unico modo di saltare per piattaforma e' valorizzare Skip
/// nel costruttore dell'attributo. Saltare e' l'esito giusto, non una rinuncia: un test di
/// named pipe che fallisse su ubuntu-latest renderebbe rosso il runner sbagliato e
/// nasconderebbe i guasti veri.
/// </remarks>
public sealed class SoloSuWindowsAttribute : FactAttribute
{
    /// <summary>Salta se il sistema non e' Windows.</summary>
    public SoloSuWindowsAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Named pipe e identita' di Windows: eseguito solo su windows-latest.";
        }
    }
}

/// <summary>Un fatto che fuori da Linux viene SALTATO invece che fallire.</summary>
public sealed class SoloSuLinuxAttribute : FactAttribute
{
    /// <summary>Salta se il sistema non e' Linux.</summary>
    public SoloSuLinuxAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "SO_PEERCRED esiste solo su Linux: eseguito solo su ubuntu-latest.";
        }
    }
}