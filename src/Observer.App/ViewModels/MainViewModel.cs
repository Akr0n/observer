using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Observer.App.Services;
using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;

namespace Observer.App.ViewModels;

/// <summary>
/// The only screen: polls the service once a second and shows what it answers.
/// </summary>
/// <remarks>
/// Non-negotiable rule for this class: it NEVER leaves the window empty and never lets an
/// exception escape. Whoever uses this application does not read logs, so every fault has to
/// become a plain English sentence inside the status bar.
/// </remarks>
public sealed partial class MainViewModel : ViewModelBase
{
    /// <summary>How often the service is polled.</summary>
    /// <remarks>
    /// Public so a test can compare it with <see cref="Controls.Gauge.NeedleTravelTime"/>: the
    /// needle's travel must stay shorter than this, otherwise it would never finish and the
    /// gauge would not sit still on a measured value even for an instant.
    /// </remarks>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>How often the service is polled when the window is minimized.</summary>
    /// <remarks>
    /// It does not stop: when the window comes back the status bar has to say straight away
    /// how it went, not "connecting". But one sample a second for a window nobody is looking
    /// at is work done on the machine being measured, and this is a tool whose own cost is
    /// part of the number it shows.
    /// </remarks>
    public static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(10);

    /// <summary>How often a failed history read is retried.</summary>
    /// <remarks>
    /// Not the period's step: at seven days that is two hours, and a timeout would leave a "No
    /// history" next to the strip that is two hours old, on data that has meanwhile come back.
    /// It retries soon, and slows down only once a read has gone well.
    /// </remarks>
    private static readonly TimeSpan HistoryRetryInterval = TimeSpan.FromSeconds(15);

    /// <summary>How many rereads a step is split into, when the read went well.</summary>
    /// <remarks>
    /// Rereading EVERY step looked like the right cadence - more often does not add a bar, it
    /// only adds traffic - and it was not: the last bar of the strip is the interval IN
    /// PROGRESS, and since 0.18.0 it is drawn as wide as the part it has covered. Rereading
    /// every step means looking at a newborn bar every time, always at the same fraction: at
    /// seven days the right edge - the one the eye reads as "now" - would stay a one-pixel
    /// sliver for the whole session, next to live gauges. A quarter of the step makes it grow
    /// in four jumps, and is still a thirtieth of the one-hour view's traffic.
    /// </remarks>
    private const int RereadsPerStep = 4;

    /// <summary>The minimum between two history rereads, whatever the period.</summary>
    /// <remarks>
    /// It only touches the one-hour view, whose step is already a minute: there the in-progress
    /// bar stays frozen at the fraction it had when the period was chosen, and that is accepted.
    /// It could go down to fifteen seconds, but that is twelve requests every fifteen seconds -
    /// one and a half times the sampling itself - to animate a thirteen-pixel bar. The price is
    /// paid by the machine this window is measuring, and it shows up in the number the window
    /// displays. On the long periods a quarter of the step costs far less than that and the
    /// defect is far bigger: that is where it is worth spending.
    /// </remarks>
    private static readonly TimeSpan MinimumRereadInterval = TimeSpan.FromMinutes(1);

    /// <summary>How far back the raw data is reread from at a minimum, whatever the period.</summary>
    /// <remarks>
    /// Aggregate consolidation has a four-minute grace: the one-minute level is five or six
    /// minutes behind now. Without this second read the last bars would ALWAYS be empty, and
    /// the strip would say "not measured" exactly at now, while the gauge above shows a live
    /// value. With a five-minute source the lag grows, and the tail widens with it: see TailFor.
    /// </remarks>
    private static readonly TimeSpan MinimumTail = TimeSpan.FromMinutes(10);

    /// <summary>How many rows to ask the process panel for.</summary>
    /// <remarks>
    /// Fifteen, not all of them: the question the panel answers is "who is eating my machine",
    /// and the tail of the list - hundreds of idle processes - answers nothing and costs
    /// bandwidth every second.
    /// </remarks>
    private const int ProcessRowCount = 15;

    private readonly Func<IMetricsClient?>? rereadConfiguration;
    private readonly Func<DateTimeOffset> clock;

    /// <summary>How to open a client to a machine picked from the list.</summary>
    private readonly Func<ObserverEndpoint, IMetricsClient>? openMachine;

    /// <summary>How to reread a machine's entry from disk, when its credential is no longer valid.</summary>
    private readonly Func<ObserverEndpoint, ObserverEndpoint?>? rereadEndpoint;

    private readonly Func<string, Task>? copyToClipboard;

    /// <summary>The last clipboard write, so the next one can be queued behind it.</summary>
    private Task clipboardQueue = Task.CompletedTask;

    /// <summary>The list entry the main loop is really reading.</summary>
    /// <remarks>
    /// NOT the list selection: that one can become null (a Ctrl+click on the highlighted entry
    /// deselects it) while the loop keeps reading the same machine, and then the probe would
    /// poll it a second time and its dot would stop following the bar. It is this entry that
    /// the probes skip and that the bar updates.
    /// </remarks>
    private MachineRow? watchedEntry;


    private IMetricsClient? client;

    private MetricCatalog catalog = MetricCatalog.Empty;
    private bool catalogLoaded;

    /// <summary>
    /// Since when readings have been failing in a row, or null if the last one went well.
    /// </summary>
    /// <remarks>
    /// This is what tells a service that is starting up from a service that is not there. It
    /// has to be cleared when the endpoint changes too: a different machine deserves a fresh
    /// grace period, not the one already used up by the previous one.
    /// </remarks>
    private DateTimeOffset? faultSince;

    /// <summary>
    /// Builds the screen.
    /// </summary>
    /// <param name="client">The client to the service, or null when the configuration is missing.</param>
    /// <param name="configurationProblem">
    /// The sentence to show when <paramref name="client"/> is null.
    /// </param>
    /// <param name="rereadConfiguration">
    /// How to retry reading the configuration while the application is open, or null not to
    /// retry at all. Returns a client once the configuration becomes valid.
    /// </param>
    /// <param name="clock">
    /// Where the time is read from, or null for the system clock. It is there for the tests:
    /// the wait before declaring a service faulted lasts ten seconds, and a test that really
    /// waited them out would be a test nobody runs willingly.
    /// </param>
    /// <param name="machineList">
    /// The machines to put in the sidebar, or null not to show it at all.
    /// </param>
    /// <param name="openMachine">How to open a client to a machine from the list.</param>
    /// <param name="rereadEndpoint">
    /// How to reread from disk the entry of a machine that is not being watched when a probe
    /// comes back with a rejected token or a fingerprint that does not match, or null not to
    /// reread.
    /// </param>
    /// <param name="copyToClipboard">
    /// How to write to the clipboard, or null: without it the copy commands stay disabled.
    /// </param>
    public MainViewModel(
        IMetricsClient? client,
        string? configurationProblem,
        Func<IMetricsClient?>? rereadConfiguration = null,
        Func<DateTimeOffset>? clock = null,
        MachineListResult? machineList = null,
        Func<ObserverEndpoint, IMetricsClient>? openMachine = null,
        Func<ObserverEndpoint, ObserverEndpoint?>? rereadEndpoint = null,
        Func<string, Task>? copyToClipboard = null)
    {
        this.copyToClipboard = copyToClipboard;
        this.client = client;
        this.rereadConfiguration = rereadConfiguration;
        this.openMachine = openMachine;
        this.rereadEndpoint = rereadEndpoint;
        this.clock = clock ?? (static () => DateTimeOffset.UtcNow);

        foreach (ObserverEndpoint endpoint in machineList?.Machines ?? [])
        {
            Machines.Add(new MachineRow(endpoint));
        }

        foreach (string problem in machineList?.Problems ?? [])
        {
            MachineListProblems.Add(problem);
        }

        // The initial selection follows the client the window was built with. No guard against
        // its own write is needed: the handler below returns on its own when the machine picked
        // is already the one that is open.
        SelectedMachine = Machines.FirstOrDefault(
            machine => client is not null && machine.Endpoint == client.Endpoint) ?? Machines.FirstOrDefault();

        // Just the application name. WHICH machine is being watched is already said by the
        // line under the title and by the highlighted entry in the sidebar: repeating it in
        // the big title is noise that gets read at every glance. The version lives in the
        // window's title bar, not here: you look it up when you need it, you do not reread it.
        Heading = "Observer";

        if (client is null)
        {
            ShowStatus(
                FAInfoBarSeverity.Error,
                "Configuration missing",
                configurationProblem ?? "The configuration could not be read.");
            Subheading = "Not connected.";
        }
        else
        {
            ShowStatus(FAInfoBarSeverity.Informational, "Connecting", "Taking the first reading…");
            Subheading = "Connecting…";
        }
    }

