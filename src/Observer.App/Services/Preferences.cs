using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Styling;

namespace Observer.App.Services;

/// <summary>Where the window was last time.</summary>
/// <param name="X">Left edge, in physical screen pixels.</param>
/// <param name="Y">Top edge, in physical screen pixels.</param>
/// <param name="Width">Width, in logical pixels (the ones the window measures itself in).</param>
/// <param name="Height">Height, in logical pixels.</param>
/// <param name="Maximized">True if it was maximized: then X, Y and the sizes are the earlier ones.</param>
/// <remarks>
/// Physical position and logical sizes, and that is not an inconsistency: it is how Avalonia
/// exposes them (<c>Position</c> is a <c>PixelPoint</c>, <c>Width</c> is in DPI-independent
/// units), and converting one into the other with yesterday's screen scale would give a window
/// of a different size the day the scale changes.
/// </remarks>
public sealed record WindowPlacement(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("maximized")] bool Maximized)
{
    /// <summary>How much of the window must sit inside a screen for it to be grabbable.</summary>
    /// <remarks>
    /// A square of 120 physical pixels from the top left corner: it holds the icon and a piece
    /// of the title bar, that is, the minimum needed to drag the window away if the rest is
    /// outside. The case this avoids is a disconnected monitor: with no check the window would
    /// reopen on a screen that is no longer there, invisible and with no way to reach it.
    /// </remarks>
    public const int MinimumGrabbableSize = 120;

    /// <summary>How far the left edge may stick out of the screen.</summary>
    /// <remarks>
    /// Windows draws an invisible border of 7-8 pixels around every window, and a window
    /// snapped to the left edge (Win+Arrow) sits at X = -8: without this tolerance it would
    /// never be remembered. Not at the top: there is no invisible border at the top, and a
    /// maximized window sits at (-8, -8), which is therefore left out.
    /// </remarks>
    public const int LeftEdgeTolerance = 16;

    /// <summary>A placement carrying only the state: it does not pass <see cref="WithinAnyOf"/>.</summary>
    private static WindowPlacement StateOnlyPlacement => new(0, 0, 0, 0, Maximized: false);

    /// <summary>A work area, in physical pixels.</summary>
    /// <param name="X">Left edge.</param>
    /// <param name="Y">Top edge.</param>
    /// <param name="Width">Width.</param>
    /// <param name="Height">Height.</param>
    public readonly record struct WorkArea(int X, int Y, int Width, int Height);

    /// <summary>This placement, if it sits on one of the screens there are now; otherwise null.</summary>
    /// <param name="screens">The work areas of the connected screens.</param>
    /// <returns>Itself, or null when the window would reopen off every screen.</returns>
    public WindowPlacement? WithinAnyOf(IReadOnlyList<WorkArea> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);

        if (Width < MinimumGrabbableSize || Height < MinimumGrabbableSize)
        {
            return null;
        }

        foreach (WorkArea screen in screens)
        {
            // The constants are added to and subtracted from the screen's edge, NEVER from
            // X or Y: with a hand-written file saying x = 2147483647 the sum overflowed, the
            // comparison passed, and the window opened invisible - and saved itself back
            // identical at every close.
            if (X >= screen.X - LeftEdgeTolerance
                && Y >= screen.Y
                && X <= screen.X + screen.Width - MinimumGrabbableSize
                && Y <= screen.Y + screen.Height - MinimumGrabbableSize)
            {
                return this;
            }
        }

        return null;
    }

    /// <summary>What to remember at close, depending on the state of the window.</summary>
    /// <param name="minimized">True if the window is minimized.</param>
    /// <param name="maximized">
    /// True if it is maximized, or if it was before being minimized.
    /// </param>
    /// <param name="lastNormal">
    /// The last geometry seen in the normal state during this session, if there was one.
    /// </param>
    /// <param name="saved">The geometry read from the file at start-up, if there was one.</param>
    /// <param name="current">The current geometry, which only holds for a normal window.</param>
    /// <returns>The placement to write, or null if there is nothing sensible to say.</returns>
    /// <remarks>
    /// The size of a maximized window is the screen's, and the position of a minimized one
    /// is outside every screen: in those two states what is remembered is the last normal
    /// geometry of THIS session, not the one read from the file at start-up - which is what
    /// was done before, and a move made before maximizing was lost: on two monitors the
    /// window reopened on the wrong one. If no normal geometry is known, the maximized
    /// state is remembered on its own, with a placement that
    /// <see cref="WithinAnyOf"/> rejects: the window opens where the system decides, but maximized.
    /// </remarks>
    public static WindowPlacement? AtClose(
        bool minimized,
        bool maximized,
        WindowPlacement? lastNormal,
        WindowPlacement? saved,
        WindowPlacement current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (!minimized && !maximized)
        {
            return current with { Maximized = false };
        }

        WindowPlacement? normal = lastNormal ?? saved;

        if (maximized)
        {
            return (normal ?? StateOnlyPlacement) with { Maximized = true };
        }

        return normal is null ? null : normal with { Maximized = false };
    }
}

