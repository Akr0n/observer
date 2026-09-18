using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>
/// What the window remembers about itself, and when it has to forget it.
/// </summary>
/// <remarks>
/// The rule that matters is the unplugged screen: a placement saved on a monitor that is no
/// longer there would reopen the window where nobody can see it or grab it.
/// </remarks>
public class PreferencesTests
{
    private static readonly WindowPlacement.WorkArea PrimaryScreen = new(0, 0, 1920, 1040);

    private static readonly WindowPlacement.WorkArea RightScreen = new(1920, 0, 2560, 1400);

    [Fact]
    public void WithNoFileTheDefaultsApply()
    {
        Assert.Equal(Preferences.Defaults, Preferences.From(null));
        Assert.Equal(Preferences.Defaults, Preferences.From(string.Empty));
        Assert.Null(Preferences.Defaults.Placement);
        Assert.Equal(1.0d, Preferences.Defaults.Zoom);
    }

    [Fact]
    public void ABrokenFileDoesNotStopTheWindow() =>
        Assert.Equal(Preferences.Defaults, Preferences.From("{ this is not json"));

    [Fact]
    public void ADisallowedZoomFallsBackToNormal()
    {
        // A hand-written file with 2.7 would give a window three times larger than the screen,
        // and one with 0.5 buttons of 16 px. A value BETWEEN two steps is refused too: a range
        // instead of the list would let 0.9 through, and the drop-down would have no entry to
        // select. The zoom levels are the ones in the list, pinned in full below.
        Assert.Equal(1.0d, Preferences.From("""{"textScale": 2.7}""").Zoom);
        Assert.Equal(1.0d, Preferences.From("""{"textScale": 0.5}""").Zoom);
        Assert.Equal(1.0d, Preferences.From("""{"textScale": 0.9}""").Zoom);
        Assert.Equal(1.15d, Preferences.From("""{"textScale": 1.15}""").Zoom);
        Assert.Equal(0.75d, Preferences.From("""{"textScale": 0.75}""").Zoom);
        Assert.Equal(1.0d, Preferences.From("""{}""").Zoom);
    }