    /// <summary>The window title: the program's name and version.</summary>
    /// <remarks>
    /// Constant for the whole life of the window, so it is not observable. The version is the
    /// one in the binary's metadata, that is from <c>Directory.Build.props</c>, without the hash.
    /// </remarks>
    public string WindowTitle { get; } = Title(AppVersion.OfThisProgram());

    /// <summary>The big title at the top of the window.</summary>
    [ObservableProperty]
    public partial string Heading { get; set; }

    /// <summary>Composes the window title from the version.</summary>
    /// <param name="version">The short version, or empty when there is none.</param>
    /// <returns><c>Observer 0.8.0</c>, or just <c>Observer</c> when the version is missing.</returns>
    public static string Title(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return version.Length == 0 ? "Observer" : "Observer " + version;
    }

    /// <summary>The line under the title: connection status and the time of the last reading.</summary>
    [ObservableProperty]
    public partial string Subheading { get; set; }

    /// <summary>The status bar's title.</summary>
    [ObservableProperty]
    public partial string StatusTitle { get; set; } = string.Empty;

    /// <summary>The status bar's text.</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>The status bar's severity.</summary>
    [ObservableProperty]
    public partial FAInfoBarSeverity StatusSeverity { get; set; } = FAInfoBarSeverity.Informational;

    /// <summary>True when there is something to report. When all is well, the bar disappears.</summary>
    [ObservableProperty]
    public partial bool IsStatusVisible { get; set; } = true;

    /// <summary>The panels, one per metric source.</summary>
    public ObservableCollection<MetricGroup> Groups { get; } = [];

    /// <summary>The gauges, gathered at the top from every source.</summary>
    /// <remarks>
    /// It holds the SAME instances that live inside the groups, not copies: the rows update in
    /// place once a second, and two copies would diverge with nothing to flag it. They are
    /// gathered here only to show them together.
    /// </remarks>
    public ObservableCollection<MetricRow> Gauges { get; } = [];

    private DateTimeOffset nextHistoryRead = DateTimeOffset.MinValue;

    /// <summary>True when there is at least one gauge to show.</summary>
    /// <remarks>
    /// Without it, an empty panel with its title would stay on screen when no metric is
    /// measurable - which is exactly the moment when it must not look as if all is well.
    /// </remarks>
    [ObservableProperty]
    public partial bool HasGauges { get; set; }

    /// <summary>The processes shown in the panel, while it is open.</summary>
    public ObservableCollection<ProcessRowState> Processes { get; } = [];

    /// <summary>True while the process panel is open.</summary>
    [ObservableProperty]
    public partial bool IsProcessPanelOpen { get; set; }

    /// <summary>The panel's title: it says which resource the processes are being watched for.</summary>
    [ObservableProperty]
    public partial string ProcessesTitle { get; set; } = string.Empty;

    /// <summary>What is wrong in the panel, when something is wrong. Empty otherwise.</summary>
    [ObservableProperty]
    public partial string ProcessesProblem { get; set; } = string.Empty;

    /// <summary>The selected row, the one the button would end.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopyRow))]
    [NotifyCanExecuteChangedFor(nameof(CopyProcessRowCommand))]
    public partial ProcessRowState? SelectedProcess { get; set; }

    /// <summary>True when there is a selected row that can be ended.</summary>
    [ObservableProperty]
    public partial bool CanEndProcess { get; set; }

    /// <summary>The name of the machine to reopen next time, or null for this computer.</summary>
    /// <remarks>
    /// The RAW name from the endpoint, not <c>MachineRow.Name</c>: that one is the
    /// <i>visible</i> name, which falls back to the address when an entry has none — the case
    /// of the old single-machine configuration — and to the words "This machine" for the
    /// local channel. Neither of the two is a key: the first is an address that would end up
    /// in a file where it must not sit, the second matches nothing in
    /// <c>machines.json</c>.
    /// <para>
    /// It is the machine really BEING READ, not the selected one: the selection can be null
    /// while the loop keeps reading, and it is the same distinction <c>watchedEntry</c> exists
    /// for. It is not observable because the window reads it exactly once, on close.
    /// </para>
    /// </remarks>
    public string? MachineToRemember => watchedEntry?.Endpoint.Name?.Trim();

    /// <summary>True when the clipboard is reachable: without it the commands stay disabled.</summary>
    /// <remarks>
    /// The wiring to the clipboard comes from whoever builds the view model, and it is optional
    /// because a test with no window does not have one. If one day someone forgot it in the
    /// composition root, a command that simply returned on the null would leave a button that
    /// does nothing and does not say so — and the tests would stay green, because they do have
    /// the fake. A disabled button, by contrast, is noticed on the very first run.
    /// </remarks>
    public bool CanCopy => copyToClipboard is not null;

    /// <summary>True when there is a process row to copy.</summary>
    /// <remarks>
    /// On the SELECTION and not on <see cref="CanEndProcess"/>, even though today they
    /// coincide: copying a row is read-only, ending it is not, and hanging the first on the
    /// second's permission means that the day the gate on who may kill a process is tightened —
    /// a user without rights, a read-only machine — the ability to copy its name would
    /// disappear too, without anyone having decided that.
    /// </remarks>
    public bool CanCopyRow => CanCopy && SelectedProcess is not null;

    /// <summary>
    /// True when the end button has already been pressed once and is waiting for the
    /// confirmation.
    /// </summary>
    /// <remarks>
    /// The confirmation lives in the button and not in a dialog window, and that is not
    /// laziness: a modal window here would mean passing the parent window to the view model,
    /// that is tying the logic to the interface exactly where it is not tied today. Two clicks
    /// on the same button, with the text changing, guard against the same mistake — a careless
    /// click on the wrong row — without that dependency.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndButtonText))]
    public partial bool IsAwaitingEndConfirmation { get; set; }

    /// <summary>What the end button says right now.</summary>
    /// <remarks>
    /// ONE button whose label changes, and not two that alternate: with two, on the first click
    /// the pressed button disappeared and the keyboard focus fell into nothing, and whoever
    /// confirms with Enter found themselves pressing Enter on nothing.
    /// </remarks>
    public string EndButtonText => IsAwaitingEndConfirmation ? "Click again to end it" : "End process";

    /// <summary>
    /// True when the window is minimized: the reading cadence gets longer.
    /// </summary>
    /// <remarks>The window sets it; the view model does not know what a window is.</remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PollInterval))]
    public partial bool IsMinimized { get; set; }

    /// <summary>How often a reading is taken right now.</summary>
    public TimeSpan PollInterval => IsMinimized ? BackgroundInterval : Interval;

    /// <summary>How much the window is scaled: 1 is the normal size, below 1 is smaller.</summary>
    /// <remarks>
    /// Avalonia does not read the system text size, so whoever raised it in Windows does not
    /// find it again here. This is the internal setting that replaces it, and it goes below
    /// 100 % too, where Windows does not: it is a zoom, not just a text size. The window
    /// applies it, scaling everything - gauges included - and remembers it between starts.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedScale))]
    public partial double Zoom { get; set; } = Preferences.NormalZoom;

    /// <summary>The scales to choose from, as selector entries.</summary>
    public static IReadOnlyList<ZoomOption> ScaleOptions { get; } =
        [.. Preferences.AllowedZoomLevels.Select(factor => new ZoomOption(factor))];

    /// <summary>The scale as a selector entry: it is <see cref="Zoom"/> with a label.</summary>
    /// <remarks>
    /// The selector can assign null while its list changes: the scale then stays as it is.
    /// </remarks>
    public ZoomOption SelectedScale
    {
        get => new(Zoom);
        set => Zoom = value?.Factor ?? Zoom;
    }