/// <summary>An entry of the zoom selector.</summary>
/// <param name="Factor">The zoom: 1 is the normal size.</param>
/// <remarks>
/// The entry's text IS its <see cref="ToString"/>: a screen reader announces that, and with
/// a bare double it announced "1,15" instead of "115 %". The record's value equality is what
/// makes the entry findable again from the number.
/// </remarks>
public sealed record ZoomOption(double Factor)
{
    /// <inheritdoc />
    public override string ToString() => Factor.ToString("P0", CultureInfo.CurrentCulture);
}

/// <summary>An entry of the history period selector.</summary>
/// <param name="Key">What goes in the file: <c>1h</c>, <c>24h</c> or <c>7d</c>.</param>
/// <remarks>
/// The bar step is not the source's, and the two numbers answer different questions. The
/// SOURCE is what the service keeps: samples every minute for seven days, every five minutes
/// for ninety. The bar STEP is how wide an interval has to be for the strip to hold it: about
/// ninety bars over eight hundred pixels give bars of nine, which is the minimum for seeing
/// them apart. Hence the table: one hour at one minute makes sixty bars, one day at a quarter
/// of an hour makes ninety-six, one week at two hours makes eighty-four.
/// <para>
/// Ninety days is NOT there, even though the service keeps them: at five minutes that would be
/// 25 920 points, over the cap the service puts on a response, and widening the bar until they
/// fit would leave the strip saying one single thing for every day and a half. A chart that
/// lies is worse than a chart that is missing.
/// </para>
/// </remarks>
public sealed record HistoryPeriodOption(string Key)
{
    /// <inheritdoc />
    public override string ToString() => Key switch
    {
        "24h" => "24 hours",
        "7d" => "7 days",
        _ => "1 hour",
    };

    /// <summary>How much history the strip shows.</summary>
    public TimeSpan Duration => Key switch
    {
        "24h" => TimeSpan.FromHours(24),
        "7d" => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(1),
    };

    /// <summary>How long one interval of the strip lasts.</summary>
    public TimeSpan Step => Key switch
    {
        "24h" => TimeSpan.FromMinutes(15),
        "7d" => TimeSpan.FromHours(2),
        _ => TimeSpan.FromMinutes(1),
    };

    /// <summary>The resolution to ask the service for.</summary>
    public string Resolution => Key switch
    {
        "1h" => "1m",
        _ => "5m",
    };

    /// <summary>How long one source point lasts, in order to align the raw tail.</summary>
    public TimeSpan SourceStep => Key switch
    {
        "1h" => TimeSpan.FromMinutes(1),
        _ => TimeSpan.FromMinutes(5),
    };

    /// <summary>The title above the strip.</summary>
    public string Title => Key switch
    {
        "24h" => "Last 24 hours",
        "7d" => "Last 7 days",
        _ => "Last hour",
    };

    /// <summary>How many bars the strip has.</summary>
    public int BarCount => (int)(Duration / Step);
}

/// <summary>An entry of the theme selector: the system's, light or dark.</summary>
/// <param name="Key">What goes in the file: <c>system</c>, <c>light</c> or <c>dark</c>.</param>
/// <remarks>
/// Like <see cref="ZoomOption"/>: the entry's text is its <see cref="ToString"/>, and it is
/// what the drop-down shows and what a screen reader announces.
/// </remarks>
public sealed record ThemeOption(string Key)
{
    /// <inheritdoc />
    public override string ToString() => Key switch
    {
        "light" => "Light",
        "dark" => "Dark",
        _ => "System",
    };

    /// <summary>The theme variant for a key: the default one follows the system.</summary>
    /// <param name="key">The key, whether already allowed or not.</param>
    /// <returns>The variant to ask the application for.</returns>
    /// <remarks>
    /// It is the only branch with a real decision: a typo here would make no noise, it would
    /// only leave a light window to whoever asked for the dark one. That is why it is tested
    /// on its own.
    /// </remarks>
    public static ThemeVariant VariantFor(string key) => key switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}

