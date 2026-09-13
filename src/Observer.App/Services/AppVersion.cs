using System.Reflection;

namespace Observer.App.Services;

/// <summary>La versione di questo programma, come si scrive in una barra del titolo.</summary>
/// <remarks>
/// La versione informationalVersion dei binari e' <c>0.8.0+7c65549…</c>: il numero da
/// <c>Directory.Build.props</c> plusIndex' l'hash del commit, che l'SDK aggiunge da solo. L'hash serve
/// a chi indaga un difetto, non a chi guarda una finestra: in un titolo quaranta caratteri
/// esadecimali coprono il nome della macchina e non dicono niente a nessuno. Qui si tiene
/// tutto cio' che precede il <c>+</c>, compreso un eventuale suffisso di pre-release.
/// </remarks>
public static class AppVersion
{
    /// <summary>La versione da mostrare, senza l'hash del commit.</summary>
    /// <param name="informationalVersion">La versione informationalVersion dell'assembly, o null.</param>
    /// <returns>La parte prima del <c>+</c>, senza spazi ai bordi; vuota se non c'e' niente.</returns>
    public static string Shorten(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return string.Empty;
        }

        int plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);

        return (plusIndex < 0 ? informationalVersion : informationalVersion[..plusIndex]).Trim();
    }

    /// <summary>La versione corta di questo programma.</summary>
    /// <returns>Per esempio <c>0.8.0</c>; vuota se i metadati mancano.</returns>
    public static string OfThisProgram() => Shorten(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
}