    /// <summary>A scale that is not allowed does not get in: it is brought back to normal.</summary>
    /// <param name="value">The requested scale.</param>
    partial void OnZoomChanged(double value)
    {
        double validScale = Preferences.NormalizeZoom(value);

        if (validScale != value)
        {
            Zoom = validScale;
        }
    }

    /// <summary>The theme: <c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    /// <remarks>
    /// The application applies it, not this class, which does not know what a theme is: only
    /// the choice lives here, because that is what the dropdown shows and what gets remembered.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTheme))]
    public partial string Theme { get; set; } = Preferences.AllowedThemes[0];

    /// <summary>The themes to choose from, as selector entries.</summary>
    public static IReadOnlyList<ThemeOption> ThemeOptions { get; } =
        [.. Preferences.AllowedThemes.Select(key => new ThemeOption(key))];

    /// <summary>How much history the strip shows: <c>1h</c>, <c>24h</c> or <c>7d</c>.</summary>
    /// <remarks>
    /// The key and not the entry, for the same reason as the theme: it is what ends up in the file.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedHistoryPeriod))]
    public partial string HistoryPeriod { get; set; } = Preferences.AllowedPeriods[0];

    /// <summary>The periods to choose from, as selector entries.</summary>
    public static IReadOnlyList<HistoryPeriodOption> HistoryPeriodOptions { get; } =
        [.. Preferences.AllowedPeriods.Select(key => new HistoryPeriodOption(key))];

    /// <summary>The period as a selector entry.</summary>
    /// <remarks>
    /// The getter NEVER throws, like the theme's: a key that is not in the table falls back to
    /// the first entry instead of bringing the window down while it is drawing.
    /// </remarks>
    public HistoryPeriodOption SelectedHistoryPeriod
    {
        get => HistoryPeriodOptions.FirstOrDefault(option => option.Key == HistoryPeriod) ?? HistoryPeriodOptions[0];
        set => HistoryPeriod = value?.Key ?? HistoryPeriod;
    }

    /// <summary>The title above the strip: it says the period DRAWN, not the one chosen.</summary>
    /// <remarks>
    /// It is not computed from <see cref="HistoryPeriod"/>, and that is a decision. Computed, it
    /// changed with the selector - that is, instantly - while underneath the previous period's
    /// bars stayed until the new read landed: up to eight seconds on a slow machine, and
    /// FOREVER on a machine that does not answer, because there the history is not reread at
    /// all. The window called sixty one-minute bars "Last 7 days", and a machine idle for an
    /// hour read as idle for a week. Now whoever draws writes it, after the guard on late
    /// responses: the selector says what was asked for, the title says what is being watched,
    /// and when they diverge it is because they really do diverge.
    /// </remarks>
    [ObservableProperty]
    public partial string HistoryTitle { get; set; } = new HistoryPeriodOption(Preferences.AllowedPeriods[0]).Title;

    /// <summary>The theme as a selector entry: it is <see cref="Theme"/> with a label.</summary>
    /// <remarks>The selector can assign null while its list changes: the theme then stays as it is.</remarks>
    public ThemeOption SelectedTheme
    {
        get => new(Theme);
        set => Theme = value?.Key ?? Theme;
    }

    /// <summary>A theme that is not allowed does not get in: it goes back to the system one.</summary>
    /// <param name="value">The requested theme.</param>
    partial void OnThemeChanged(string value)
    {
        string validTheme = Preferences.NormalizeTheme(value);

        if (!string.Equals(validTheme, value, StringComparison.Ordinal))
        {
            Theme = validTheme;
        }
    }

    /// <summary>A period that is not allowed does not get in, and the new one is read at once.</summary>
    /// <param name="value">The requested period.</param>
    /// <remarks>
    /// At once and not on the next loop: whoever picks "7 days" is watching the strip, and
    /// waiting up to two hours to see it change would be indistinguishable from a selector that
    /// does not work. The deadline moves back instead of calling the read from here: this way
    /// the request starts from the loop, where the cancellation token and the guard on the
    /// outcome already are, and not from a setter the selector calls on the interface thread.
    /// </remarks>
    partial void OnHistoryPeriodChanged(string value)
    {
        string validPeriod = Preferences.NormalizePeriod(value);

        if (!string.Equals(validPeriod, value, StringComparison.Ordinal))
        {
            HistoryPeriod = validPeriod;

            return;
        }

        // As long as nothing is drawn the title follows the selector: there is no strip to
        // contradict, and at start-up with "7d" in the file saying "Last hour" over an empty
        // space would just be wrong. As soon as a read lands, the title goes back to saying
        // what can be seen.
        if (!Gauges.Any(row => row.ShowHistory))
        {
            HistoryTitle = SelectedHistoryPeriod.Title;
        }

        nextHistoryRead = DateTimeOffset.MinValue;

        // Changing the period changes the summary's QUESTION, not just its answer: "what did I
        // miss in the last hour" and "in the last seven days" are two different things. It
        // starts over, dismissal included - whoever closes a panel closes that one, not every
        // future panel.
        awaySummaryDismissed = false;
        AwaySummaryText = string.Empty;
        ShowAwaySummary = false;

        foreach (MachineRow machine in Machines)
        {
            machine.SummaryPeriodKey = null;
            machine.SummaryLine = string.Empty;
        }
    }

    /// <summary>What happened while the window was closed, one line per machine.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyAwaySummaryCommand))]
    public partial string AwaySummaryText { get; set; } = string.Empty;

    /// <summary>True when there is a summary to show and nobody has dismissed it yet.</summary>
    /// <remarks>
    /// Two-way: the bar has its own X, and closing it writes here. That is not a detail - the
    /// STATUS bar is deliberately not closable, because it says something that is going on;
    /// this one says something from the past, and a past fact is read once and filed away.
    /// </remarks>
    [ObservableProperty]
    public partial bool ShowAwaySummary { get; set; }

    /// <summary>True when the user has closed the panel for this period.</summary>
    private bool awaySummaryDismissed;

    partial void OnShowAwaySummaryChanged(bool value)
    {
        if (!value && AwaySummaryText.Length > 0)
        {
            awaySummaryDismissed = true;
        }
    }

    /// <summary>Which resource the panel is watching: <c>cpu</c>, <c>memory</c>, or null.</summary>
    private string? shownResource;

    /// <summary>The machines to choose from, each with its status. The first is always this one.</summary>
    public ObservableCollection<MachineRow> Machines { get; } = [];

    /// <summary>How often the machines that are NOT being watched get probed.</summary>
    /// <remarks>
    /// Fifteen seconds and not one: a dot next to the name has to say "it is alive", not track
    /// the CPU. And the probes are fired and not awaited: a machine that is off costs eight
    /// seconds of timeout, and the gauges' loop must not pay them.
    /// </remarks>
    public static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromSeconds(15);

    private DateTimeOffset nextProbe = DateTimeOffset.MinValue;

    /// <summary>The list entries that were rejected, and why.</summary>
    /// <remarks>
    /// Shown next to the list instead of hidden in a log: a badly configured machine that
    /// simply DOES NOT APPEAR is indistinguishable from a machine that was never added, and
    /// whoever looks for it has no way of knowing what to fix.
    /// </remarks>
    public ObservableCollection<string> MachineListProblems { get; } = [];

    /// <summary>
    /// True when the list holds only this machine and there is nothing to fix.
    /// </summary>
    /// <remarks>
    /// The sidebar is ALWAYS visible, and it was not always so: it appeared only once a second
    /// machine was already there. The result was that nobody could discover they could add
    /// one, because the only place the feature announces itself is the feature itself. A
    /// feature that shows itself only to whoever already knows it exists does not exist.
    /// <para>
    /// In its place, when there is a single machine, it explains how to add another one and
    /// gives the exact path of the file to write.
    /// </para>
    /// </remarks>
    public bool ShowMachineListHint => Machines.Count <= 1 && MachineListProblems.Count == 0;