    [Fact]
    public void RoundTripThroughJson()
    {
        Preferences original = new(
            new WindowPlacement(192, 100, 900, 700, Maximized: false), 1.3d, "dark", "laptop", "24h");

        Assert.Equal(original, Preferences.From(original.ToJson()));
        Assert.Contains("\"textScale\":1.3", original.ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"theme\":\"dark\"", original.ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"machine\":\"laptop\"", original.ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"historyWindow\":\"24h\"", original.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithoutThePeriodShowsTheLastHourAsBefore()
    {
        // Every file written before this version: the absence means the hour, which is what
        // that version showed. No migration.
        Assert.Equal("1h", Preferences.From("""{"theme": "dark"}""").HistoryPeriod);
        Assert.Equal("1h", Preferences.Defaults.HistoryPeriod);
    }

    [Fact]
    public void AnUnknownPeriodFallsBackToTheHourAndCaseIsIgnored()
    {
        Assert.Equal("1h", Preferences.From("""{"historyWindow": "1y"}""").HistoryPeriod);
        Assert.Equal("24h", Preferences.From("""{"historyWindow": "24H"}""").HistoryPeriod);
    }

    [Theory]
    [InlineData("1h", 60, 1)]
    [InlineData("24h", 96, 15)]
    [InlineData("7d", 84, 120)]
    public void EveryPeriodFitsTheStripAndItsBarIsAMultipleOfTheSourceStep(
        string key, int bars, int minutesPerBar)
    {
        // The constraint that holds the table up: about ninety bars over eight hundred pixels
        // give bars of nine, which is the floor for seeing them apart. Two thousand bars - which
        // is what seven days at the source step would give - would be below the pixel, that is
        // a strip that cannot be read.
        HistoryPeriodOption period = new(key);

        Assert.Equal(bars, period.BarCount);
        Assert.Equal(TimeSpan.FromMinutes(minutesPerBar), period.Step);
        Assert.InRange(period.BarCount, 50, 120);

        // And the bar step must be a multiple of the source one, or one interval would hold a
        // different number of points from the interval next to it.
        Assert.Equal(TimeSpan.Zero, period.Step - (period.SourceStep * (int)(period.Step / period.SourceStep)));
    }

    [Fact]
    public void ThePeriodOptionShowsItsLabelAndItsStripTitle()
    {
        Assert.Equal("1 hour", new HistoryPeriodOption("1h").ToString());
        Assert.Equal("24 hours", new HistoryPeriodOption("24h").ToString());
        Assert.Equal("7 days", new HistoryPeriodOption("7d").ToString());

        Assert.Equal("Last hour", new HistoryPeriodOption("1h").Title);
        Assert.Equal("Last 7 days", new HistoryPeriodOption("7d").Title);
    }

    [Fact]
    public void AFileWithoutTheMachineFieldOpensOnThisComputer()
    {
        // That is, every file written before this version: the absence of the field is exactly
        // what "this computer" means, so no migration is needed.
        Assert.Null(Preferences.From("""{"textScale": 1.15, "theme": "dark"}""").MachineName);
    }

    [Fact]
    public void TheRememberedMachineIsFoundByNameIgnoringSurroundingSpaces()
    {
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint remote = RemoteEndpoint("laptop");

        Assert.Equal(remote, Preferences.RememberedMachine([local, remote], "laptop"));

        // Surrounding spaces do not count: inside machines.json the name arrives raw.
        Assert.Equal(remote, Preferences.RememberedMachine([local, remote], "  laptop  "));

        // And they do NOT count on the entry's side either, which is the real case:
        // MachineDirectory passes the name exactly as it is written in the file, and an entry
        // " laptop " is the same machine as "laptop". Without the Trim on this side it would
        // be lost.
        ObserverEndpoint withSpaces = ObserverEndpoint.Remote(
            new Uri("https://laptop:5058/"), "token", "machines.json", new string('a', 64), " laptop ");

        Assert.Equal(withSpaces, Preferences.RememberedMachine([local, withSpaces], "laptop"));
    }

    [Fact]
    public void AMissingOrBlankNameFallsBackToThisComputer()
    {
        // The entry may have been removed or renamed: it starts again from this computer,
        // which is where it started from the first time. Opening on nothing, or complaining
        // about a preference, would be worse than forgetting it.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint remote = RemoteEndpoint("laptop");

        Assert.Equal(local, Preferences.RememberedMachine([local, remote], "vanished"));
        Assert.Equal(local, Preferences.RememberedMachine([local, remote], null));
        Assert.Equal(local, Preferences.RememberedMachine([local, remote], "   "));
    }

    [Fact]
    public void NameComparisonIsCaseSensitive()
    {
        // On Linux the credential for "Laptop" and the one for "laptop" are two different
        // files, so they are two different MACHINES: treating them as the same name would
        // reopen the other one.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        ObserverEndpoint remote = RemoteEndpoint("laptop");

        Assert.Equal(local, Preferences.RememberedMachine([local, remote], "Laptop"));
    }

    [Fact]
    public void WithNoMachinesNothingIsRemembered() =>
        Assert.Null(Preferences.RememberedMachine([], "laptop"));

    // The NAME is the fifth parameter: the third is the origin, that is where the
    // configuration comes from. Passing the name there leaves Name null, and that is exactly
    // the case of the old single-machine configuration: an entry with no name is not
    // remembered.
    private static ObserverEndpoint RemoteEndpoint(string name) =>
        ObserverEndpoint.Remote(
            new Uri($"https://{name}:5058/"),
            "token",
            "machines.json",
            new string('a', 64),
            name);

    [Theory]
    [InlineData("""{}""", "system")]
    [InlineData("""{"theme": null}""", "system")]
    [InlineData("""{"theme": "black"}""", "system")]
    [InlineData("""{"theme": "dark"}""", "dark")]
    [InlineData("""{"theme": "Dark"}""", "dark")]
    [InlineData("""{"theme": "light"}""", "light")]
    public void AnUnknownThemeFallsBackToSystemAndCaseIsIgnored(string json, string expected)
    {
        // An old file has no field, a hand-written one can hold anything: none of this must
        // stop the window, and capitals are forgiven.
        Assert.Equal(expected, Preferences.From(json).Theme);
        Assert.Equal("system", Preferences.Defaults.Theme);
    }

    [Fact]
    public void APlacementInsideTheScreenIsKept()
    {
        WindowPlacement placement = new(192, 100, 900, 700, Maximized: false);

        Assert.Equal(placement, placement.WithinAnyOf([PrimaryScreen]));
    }

    [Fact]
    public void APlacementOnTheSecondScreenIsKeptWhileThatScreenExists()
    {
        // The real case: laptop with an external monitor, window left on the monitor, and the
        // next day the monitor is not there. With both screens the placement holds; with the
        // laptop's screen alone it does not.
        WindowPlacement onSecondScreen = new(2400, 200, 900, 700, Maximized: false);

        Assert.Equal(onSecondScreen, onSecondScreen.WithinAnyOf([PrimaryScreen, RightScreen]));
        Assert.Null(onSecondScreen.WithinAnyOf([PrimaryScreen]));
    }

    [Theory]
    [InlineData(1850, 100)]
    [InlineData(100, 980)]
    [InlineData(-500, 100)]
    [InlineData(100, -500)]
    public void APlacementThatLeavesTheGrabbableCornerOffScreenIsForgotten(int x, int y)
    {
        // One pixel inside is not enough: the corner with the title bar has to fit, or the
        // window can be seen but cannot be moved.
        WindowPlacement placement = new(x, y, 900, 700, Maximized: false);

        Assert.Null(placement.WithinAnyOf([PrimaryScreen]));
    }

    [Fact]
    public void AWindowTooSmallToBeRealIsForgotten() =>
        Assert.Null(new WindowPlacement(10, 10, 40, 40, Maximized: false).WithinAnyOf([PrimaryScreen]));

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1800, 920, true)]
    [InlineData(1801, 920, false)]
    [InlineData(1800, 921, false)]
    [InlineData(-8, 0, true)]
    [InlineData(-16, 0, true)]
    [InlineData(-17, 0, false)]
    [InlineData(0, -1, false)]
    public void TheGrabbableCornerCheckIsExactAndToleratesTheInvisibleLeftBorder(int x, int y, bool isKept)
    {
        // The exact edges, or a <= turning into a < would go unnoticed. On the left there is
        // a tolerance: a window snapped to the edge sits at X = -8 because of Windows'
        // invisible border, and it has to be remembered. At the top it does not: (-8, -8) is
        // a maximized window, and that is not a normal geometry.
        WindowPlacement placement = new(x, y, 900, 700, Maximized: false);

        Assert.Equal(isKept ? placement : null, placement.WithinAnyOf([PrimaryScreen]));
    }