/// <summary>What the dashboard remembers about itself between one start and the next.</summary>
/// <param name="Placement">Where the window was, or null if it does not know yet.</param>
/// <param name="Zoom">How much the window is scaled: 1 is the normal size, below 1 it is
/// smaller.</param>
/// <param name="Theme">The chosen theme: <c>system</c>, <c>light</c> or <c>dark</c>.</param>
/// <param name="MachineName">
/// The name of the machine that was being watched, or null for this computer.
/// </param>
/// <param name="HistoryPeriod">How much history the strip shows: <c>1h</c>, <c>24h</c> or <c>7d</c>.</param>
/// <remarks>
/// A separate file and not <c>client.json</c>: that one carries a credential, and a program that
/// rewrote it at every close to save a few preferences would be a program that rewrites a
/// credential at every close. They are ALL positional parameters with no default value, on
/// purpose: whoever builds the preferences has to state them all, and a
/// <c>new Preferences(placement, zoom)</c> that forgets one does not compile — which is how,
/// the day another one is added, every call site that needs updating gets found.
/// </remarks>
public sealed record Preferences(
    [property: JsonPropertyName("window")] WindowPlacement? Placement,
    // The key stays textScale even though the interface says Zoom: renaming it would lose
    // everyone's saved zoom, and an earlier version would no longer read it.
    [property: JsonPropertyName("textScale")] double Zoom,
    [property: JsonPropertyName("theme")] string Theme,
    // The machine's NAME, not its address and much less its token: it is already the key
    // the client finds the credential with, and it is the only thing machines.json cannot
    // change underneath without it being a different machine. Null means "this computer",
    // which is also what a file written by an earlier version reads as.
    [property: JsonPropertyName("machine")] string? MachineName,
    [property: JsonPropertyName("historyWindow")] string HistoryPeriod)
{
    /// <summary>The normal size: 1.</summary>
    public const double NormalZoom = 1.0d;

    /// <summary>The scales that can be chosen, in increasing order.</summary>
    /// <remarks>
    /// Steps and not a continuous slider: the window redraws at every notch. Above normal they
    /// are the ones Windows itself offers for text (115, 130, 150). Below, two steps for
    /// seeing more without scrolling: in a 900x700 window with six gauges the history rows in
    /// view go from one to three (85) and four (75), and the columns of gauges from four to
    /// five; on a big screen the six already fit on one row at 100, so the gain is vertical.
    /// The floor is 75 and it goes NO lower: Fluent controls of 32 px become 24 logical, and
    /// the status ring, captured at 75 % in both states, keeps a hole of 5 physical px at
    /// 125 DPI (4 simulated at 100 DPI). At 67 the controls would be 21 px and the text body
    /// 9 physical px on a screen at 100 %, below any system text size. The price of 75 is the
    /// captions: 9 logical px.
    /// </remarks>
    public static readonly IReadOnlyList<double> AllowedZoomLevels = [0.75d, 0.85d, 1.0d, 1.15d, 1.3d, 1.5d];

    /// <summary>The themes that can be chosen. The first is the system's.</summary>
    public static readonly IReadOnlyList<string> AllowedThemes = ["system", "light", "dark"];

    /// <summary>The history periods to choose between. The first is the one that was always there.</summary>
    public static readonly IReadOnlyList<string> AllowedPeriods = ["1h", "24h", "7d"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The preferences of someone who has not saved any yet.</summary>
    public static Preferences Defaults =>
        new(null, NormalZoom, AllowedThemes[0], null, AllowedPeriods[0]);

    /// <summary>The machine to reopen on: the remembered one if it is still there, otherwise the first.</summary>
    /// <param name="machines">The list read just now, in order: the first is this computer.</param>
    /// <param name="name">The remembered name, or null.</param>
    /// <returns>The entry to open on, or null if the list is empty.</returns>
    /// <remarks>
    /// The name and not the index: just reorder <c>machines.json</c> and an index would open
    /// a different machine, with a different credential, and nothing would say so. A name that is
    /// no longer there — entry removed, renamed — is not an error to report: it starts again
    /// from this computer, which is where it started from the first time.
    /// <para>
    /// The comparison is ordinal and trimmed of spaces on both sides: inside
    /// <c>machines.json</c> the name arrives raw, and on Linux two names that differ only
    /// in case are two different credentials.
    /// </para>
    /// </remarks>
    public static ObserverEndpoint? RememberedMachine(IReadOnlyList<ObserverEndpoint> machines, string? name)
    {
        ArgumentNullException.ThrowIfNull(machines);

        if (machines.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return machines[0];
        }

        string wanted = name.Trim();

        foreach (ObserverEndpoint endpoint in machines)
        {
            if (string.Equals(endpoint.Name?.Trim(), wanted, StringComparison.Ordinal))
            {
                return endpoint;
            }
        }

        return machines[0];
    }

    /// <summary>The requested zoom if it is one of the allowed ones, otherwise the normal one.</summary>
    /// <param name="zoom">The zoom read from the file, or chosen.</param>
    /// <returns>An allowed zoom.</returns>
    public static double NormalizeZoom(double zoom) =>
        AllowedZoomLevels.Contains(zoom) ? zoom : NormalZoom;

    /// <summary>The requested theme if it is one of the allowed ones, otherwise the system's.</summary>
    /// <param name="theme">The theme read from the file, or chosen; may also be null.</param>
    /// <returns>An allowed key, in lower case.</returns>
    /// <remarks>
    /// Case does not matter: a hand-written file with <c>"Dark"</c> means dark. Everything else -
    /// null, a missing field, a made-up word - counts as the system.
    /// </remarks>
    public static string NormalizeTheme(string? theme)
    {
        foreach (string allowed in AllowedThemes)
        {
            if (string.Equals(allowed, theme, StringComparison.OrdinalIgnoreCase))
            {
                return allowed;
            }
        }

        return AllowedThemes[0];
    }

    /// <summary>The requested period if it is one of the allowed ones, otherwise one hour.</summary>
    /// <param name="period">The period read from the file, or chosen; may also be null.</param>
    /// <returns>An allowed key.</returns>
    /// <remarks>
    /// A file written by an earlier version does not have the field, so null arrives here and
    /// it falls back to one hour, which is what that version showed: no migration to do.
    /// </remarks>
    public static string NormalizePeriod(string? period)
    {
        foreach (string allowed in AllowedPeriods)
        {
            if (string.Equals(allowed, period, StringComparison.OrdinalIgnoreCase))
            {
                return allowed;
            }
        }

        return AllowedPeriods[0];
    }

    /// <summary>Reads the preferences from a file, tolerating everything that can go wrong.</summary>
    /// <param name="json">The file's content, or null if there is none.</param>
    /// <returns>The preferences, or the default ones: a broken file does not stop the window.</returns>
    public static Preferences From(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Defaults;
        }

        try
        {
            Preferences? parsed = JsonSerializer.Deserialize<Preferences>(json, JsonOptions);

            return parsed is null
                ? Defaults
                : parsed with
                {
                    Zoom = NormalizeZoom(parsed.Zoom),
                    Theme = NormalizeTheme(parsed.Theme),
                    HistoryPeriod = NormalizePeriod(parsed.HistoryPeriod),
                };
        }
        catch (JsonException)
        {
            return Defaults;
        }
    }

    /// <summary>The preferences as they are written into the file.</summary>
    /// <returns>JSON.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>The preferences file, next to the configuration one.</summary>
public static class PreferencesStore
{
    /// <summary>Path of the file: <c>preferences.json</c> in <c>client.json</c>'s folder.</summary>
    public static string FilePath => Path.Combine(
        Path.GetDirectoryName(ClientConfiguration.FilePath) ?? ".",
        "preferences.json");

    /// <summary>Reads the file. A missing or unreadable file counts as the default preferences.</summary>
    /// <returns>The preferences.</returns>
    public static Preferences Read()
    {
        try
        {
            return Preferences.From(File.Exists(FilePath) ? File.ReadAllText(FilePath) : null);
        }
        catch (IOException)
        {
            return Preferences.Defaults;
        }
        catch (UnauthorizedAccessException)
        {
            return Preferences.Defaults;
        }
    }

    /// <summary>Writes the file. If it fails, it does not say so: a lost preference is not a fault.</summary>
    /// <param name="preferences">What to remember.</param>
    public static void Write(Preferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ".");

            // First to a temporary file, then in place of the real one: a close interrupted
            // half-way through the write does not leave a truncated file, which at the next
            // start-up would count as "no preferences" and forget everything at once.
            string tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, preferences.ToJson());
            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch (IOException)
        {
            // The window opens all the same, wherever it lands: that is what it did before.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}