    /// <summary>How to add a machine, with the path of the file to write.</summary>
    public string MachineListHint { get; } =
        "Only this machine so far. To watch another one, run \"observer share\" on it and add a " +
        "name, its address (https://HOST:5058/) and the fingerprint it prints to " +
        MachineDirectory.FilePath + ". Then keep that machine's token on this computer with " +
        "\"observer token set NAME\", using the same name, and reopen this window. The token " +
        "never goes in the file.";

    /// <summary>The machine currently being watched.</summary>
    [ObservableProperty]
    public partial MachineRow? SelectedMachine { get; set; }

    /// <summary>Switches machine without restarting the window.</summary>
    /// <param name="value">The machine picked from the list.</param>
    partial void OnSelectedMachineChanged(MachineRow? value)
    {
        if (value is null)
        {
            // The list should never get here (AlwaysSelected); if it does, the watched machine
            // stays the one from before and watchedEntry does not change.
            return;
        }

        watchedEntry = value;

        // The load is derived from the watched machine like the catalog and the gauges, and it
        // has to be thrown away HERE, before the early returns, because the invariant is tied
        // to watchedEntry and not to the client. Without this line the entry just clicked keeps
        // showing the numbers the probe had written into it up to fifteen seconds earlier:
        // about a second if the machine answers, but the whole eight of the request budget if
        // it does not - that is, exactly when it was clicked to find out what is happening to
        // it, under the highlighted name it still says it is working while the bar says
        // "Connecting".
        value.MachineLoad = MachineLoad.None;

        if (openMachine is null)
        {
            return;
        }

        if (client is not null && value.Endpoint == client.Endpoint)
        {
            return;
        }

        client = openMachine(value.Endpoint);

        // Everything that described the PREVIOUS machine has to be thrown away: the catalog,
        // because the labels belong to that service, and the panels, because they are its
        // measurements. The fault clock is INHERITED from the entry instead: if the probe has
        // already known for twenty seconds that this machine is off, the bar opens red at once
        // instead of acting out ten seconds of "Connecting" - the grace is for a service that
        // is starting up, not for one already measured as off. And so bar and dot share one clock.
        catalogLoaded = false;
        catalog = MetricCatalog.Empty;
        faultSince = value.FailingSince;
        Groups.Clear();

        // And the gauges, which are a SECOND collection over the same rows. Clearing only the
        // panels left the needles and the strips of the previous machine on screen, under the
        // new machine's name: real numbers, attributed to the wrong machine. It showed at a
        // glance precisely because half the window emptied and half did not. Whoever adds a
        // third derived collection adds it HERE.
        Gauges.Clear();
        HasGauges = false;

        // And the history deadline is derived from the previous machine too. The rows come back
        // with no strip and no note - neither bars nor the reason why they are missing -
        // and without this line they stay that way until the INHERITED deadline: half an hour
        // at seven days, almost four minutes at twenty-four hours, with the gauges above
        // already live.
        nextHistoryRead = DateTimeOffset.MinValue;

        ShowStatus(FAInfoBarSeverity.Informational, "Connecting", "Taking the first reading...");
        Subheading = "Connecting...";
    }

    /// <summary>
    /// The refresh loop. It never throws: any fault becomes text on screen.
    /// </summary>
    /// <param name="cancellationToken">Cancelled when the application closes.</param>
    /// <summary>
    /// Retries reading the configuration until it becomes valid.
    /// </summary>
    /// <returns>True if a client was adopted, false if there is no way to retry.</returns>
    private async Task<bool> WaitForConfigurationAsync(CancellationToken cancellationToken)
    {
        if (rereadConfiguration is null)
        {
            return false;
        }

        using PeriodicTimer timer = new(Interval);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await timer.WaitForNextTickAsync(cancellationToken))
            {
                return false;
            }

            if (rereadConfiguration() is not { } newClient)
            {
                continue;
            }

            client = newClient;
            faultSince = null;
            ShowStatus(FAInfoBarSeverity.Informational, "Connecting", "Taking the first reading…");
            Subheading = "Connecting…";
            return true;
        }

        return false;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (client is null)
        {
            // With no token there is nothing to poll: hammering the service with requests bound
            // for a 401 does not help. But the message on screen tells the user to create a
            // configuration file, and if creating it had no effect until a restart — which the
            // message does not say — the user would follow the instructions to the letter and
            // conclude the application is broken. So it rereads.
            if (!await WaitForConfigurationAsync(cancellationToken))
            {
                return;
            }
        }

        using PeriodicTimer timer = new(Interval);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ServiceOutcome fetch = await RefreshAsync(cancellationToken);

                // History after the sampling and only if the sampling went through: if the
                // machine does not answer, insisting on history would add waits to a window
                // that is already waiting, with nothing new to say.
                if (fetch == ServiceOutcome.Ok && clock() >= nextHistoryRead)
                {
                    // The read moves the deadline, not this line: it is the only one that knows
                    // whether it went well, badly, or whether the response arrived when it was
                    // no longer wanted. Moving it from here meant three wrong things at once -
                    // a timeout postponed by a whole step, that is two hours of "No history" on
                    // data that had already come back; a discarded response cancelled the reset
                    // the selector had just made, killing it for fifteen seconds; and the
                    // deadline was read from the period of NOW instead of the one asked for.
                    await RefreshHistoryAsync(cancellationToken);
                }

                // The processes follow the same loop as the gauges, but only while the panel is
                // open: asking for a list nobody is watching would cost one request a second
                // for nothing.
                if (fetch == ServiceOutcome.Ok && IsProcessPanelOpen)
                {
                    await RefreshProcessesAsync(cancellationToken);
                }

                // The other machines, for the dot next to the name. They are fired and not
                // awaited: see ProbeOtherMachines.
                if (clock() >= nextProbe)
                {
                    nextProbe = clock() + StatusRefreshInterval;
                    ProbeOtherMachines(cancellationToken);

                    // And, at the same pace, what happened while the window was closed. It sits
                    // in HERE and not outside for two reasons: a successful request is never
                    // repeated (the guard is SummaryPeriodKey), but a FAILED one is, and this
                    // is its cadence - the same one the probe retries the dot with. Outside the
                    // gate it would spin once a second to do nothing.
                    StartAwaySummaries(cancellationToken);
                }

                // A 401 on a window that is ALREADY connected almost always means the token was
                // rotated. Without rereading here, the window would stay stuck on "Token
                // rejected" until a restart: it is the same incident as "Configuration
                // missing", on another path, and it has to be closed the same way.
                // FingerprintMismatch too, and for the same reason: the message tells the user
                // to fix machines.json, and fixing it has to be ENOUGH. It is the third path
                // this incident shows up on - after "Configuration missing" and "Token
                // rejected" - and closing two out of three is worth nothing.
                if (fetch is ServiceOutcome.TokenRejected or ServiceOutcome.FingerprintMismatch)
                {
                    AdoptUpdatedConfiguration();
                }

                // The cadence follows the window: minimized, it reads every ten seconds.
                // Changing a PeriodicTimer's period takes effect from the next tick, which is
                // exactly what is needed: no timer to recreate, no loop lost.
                timer.Period = PollInterval;

                if (!await timer.WaitForNextTickAsync(cancellationToken))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The application is closing: a normal exit, not an error to show.
        }
#pragma warning disable CA1031 // This loop is started with nobody awaiting its outcome: an
        catch (Exception ex) // exception here would vanish in silence and the window would