    [Fact]
    public void AWindowExactlyTheMinimumGrabbableSizeIsKept()
    {
        Assert.NotNull(new WindowPlacement(10, 10, 120, 120, Maximized: false).WithinAnyOf([PrimaryScreen]));
        Assert.Null(new WindowPlacement(10, 10, 119, 120, Maximized: false).WithinAnyOf([PrimaryScreen]));
        Assert.Null(new WindowPlacement(10, 10, 120, 119, Maximized: false).WithinAnyOf([PrimaryScreen]));
    }

    [Theory]
    [InlineData(int.MaxValue, 100)]
    [InlineData(int.MaxValue - 100, 100)]
    [InlineData(100, int.MaxValue)]
    [InlineData(100, int.MaxValue - 100)]
    [InlineData(int.MinValue, 100)]
    public void AnAbsurdCoordinateDoesNotOverflow(int x, int y)
    {
        // A hand-written file with x = 2147483647: the old X + 120 sum overflowed into the
        // negatives, the comparison passed and the window opened invisible - for ever,
        // because on close it saved itself identical. A hundred below the maximum too: there
        // the first clause does not overflow yet, and only the second is left defending.
        Assert.Null(new WindowPlacement(x, y, 900, 700, Maximized: false).WithinAnyOf([PrimaryScreen, RightScreen]));
    }

    [Fact]
    public void ClosedWhileNormalItRemembersWhereItWas()
    {
        WindowPlacement now = new(300, 200, 900, 700, Maximized: false);

        Assert.Equal(now, WindowPlacement.AtClose(
            minimized: false, maximized: false, lastNormal: null, saved: null, current: now));
    }

    [Fact]
    public void ClosedWhileMaximizedItRemembersThisSessionsNormalGeometry()
    {
        // The real defect: yesterday's window was on monitor A, today you move it to B and
        // maximize it. On close it must remember B - today's last normal geometry - and not
        // A, which is what the file said this morning.
        WindowPlacement yesterday = new(100, 100, 900, 700, Maximized: false);
        WindowPlacement today = new(2400, 200, 900, 700, Maximized: false);
        WindowPlacement fullScreen = new(-8, -8, 1936, 1056, Maximized: false);

        Assert.Equal(today with { Maximized = true }, WindowPlacement.AtClose(
            minimized: false, maximized: true, lastNormal: today, saved: yesterday, current: fullScreen));

        // With no geometry from today, yesterday's holds.
        Assert.Equal(yesterday with { Maximized = true }, WindowPlacement.AtClose(
            minimized: false, maximized: true, lastNormal: null, saved: yesterday, current: fullScreen));
    }

    [Fact]
    public void MaximizedWithNoKnownGeometryOnlyTheStateIsRemembered()
    {
        // First start, maximized straight away, closed: the full-screen measurements must NOT
        // be saved as if they were a normal window. The state stays, with a placement no
        // screen accepts: the window reopens where the system decides, but full. The geometry
        // is X11's, (0, 0): on Windows it sits at (-8, -8) and would be discarded anyway, and
        // a test using that one could not tell the rule apart from luck.
        WindowPlacement fullScreen = new(0, 0, 1920, 1040, Maximized: false);

        WindowPlacement? remembered = WindowPlacement.AtClose(
            minimized: false, maximized: true, lastNormal: null, saved: null, current: fullScreen);

        Assert.NotNull(remembered);
        Assert.True(remembered.Maximized);
        Assert.Null(remembered.WithinAnyOf([PrimaryScreen, RightScreen]));
    }

