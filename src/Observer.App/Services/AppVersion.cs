using System.Reflection;

namespace Observer.App.Services;

/// <summary>This program's version, as it is written in a title bar.</summary>
/// <remarks>
/// The informational version of the binaries is <c>0.8.0+7c65549…</c>: the number from
/// <c>Directory.Build.props</c> plus the commit hash, which the SDK adds on its own. The hash is
/// for whoever investigates a defect, not for whoever is looking at a window: in a title, forty
/// hexadecimal characters crowd out the machine name and tell nobody anything. What is kept
/// here is everything before the <c>+</c>, including any pre-release suffix.
/// </remarks>
public static class AppVersion
{
    /// <summary>The version to show, without the commit hash.</summary>
    /// <param name="informationalVersion">The assembly's informational version, or null.</param>
    /// <returns>The part before the <c>+</c>, trimmed; empty if there is nothing.</returns>
    public static string Shorten(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return string.Empty;
        }

        int plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);

        return (plusIndex < 0 ? informationalVersion : informationalVersion[..plusIndex]).Trim();
    }

    /// <summary>The short version of this program.</summary>
    /// <returns>For example <c>0.8.0</c>; empty if the metadata is missing.</returns>
    public static string OfThisProgram() => Shorten(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
}