#pragma warning restore CA1031 // freeze saying nothing. It has to be shown, not propagated.
        {
            ShowStatus(
                FAInfoBarSeverity.Error,
                "Updates stopped",
                $"Automatic refresh stopped after an unexpected error ({ex.GetType().Name}: " +
                $"{ex.Message}). The values on screen have stopped updating: close and reopen the application.");
            Subheading = "Refresh stopped.";
        }
    }

    /// <summary>
    /// Rereads the configuration and adopts the resulting client, if it changed.
    /// </summary>
    /// <remarks>
    /// It does not close the previous client: whoever built it keeps the reference and closes
    /// it on exit. Closing it here would pull it out from under a request still in flight.
    /// </remarks>
    private void AdoptUpdatedConfiguration()
    {
        if (rereadConfiguration?.Invoke() is not { } updatedClient || ReferenceEquals(updatedClient, client))
        {
            return;
        }

        client = updatedClient;
        watchedEntry?.Update(updatedClient.Endpoint);

        // A fresh wait: the endpoint changed, and the seconds already spent against the
        // previous one say nothing about this one. The dot has the same clock, but Update above
        // clears it, along with the text derived from it: from the outside nobody touches the
        // entry any more.
        faultSince = null;

        // The catalog belongs to the previous service: it has to be refreshed, otherwise the
        // labels would stay those of a different machine.
        catalogLoaded = false;
        catalog = MetricCatalog.Empty;
    }

    private async Task<ServiceOutcome> RefreshAsync(CancellationToken cancellationToken)
    {
        if (client is not { } activeClient)
        {
            return ServiceOutcome.Unknown;
        }

        // The sampling first and ONLY THEN the catalog. Verified experimentally: with the
        // service off, asking for the catalog first doubles the wait — two timeouts instead of
        // one — and the window keeps saying "connecting" for six seconds before admitting it
        // cannot connect.
        SnapshotFetch fetch = await activeClient.GetLatestAsync(cancellationToken);

        // Between the request leaving and its response arriving the user may have switched
        // machine in the sidebar. Applying the values that just arrived here would mean showing
        // the PREVIOUS machine's measurements under the new machine's name, and filling its
        // catalog with labels that are not its own.
        if (!ReferenceEquals(client, activeClient))
        {
            return ServiceOutcome.Unknown;
        }

        if (!fetch.IsOk)
        {
            ReportProblem(fetch.Outcome, fetch.Problem, activeClient.Endpoint);
            return fetch.Outcome;
        }

        // The catalog changes only when the service changes: it is read once, and retried on
        // the next loop if it fails. It has to be read BEFORE drawing, otherwise the first
        // frame would show "cpu.usage.total" instead of "CPU usage". If it never arrives, the
        // metrics stay visible with their raw identifier instead of disappearing.
        if (!catalogLoaded)
        {
            CatalogFetch catalogFetch = await activeClient.GetCatalogAsync(cancellationToken);

            if (catalogFetch.IsOk && ReferenceEquals(client, activeClient))
            {
                catalog = catalogFetch.Catalog!;
                catalogLoaded = true;
            }
        }

        // The same guard after the catalog too: it is a second await, and the user may have
        // switched machine right there. Without it, the dot of a machine never contacted turned
        // "Reachable" on the previous machine's reading.
        if (!ReferenceEquals(client, activeClient))
        {
            return ServiceOutcome.Unknown;
        }

        MachineSnapshot snapshot = fetch.Snapshot!;
        Apply(SnapshotProjection.Project(snapshot, catalog));

        IsStatusVisible = false;
        watchedEntry?.Record(ServiceOutcome.Ok, string.Empty, clock());

        // The run of faults is over: the next one starts from scratch, and is entitled to the
        // same grace this one had.
        faultSince = null;

        // Only WHEN. The where has not disappeared, it moved where it does not have to be
        // refreshed at every glance: the machine being watched is the one selected in the list
        // on the left, and this line changes once a second while that one never changes.
        string timeText = snapshot.CapturedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        Subheading = $"Last Reading: {timeText}";

        return ServiceOutcome.Ok;
    }

    /// <summary>
    /// Turns a failed reading into what is seen on screen.
    /// </summary>
    /// <remarks>
    /// The severity does NOT depend on the single attempt that went wrong but on how long the
    /// run lasts: <see cref="StatusEscalation"/> decides it, and that is where the tested table
    /// lives. Only measuring the elapsed time and mapping it to a colour is left here.
    /// </remarks>
    private void ReportProblem(ServiceOutcome outcome, string text, ObserverEndpoint endpoint)
    {
        DateTimeOffset now = clock();
        faultSince ??= now;

        StatusMessage message = StatusEscalation.MessageFor(
            outcome,
            text,
            now - faultSince.Value,
            endpoint,
            hasValuesOnScreen: Groups.Count > 0);

        ShowStatus(SeverityFor(message.Tone), message.Title, message.Text);
        Subheading = message.Subheading;

        // The watched machine's dot follows the status bar, with the same clock, so the two
        // never say different things.
        watchedEntry?.Record(outcome, text, now);
    }

    /// <summary>Polls the machines that are not being watched, all at once and without awaiting them.</summary>
    /// <param name="cancellationToken">Cancelled on close.</param>
    /// <remarks>
    /// The main loop does NOT await the probes: a machine that is off answers after eight
    /// seconds of timeout, and the watched machine's gauges must not stop for that. Each probe
    /// updates its own entry when it comes back, and while one is in flight another does not start.
    /// </remarks>
    /// <summary>What happened while nobody was watching, machine by machine.</summary>
    /// <remarks>
    /// <para>
    /// This is the answer this project can HONESTLY give to "tell me if a machine goes down
    /// while the window is closed". The real alert - a notification-area icon, or a system
    /// notification - cannot be delivered on this stack without being able to fail in SILENCE,
    /// which is the thing this program does not do: Avalonia's icon cannot be queried
    /// (<c>TrayIcon._impl</c> is internal and every call is <c>?.</c>), on Linux without a
    /// StatusNotifierItem host it never appears and logs nothing, and on Windows the return
    /// value of <c>Shell_NotifyIcon</c> is discarded. An alert that can fail to appear without
    /// saying so is worse than no alert - the same reason Ctrl+C was removed in 0.16.0.
    /// </para>
    /// <para>
    /// The data is already there, though, and not on this machine: the remote service keeps
    /// seven days of one-minute samples. So no running process is needed, no dependency and no
    /// autostart - it is asked for on the way back in. One request per machine per chosen
    /// period, not a periodic one: the guard is <c>SummaryPeriodKey</c>, and that is also what
    /// restarts the count when the period changes, because there the question changes.
    /// </para>
    /// <para>
    /// What it does NOT cover, and this should be said: it wakes nobody, and it says nothing
    /// about the machine that is still down right now - that machine holds that data, and it is
    /// not answering. For that one the red diamond and "for 3 min" remain, with the limit
    /// already declared on <c>FailingSince</c>.
    /// </para>
    /// </remarks>
    private void StartAwaySummaries(CancellationToken cancellationToken)
    {
        HistoryPeriodOption period = SelectedHistoryPeriod;

        foreach (MachineRow machine in Machines)
        {
            // Only the machines that answer: history cannot be asked of one that does not
            // answer, and that is exactly the one where it would help most. That one produces
            // no line, deliberately - the red diamond and "for 3 min" next to the name already
            // say it, and repeating it here would be the same thing written twice. The
            // "history could not be read" line is for the different case: the machine answers
            // and the history does not, which without a sentence would stay indistinguishable
            // from "all is well".
            if (machine.IsSummarizing
                || machine.Status != MachineStatus.Reachable
                || string.Equals(machine.SummaryPeriodKey, period.Key, StringComparison.Ordinal))
            {
                continue;
            }

            IMetricsClient? entryClient = ReferenceEquals(machine, watchedEntry) ? client : openMachine?.Invoke(machine.Endpoint);

            if (entryClient is null)
            {
                continue;
            }

            machine.IsSummarizing = true;
            _ = ReadAwaySummaryAsync(machine, entryClient, period, cancellationToken);
        }
    }

    /// <summary>Reads a machine's history and derives its summary line from it.</summary>
    private async Task ReadAwaySummaryAsync(
        MachineRow machine,
        IMetricsClient entryClient,
        HistoryPeriodOption period,
        CancellationToken cancellationToken)
    {
        try
        {
            // ONE series only, and a fixed one: the question is not "what was it measuring" but
            // "was it measuring", and any metric the service always samples answers that. Same
            // argument as MachineLoad, same constant shared from Observer.Core.
            // It asks for MORE than it examines, and that is not padding. The grid anchors to
            // the LAST point the machine sends, and that point is behind now by as long as
            // consolidation lasts: asking for exactly the window, the first buckets would fall
            // before the request's "from" and would be empty BY CONSTRUCTION, not because the
            // machine was off. Being contiguous with the start they would be read as an edge,
            // that is "nothing known before" on EVERY healthy machine at EVERY open - a bar
            // that always opens saying the same untrue thing is one you learn to close without
            // reading. The margin is TailFor, the number this project has already measured for
            // the same lag in the strip; the extra points fall outside the grid and Build
            // ignores them.
            HistoryFetch history = await entryClient.GetHistoryAsync(
                new HistoryQuery(
                    "cpu",
                    CpuCollector.TotalUsageMetricId,
                    null,
                    clock() - period.Duration - TailFor(period),
                    period.Resolution),
                cancellationToken).ConfigureAwait(true);

            // The period may have changed during the wait: that response answers a question
            // that is no longer the one on screen.
            if (SelectedHistoryPeriod != period)
            {
                return;
            }

            machine.SummaryLine = LineFor(machine, history, period);

            // The key is marked ONLY when the read really happened. Marking it on the fault too
            // would mean that a single timeout - eight seconds for the whole response, and at
            // seven days that is two thousand points - leaves "history could not be read" at
            // the top of the window for the whole session, while next to the name the machine
            // is green and the gauges refresh every second. Not marking it means it is retried
            // on the probes' loop, and the stale line replaces itself.
            if (history.Outcome == ServiceOutcome.Ok)
            {
                machine.SummaryPeriodKey = period.Key;
            }

            ComposeAwaySummary();
        }
        catch (OperationCanceledException)
        {
            // Closing: nothing to say.
        }
#pragma warning disable CA1031 // Like the probe: a summary that throws must bring nothing down.
        catch (Exception error)
#pragma warning restore CA1031
        {
            // As above: the key is not marked, so it is retried.
            machine.SummaryLine = $"{machine.Name}: history could not be read ({error.Message})";
            ComposeAwaySummary();
        }
        finally
        {
            machine.IsSummarizing = false;
        }
    }

    private static string LineFor(MachineRow machine, HistoryFetch history, HistoryPeriodOption period)
    {
        if (history.Outcome != ServiceOutcome.Ok || history.Points is null)
        {
            return $"{machine.Name}: history could not be read ({history.Problem})";
        }

        if (history.Points.Count == 0)
        {
            // Zero points is not "down the whole time": it can be a machine installed
            // yesterday, or persistence turned off. Saying it as it is costs one word and
            // invents nothing.
            return $"{machine.Name}: no history for this period";
        }

        return AwaySummary.LineFor(
            machine.Name,
            HistoryStrip.FindGaps(history.Points, period.Duration, period.SourceStep),

            // Same threshold as HistoryStrip.Describe: past a day the time of day alone no
            // longer places anything.
            period.Duration > TimeSpan.FromHours(24));
    }

    /// <summary>Puts the machines' lines together into one text.</summary>
    private void ComposeAwaySummary()
    {
        string text = string.Join(
            Environment.NewLine,
            Machines.Select(machine => machine.SummaryLine).Where(line => line.Length > 0));

        AwaySummaryText = text;

        // Closed by the user stays closed, until the period changes: one more line arriving ten
        // seconds later must not bring back a panel that was just dismissed.
        ShowAwaySummary = text.Length > 0 && !awaySummaryDismissed;
    }

    private void ProbeOtherMachines(CancellationToken cancellationToken)
    {
        if (openMachine is null)
        {
            return;
        }

        foreach (MachineRow machine in Machines)
        {
            // The entry the main loop REALLY reads is skipped, not the list selection: see
            // watchedEntry.
            if (machine.IsProbing || ReferenceEquals(machine, watchedEntry))
            {
                continue;
            }

            machine.IsProbing = true;
            _ = ProbeAsync(machine, cancellationToken);
        }
    }

    private async Task ProbeAsync(MachineRow machine, CancellationToken cancellationToken)
    {
        try
        {
            SnapshotFetch fetch = await openMachine!(machine.Endpoint).GetLatestAsync(cancellationToken);

            // The entry became the WATCHED one while the probe was in flight: nothing is
            // written, and the endpoint is not reread either. The probe starts by skipping the
            // watched entry but comes back up to eight seconds later, and one click is enough.
            // Writing would mean two readings of the same machine at different cadences, which
            // with the numbers next to the name contradict each other on sight. And rereading
            // would be worse than useless: Update swaps Endpoint without touching client, and
            // AdoptUpdatedConfiguration compares that very Endpoint with disk - finding it
            // already updated, it would never repair anything again, and the window would
            // stay on "Token rejected" after a successful "observer token set". The watched
            // entry is the main loop's business, and it holds both the endpoint and the client.
            if (ReferenceEquals(machine, watchedEntry))
            {
                return;
            }

            machine.Record(fetch.Outcome, fetch.Problem, clock(), fetch.Snapshot);

            // Token rejected or fingerprint that does not match: the entry has to be reread
            // from disk, as the main loop already does for the watched machine. Otherwise the
            // next probe starts again with the old credential and the dot stays red until a
            // restart, even after "observer token set".
            if (fetch.Outcome is ServiceOutcome.TokenRejected or ServiceOutcome.FingerprintMismatch
                && rereadEndpoint?.Invoke(machine.Endpoint) is { } refreshedEndpoint
                && refreshedEndpoint != machine.Endpoint)
            {
                machine.Update(refreshedEndpoint);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing: nothing to record.
        }
#pragma warning disable CA1031 // A probe that throws must bring nothing down: its outcome is a dot.
        catch (Exception error)
#pragma warning restore CA1031
        {
            // The same guard as the successful branch, and for the same reason.
            if (!ReferenceEquals(machine, watchedEntry))
            {
                machine.Record(ServiceOutcome.Unknown, error.Message, clock());
            }
        }
        finally
        {
            machine.IsProbing = false;
        }
    }

    private static FAInfoBarSeverity SeverityFor(StatusTone tone) => tone switch
    {
        StatusTone.Informational => FAInfoBarSeverity.Informational,
        StatusTone.Warning => FAInfoBarSeverity.Warning,
        _ => FAInfoBarSeverity.Error,
    };

    private void ShowStatus(FAInfoBarSeverity severity, string title, string message)
    {
        StatusSeverity = severity;
        StatusTitle = title;
        StatusText = message;
        IsStatusVisible = true;
    }

    private void Apply(IReadOnlyList<MetricGroupState> states)
    {
        if (!HasSameCollectors(states))
        {
            Groups.Clear();

            foreach (MetricGroupState state in states)
            {
                Groups.Add(new MetricGroup(state));
            }
        }

        else
        {
            for (int i = 0; i < states.Count; i++)
            {
                Groups[i].Update(states[i]);
            }
        }

        SyncGauges();
    }

    /// <summary>Rereads the history of every metric that has a gauge.</summary>
    /// <param name="cancellationToken">Cancelled on close.</param>
    /// <remarks>
    /// <para>
    /// It does not throw and it touches neither <c>faultSince</c> nor the status bar, on
    /// purpose: <b>a history fault is not a machine fault</b>. The service can answer the
    /// sampling perfectly well and have persistence turned off, and colouring the window red
    /// for that would teach people to ignore the real alarms too. The reason ends up next to
    /// the strip, where it belongs.
    /// </para>
    /// <para>
    /// This function owns <c>nextHistoryRead</c>, and there are THREE outcomes, not two: it
    /// went well, it went badly, and it arrived when it was no longer wanted. The third does
    /// not touch the deadline - whoever changed period or machine has just moved it back on
    /// purpose, and moving it here would mean leaving the old strip on screen under the new
    /// title for fifteen seconds, which from the outside is indistinguishable from a broken
    /// selector.
    /// </para>
    /// </remarks>
    private async Task RefreshHistoryAsync(CancellationToken cancellationToken)
    {
        if (client is not { } activeClient)
        {
            return;
        }

        DateTimeOffset now = clock();
        HistoryPeriodOption period = SelectedHistoryPeriod;

        // All the strips together, not one after the other: six gauges made twelve requests in
        // a row, and the loop's time was the SUM of the latencies. The requests start here, in
        // parallel; the rows are touched only afterwards, once they have all come back, and on
        // the interface thread.
        List<MetricRow> rows = [.. Gauges];

        (HistoryFetch Aggregate, HistoryFetch? Tail)[] readings = await Task.WhenAll(
            rows.Select(row => ReadHistoryAsync(activeClient, row.Key, period, now, cancellationToken)))
            .ConfigureAwait(true);

        // At seven days a read can last the whole eight-second budget, and in that time TWO
        // things can have changed: the chosen period and the watched machine. Writing these
        // bars now would mean drawing a week inside a one-hour strip, or the history of the
        // wrong machine.
        // Comparing the rows is not redundant next to comparing the client: App.Open keeps ONE
        // client per endpoint, so two machine switches in a row (A->B->A) hand back the exact
        // same object, while Gauges has been cleared twice and these rows are no longer on
        // screen. Writing into them would lose the read in silence.
        if (!ReferenceEquals(client, activeClient)
            || SelectedHistoryPeriod != period
            || !rows.SequenceEqual(Gauges))
        {
            return;
        }

        // Whoever draws writes the title, here and not in the selector: from this line on the
        // strip and the sentence above it talk about the same period.
        HistoryTitle = period.Title;

        // "Went well" means ALL of them, not at least one. With "at least one" five strips out
        // of six can spend two hours saying "No history" while the sixth refreshes, which is
        // the same defect as before cut by a sixth. Only the aggregate counts: the raw tail can
        // be missing without the strip suffering - the aggregate draws it anyway - and watching
        // it here would turn a harmless fault into a request every fifteen seconds for ever.
        // Zero gauges counts as not gone well: no request was fired, so retrying soon is free
        // and covers the gauges that show up later.
        bool succeeded = rows.Count > 0;

        for (int i = 0; i < rows.Count; i++)
        {
            ApplyHistory(rows[i], readings[i].Aggregate, readings[i].Tail, period, now);
            succeeded &= readings[i].Aggregate.Outcome == ServiceOutcome.Ok;
        }

        nextHistoryRead = clock() + HistoryReadDelay(period, succeeded);
    }

    /// <summary>How long until the history is reread, given the period and how it went.</summary>
    /// <param name="period">The period on show.</param>
    /// <param name="succeeded">True if every strip got its data.</param>
    /// <returns>How long to wait before the next read.</returns>
    /// <remarks>
    /// Pure and public because the two mistakes it has already made are visible nowhere but
    /// here: <b>postponing a fault by a whole step</b> - at seven days two hours of "No
    /// history" on data that came back a second later - and <b>rereading exactly every
    /// step</b>, which looks like the right cadence and is not, because it would look at a
    /// newborn bar every time and the strip's right edge would stay one pixel for ever.
    /// </remarks>
    public static TimeSpan HistoryReadDelay(HistoryPeriodOption period, bool succeeded)
    {
        ArgumentNullException.ThrowIfNull(period);

        if (!succeeded)
        {
            return HistoryRetryInterval;
        }

        TimeSpan readInterval = period.Step / RereadsPerStep;

        return readInterval > MinimumRereadInterval ? readInterval : MinimumRereadInterval;
    }

    /// <summary>How far back to read the raw data for the tail, given the source's step.</summary>
    /// <remarks>
    /// Three source points, never less than the minimum. With a one-minute source the usual ten
    /// minutes stand; at five minutes fifteen are needed, because that level's consolidation
    /// also waits for the level below it and stays behind for longer.
    /// </remarks>
    private static TimeSpan TailFor(HistoryPeriodOption period)
    {
        TimeSpan threeSteps = period.SourceStep * 3;

        return threeSteps > MinimumTail ? threeSteps : MinimumTail;
    }

    /// <summary>The two history reads of ONE metric: the one-minute aggregate and the raw tail.</summary>
    /// <returns>The tail is null when the aggregate is missing: without that one it is no use.</returns>
    private static async Task<(HistoryFetch Aggregate, HistoryFetch? Tail)> ReadHistoryAsync(
        IMetricsClient activeClient,
        string key,
        HistoryPeriodOption period,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string[] parts = key.Split('|');

        if (parts.Length < 2)
        {
            return (new HistoryFetch(ServiceOutcome.Unknown, "malformed metric key", null), null);
        }

        string? instance = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;

        HistoryFetch aggregate = await activeClient.GetHistoryAsync(
            new HistoryQuery(parts[0], parts[1], instance, now - period.Duration, period.Resolution),
            cancellationToken).ConfigureAwait(false);

        if (aggregate.Outcome != ServiceOutcome.Ok || aggregate.Points is null)
        {
            return (aggregate, null);
        }

        HistoryFetch tail = await activeClient.GetHistoryAsync(
            new HistoryQuery(parts[0], parts[1], instance, now - TailFor(period), "raw"),
            cancellationToken).ConfigureAwait(false);

        return (aggregate, tail);
    }

    private static void ApplyHistory(
        MetricRow row,
        HistoryFetch aggregate,
        HistoryFetch? tail,
        HistoryPeriodOption period,
        DateTimeOffset now)
    {
        if (aggregate.Outcome != ServiceOutcome.Ok || aggregate.Points is null)
        {
            row.History = null;
            row.HistoryNote = "No history: " + aggregate.Problem;

            return;
        }

        // The raw tail is bucketed at the SOURCE's step, not the bar's: that is what makes it
        // comparable with the aggregated points before merging them. Build applies the bar's
        // step, once and over everything.
        IReadOnlyList<HistoryPoint> points = tail is { Outcome: ServiceOutcome.Ok, Points: not null }
            ? HistoryStrip.Merge(aggregate.Points, HistoryStrip.Bucket(tail.Points, period.SourceStep))
            : aggregate.Points;

        row.HistoryNote = points.Count > 0
            ? string.Empty
            : "No history recorded for this metric yet.";

        row.History = HistoryStrip.Build(ToFractions(points), now, period.BarCount, period.Step);
    }

    /// <summary>Brings the history values into the gauges' 0..1 scale.</summary>
    /// <remarks>
    /// The history keeps the values as they were measured, so a percentage arrives from 0 to
    /// 100. It is the same division <c>MetricFormatting.Fraction</c> does for the row on
    /// screen: if the two diverged, gauge and strip would tell two different stories about the
    /// same metric.
    /// </remarks>
    private static IReadOnlyList<HistoryPoint> ToFractions(IReadOnlyList<HistoryPoint> points) =>
        [.. points.Select(point => point with
        {
            Avg = Math.Clamp(point.Avg / 100d, 0d, 1d),
            Min = Math.Clamp(point.Min / 100d, 0d, 1d),
            Max = Math.Clamp(point.Max / 100d, 0d, 1d),
            Last = Math.Clamp(point.Last / 100d, 0d, 1d),
        })];

    /// <summary>Rebuilds the list of gauges only when it really changes.</summary>
    /// <remarks>
    /// The comparison is by REFERENCE, and it has to stay that way: the rows are the same
    /// instances that live in the groups and update themselves, so clearing and refilling the
    /// collection on every loop would rebuild every gauge once a second, making the window
    /// flicker. It is rebuilt when a collector comes or goes, or when a metric stops being
    /// measurable and its gauge no longer makes sense.
    /// </remarks>
    private void SyncGauges()
    {
        List<MetricRow> expectedGauges =
            [.. Groups.SelectMany(group => group.Rows).Where(row => row.HasGauge)];

        HasGauges = expectedGauges.Count > 0;

        if (expectedGauges.Count == Gauges.Count
            && !expectedGauges.Where((row, i) => !ReferenceEquals(row, Gauges[i])).Any())
        {
            return;
        }

        Gauges.Clear();

        foreach (MetricRow row in expectedGauges)
        {
            Gauges.Add(row);
        }
    }

    /// <summary>Opens the process panel for the resource of the gauge that was clicked.</summary>
    /// <param name="row">The gauge that was clicked.</param>
    /// <returns>The wait for the first read.</returns>
    /// <remarks>
    /// Concurrent executions have to be ALLOWED: there is one command for all the gauges, and
    /// an asynchronous command refuses every other execution while it is running. Without
    /// this, while the first read is in flight to a slow remote machine, every other click
    /// — on another gauge, or on the same one to close it — would be discarded in silence, and
    /// the window would look unresponsive. <see cref="RefreshProcessesAsync"/> discards the
    /// response of a read that has since been superseded.
    /// </remarks>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenProcessesAsync(MetricRow? row)
    {
        if (row is null || ProcessResource.From(row.Key) is not { } resource)
        {
            return;
        }

        // The same gauge a second time CLOSES: it is the gesture anyone tries first to make
        // something they have just made appear go away. Another gauge switches the list
        // instead, without closing.
        if (IsProcessPanelOpen && string.Equals(shownResource, resource, StringComparison.Ordinal))
        {
            CloseProcessPanel();

            return;
        }

        shownResource = resource;
        // "Whole machine" is in the title because the gauge you arrive from is ONE disk's, and
        // the list is not: the I/O counters are per process, not per device.
        ProcessesTitle = resource switch
        {
            "memory" => "Top processes by memory",
            "io" => "Top processes by I/O (whole machine)",
            _ => "Top processes by CPU",
        };

        IsProcessPanelOpen = true;
        IsAwaitingEndConfirmation = false;
        ProcessesProblem = string.Empty;

        await RefreshProcessesAsync(CancellationToken.None);
    }

    /// <summary>Copies what the status bar says to the clipboard.</summary>
    /// <returns>The wait for the clipboard write.</returns>
    /// <remarks>
    /// This is the case that matters: a long error message — a fingerprint that does not match,
    /// with both fingerprints in full — otherwise has to be retyped by hand to paste it into a
    /// search. The title and the message on two lines, because they are two sentences.
    /// <para>
    /// <c>AllowConcurrentExecutions</c> is not decoration: an <c>AsyncRelayCommand</c> that is
    /// running disables itself and refuses every other call, so a second click while the
    /// clipboard is being written would fall into nothing with the button flashing disabled.
    /// It is the defect the six gauge buttons already paid for.
    /// </para>
    /// </remarks>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanCopy))]
    private Task CopyStatusAsync() =>
        WriteToClipboardAsync(StatusTitle + Environment.NewLine + StatusText);

    /// <summary>Copies the summary of what happened while nobody was watching to the clipboard.</summary>
    /// <returns>The wait for the clipboard write.</returns>
    /// <remarks>
    /// The case that matters is one line per machine with dates and durations: it is exactly
    /// the text you paste into a message to whoever runs that machine, and retyping it by hand
    /// from a panel is like retyping a fingerprint. Same queue as the other two Copy commands,
    /// same <c>AllowConcurrentExecutions</c>, same reason.
    /// </remarks>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanCopyAwaySummary))]
    private Task CopyAwaySummaryAsync() => WriteToClipboardAsync(AwaySummaryText);

    /// <summary>True when there is a summary to copy.</summary>
    private bool CanCopyAwaySummary() => copyToClipboard is not null && AwaySummaryText.Length > 0;

    /// <summary>Copies the selected process row to the clipboard, with its PID.</summary>
    /// <returns>The wait for the clipboard write.</returns>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanCopyRow))]
    private Task CopyProcessRowAsync() =>
        SelectedProcess is { } process ? WriteToClipboardAsync(process.ForClipboard) : Task.CompletedTask;

    /// <summary>Writes to the clipboard, and does not let a clipboard fault show up elsewhere.</summary>
    /// <param name="text">What goes into the clipboard.</param>
    /// <returns>The wait for the write.</returns>
    /// <remarks>
    /// A clipboard fault has nowhere to be reported: the only place would be the status bar, which
    /// is exactly what is being copied, and overwriting it would erase the message. Better to
    /// do nothing than to lose the text in order to report that copying it failed.
    /// </remarks>
    private Task WriteToClipboardAsync(string text)
    {
        if (copyToClipboard is not { } copy)
        {
            return Task.CompletedTask;
        }

        // QUEUED, one after the other. The Windows clipboard can be held by another program,
        // and in that case Avalonia retries ten times a hundred milliseconds apart: two writes
        // started close together have two independent retry loops, and the one that SUCCEEDS
        // last wins, not the one that was asked for last. Without the queue, a second click can
        // leave the first one's text in the clipboard — measured, and in silence. Everything
        // runs on the interface thread, so the queue needs no locks: chaining the Tasks is
        // enough.
        clipboardQueue = WriteQueuedAsync(clipboardQueue, copy, text);

        return clipboardQueue;
    }

    /// <summary>Waits for the previous write, then writes. It never throws.</summary>
    /// <param name="previousWrite">The write to wait for.</param>
    /// <param name="copy">How to write.</param>
    /// <param name="text">What to write.</param>
    /// <returns>The wait for its own write.</returns>
    /// <remarks>
    /// That it never throws is what makes it safe for the next call to await it: a failed write
    /// must not drag down the ones after it.
    /// </remarks>
    private static async Task WriteQueuedAsync(Task previousWrite, Func<string, Task> copy, string text)
    {
        await previousWrite;

        try
        {
            await copy(text);
        }
#pragma warning disable CA1031 // The clipboard can be held by another program: that is a
        catch (Exception) // failure of the system, not a fault of the dashboard.
#pragma warning restore CA1031
        {
            // Nothing. A clipboard fault has nowhere to be reported: the only place would be the
            // status bar, which is exactly what is being copied.
        }
    }

    /// <summary>Closes the panel and forgets what was in it.</summary>
    [RelayCommand]
    private void CloseProcessPanel()
    {
        IsProcessPanelOpen = false;
        shownResource = null;
        SelectedProcess = null;
        IsAwaitingEndConfirmation = false;
        ProcessesProblem = string.Empty;
        Processes.Clear();
    }

    /// <summary>Ends the selected process, asking for confirmation on the first click.</summary>
    /// <returns>The wait for the request and the reread.</returns>
    [RelayCommand]
    private async Task EndSelectedProcessAsync()
    {
        if (client is null || SelectedProcess is not { } process)
        {
            return;
        }

        // First click: it only arms. The button changes text, and whoever clicked by mistake
        // notices before anything happens.
        if (!IsAwaitingEndConfirmation)
        {
            IsAwaitingEndConfirmation = true;

            return;
        }

        IsAwaitingEndConfirmation = false;

        KillFetch fetch = await client.KillProcessAsync(process.Pid, CancellationToken.None);

        ProcessesProblem = fetch.Outcome == ServiceOutcome.Ok ? string.Empty : fetch.Problem;

        await RefreshProcessesAsync(CancellationToken.None);
    }

    /// <summary>Changing row disarms the confirmation.</summary>
    /// <param name="value">The row just selected.</param>
    /// <remarks>
    /// Without it, a confirmation armed on one process would stay armed after selecting another
    /// process, and the second click would end the wrong one.
    /// </remarks>
    partial void OnSelectedProcessChanged(ProcessRowState? value)
    {
        IsAwaitingEndConfirmation = false;
        CanEndProcess = value is not null;
    }

    private async Task RefreshProcessesAsync(CancellationToken cancellationToken)
    {
        if (client is null || shownResource is not { } resource)
        {
            return;
        }

        ProcessFetch fetch = await client.GetProcessesAsync(resource, ProcessRowCount, cancellationToken);

        // While the response was in flight the panel may have been closed, or moved to another
        // resource: that response then belongs to nobody. Applying it would fill a closed
        // panel, or put the CPU rows under the memory title.
        if (!IsProcessPanelOpen || !string.Equals(shownResource, resource, StringComparison.Ordinal))
        {
            return;
        }

        if (fetch.Outcome != ServiceOutcome.Ok)
        {
            ProcessesProblem = fetch.Problem;

            return;
        }

        ProcessesProblem = string.Empty;

        // The selection is kept on the PID and not on the object: the rows arrive new on every
        // loop, and without this the selection would be lost once a second — that is, exactly
        // while you are aiming at the process to end.
        int? selectedPid = SelectedProcess?.Pid;

        Processes.Clear();

        foreach (ProcessRowState row in fetch.Processes)
        {
            Processes.Add(row);
        }

        SelectedProcess = selectedPid is { } pid
            ? Processes.FirstOrDefault(row => row.Pid == pid)
            : null;
    }

    private bool HasSameCollectors(IReadOnlyList<MetricGroupState> states)
    {
        if (Groups.Count != states.Count)
        {
            return false;
        }

        for (int i = 0; i < states.Count; i++)
        {
            if (!string.Equals(Groups[i].CollectorId, states[i].CollectorId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}