    [Fact]
    public void MinimizedItRemembersHowItWasBefore()
    {
        WindowPlacement normal = new(300, 200, 900, 700, Maximized: false);
        WindowPlacement offScreen = new(-32000, -32000, 900, 700, Maximized: false);

        // It was normal before: it is remembered normal, even if the file said maximized.
        Assert.Equal(normal, WindowPlacement.AtClose(
            minimized: true, maximized: false, lastNormal: normal,
            saved: normal with { Maximized = true }, current: offScreen));

        // It was maximized before: it is remembered that way.
        Assert.Equal(normal with { Maximized = true }, WindowPlacement.AtClose(
            minimized: true, maximized: true, lastNormal: normal, saved: null, current: offScreen));

        // Nothing is known: nothing to say, and above all NOT the off-screen placement.
        Assert.Null(WindowPlacement.AtClose(
            minimized: true, maximized: false, lastNormal: null, saved: null, current: offScreen));
    }

    [Fact]
    public void TheZoomOptionReadsAsAPercentage()
    {
        // This is what a screen reader announces: "115 %", not "1,15".
        string text = new ZoomOption(1.15d).ToString();

        Assert.Contains("115", text, StringComparison.Ordinal);
        Assert.Contains("%", text, StringComparison.Ordinal);

        // The bare double starts with a 0 in every culture; the P0 of 0.75 contains none in
        // any of .NET's 889 cultures. Not StartsWith("75"): in Turkish it is "%75".
        Assert.DoesNotContain("0", new ZoomOption(0.75d).ToString(), StringComparison.Ordinal);
        Assert.Equal(new ZoomOption(1.15d), new ZoomOption(1.15d));
    }

    [Fact]
    public void TheAllowedZoomLevelsAreExactlyTheseSix()
    {
        Assert.Equal(1.0d, Preferences.NormalZoom);

        // The exact list and not a rule (ascending order, floor): with the rule alone,
        // removing 0.85 or 1.5 failed nothing, measured with mutants. The floor is 0.75 and
        // goes no lower: it is the zoom at which a 32 px Fluent control is still 24 px, and
        // the status ring keeps its hole (measured on real captures).
        Assert.Equal([0.75d, 0.85d, 1.0d, 1.15d, 1.3d, 1.5d], Preferences.AllowedZoomLevels);
    }

    [Fact]
    public void TheAllowedPeriodsAreExactlyTheseThree()
    {
        // The exact list, for the same reason as the zoom levels: the real constraint - the
        // strip holds between 60 and 96 bars - is pinned by another test, but with that one
        // ALONE "24h" could be removed without anything turning red. And the first is the
        // default, so the order matters: a file with no field opens on the hour, not on a week.
        Assert.Equal(["1h", "24h", "7d"], Preferences.AllowedPeriods);

        // 90 days is NOT there although the service keeps them: it would be twenty-five
        // thousand points, over a response's cap, and at the width needed one bar would stand
        // for a day and a half. A chart that lies is worse than a chart that is missing.
        Assert.DoesNotContain("90d", Preferences.AllowedPeriods);
    }

    [Fact]
    public void TheSavedPeriodPreferenceReachesTheSelector()
    {
        // The bridge between the file and the drop-down: the window assigns HistoryPeriod, and
        // out of that must come the right selected entry, title and step. They were three
        // properties with not one test, and a mutation in the middle (SelectedHistoryPeriod
        // always falling back to the first entry) left the suite green with the window stuck
        // on one hour.
        MainViewModel viewModel = new(client: null, configurationProblem: null)
        {
            HistoryPeriod = "7d",
        };

        Assert.Equal("7d", viewModel.SelectedHistoryPeriod.Key);
        Assert.Contains(viewModel.SelectedHistoryPeriod, MainViewModel.HistoryPeriodOptions);
        Assert.Equal(TimeSpan.FromHours(2), viewModel.SelectedHistoryPeriod.Step);

        // And the drop-down shows HistoryPeriodOptions, not AllowedPeriods: an entry lost
        // between the two lists would be a period that gets saved and cannot be chosen.
        Assert.Equal(Preferences.AllowedPeriods, MainViewModel.HistoryPeriodOptions.Select(option => option.Key));

        // A made-up period in the file does not leave the window stuck on an empty drop-down.
        viewModel.HistoryPeriod = "90d";

        Assert.Equal(Preferences.AllowedPeriods[0], viewModel.SelectedHistoryPeriod.Key);
    }
}