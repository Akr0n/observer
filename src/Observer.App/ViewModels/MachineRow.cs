using CommunityToolkit.Mvvm.ComponentModel;
using Observer.App.Services;
using Observer.Core.Metrics;

namespace Observer.App.ViewModels;

/// <summary>How a machine in the list is doing, for the dot next to the name.</summary>
public enum MachineStatus
{
    /// <summary>It has not been probed yet. Grey.</summary>
    Unknown = 0,

    /// <summary>The last reading went through. Green.</summary>
    Reachable = 1,

    /// <summary>It has not answered for a short while, or it answers with a warning. Yellow.</summary>
    Warning = 2,

    /// <summary>A real fault, by the same rule as the status bar. Red.</summary>
    Faulted = 3,
}

/// <summary>
/// An entry in the sidebar: the machine, and how it is doing.
/// </summary>
/// <remarks>
/// Derives from <see cref="ObservableObject"/> and NOT from <see cref="ViewModelBase"/>, for the
/// same reason as <see cref="MetricRow"/>: ViewLocator picks up any ViewModelBase and
/// would draw a "Not Found" in place of the row.
/// <para>
/// The state follows the status bar's rule - <see cref="StatusEscalation"/>, with its
/// ten-second grace - so a red dot means the same thing as a red bar.
/// Before the sidebar with the dots, finding out how a machine was doing meant
/// clicking on it.
/// </para>
/// </remarks>
public sealed partial class MachineRow : ObservableObject
{
    /// <summary>Builds the entry, still without a state.</summary>
    /// <param name="endpoint">The machine.</param>
    public MachineRow(ObserverEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        Endpoint = endpoint;
    }

    /// <summary>The machine. Changes only through <see cref="Update"/>, on a rotated credential.</summary>
    public ObserverEndpoint Endpoint { get; private set; }

    /// <summary>Replaces the endpoint: same address, new credential.</summary>
    /// <param name="endpoint">The entry re-read from disk.</param>
    /// <remarks>
    /// Without it, a machine that is not being watched would be probed for ever with the token
    /// read at start-up, and after <c>observer token set</c> its dot would stay on "Token rejected"
    /// until a restart: the same incident already closed three times for the watched machine.
    /// </remarks>
    internal void Update(ObserverEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        Endpoint = endpoint;

        // The measurement starts over: from here on it is another machine, or the same one
        // reached another way, and "down for two days" said of the previous one would be a lie.
        // It lives HERE and not in the caller because there are two callers, and one of the two
        // would forget.
        FailingSince = null;
        DowntimeText = string.Empty;

        // And the load with them: it belonged to that other endpoint. A real number that refers
        // to a machine which is no longer this one reads as if it were this one's. The same holds
        // for the summary, which tells another machine's story: it must be redone, not adapted.
        MachineLoad = MachineLoad.None;
        SummaryLine = string.Empty;
        SummaryPeriodKey = null;

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(AccessibleName));
    }

    /// <summary>The name written in the list.</summary>
    public string Name => Endpoint.DisplayName;

    /// <summary>Since when the readings have been failing in a row, or null if the last one went through.</summary>
    /// <remarks>
    /// The same measurement the status bar keeps for the watched machine. The setter is
    /// private: the clock and the text derived from it have to move together, and from outside
    /// they are cleared only by changing the entry's machine, that is from <see cref="Update"/>.
    /// <para>
    /// It is the first failure THIS window has seen, not the instant the machine
    /// went down: a dashboard just opened on a machine that has been off for three days will say
    /// "under 1 min". The data to really know it is not there — the machine that should say so
    /// is precisely the one that is not answering. For the same reason it is calendar time and not
    /// observed time: across a PC suspend, or an interval in which the window
    /// was closed, the duration claims a continuity nobody was watching.
    /// </para>
    /// </remarks>
    internal DateTimeOffset? FailingSince { get; private set; }

    /// <summary>Whether this machine has ever answered with a reading, since the window opened.</summary>
    private bool hasMeasured;

    /// <summary>True while a probe is in flight: the next one does not start on top of it.</summary>
    /// <remarks>Readable from outside because a test observes it; only the view model writes it.</remarks>
    public bool IsProbing { get; internal set; }

    /// <summary>True while the history is read for the summary. Twin of <see cref="IsProbing"/>.</summary>
    public bool IsSummarizing { get; internal set; }

    /// <summary>For which period the summary was computed, or null if never.</summary>
    /// <remarks>
    /// The key and not the entry, as everywhere: it is what gets compared to know whether it must
    /// be redone. Changing period changes the question - "what did I miss in the last hour" is not
    /// "in the last seven days" - so the old answer no longer holds.
    /// </remarks>
    public string? SummaryPeriodKey { get; internal set; }

    /// <summary>What happened to this machine while nobody was watching. Empty if nothing.</summary>
    public string SummaryLine { get; internal set; } = string.Empty;

    /// <summary>How it is doing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnknown), nameof(IsReachable), nameof(IsWarning), nameof(IsFaulted), nameof(AccessibleName))]
    public partial MachineStatus Status { get; set; }

    /// <summary>Why it is that way, in one short sentence: the title the status bar would have.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(ToolTipText))]
    public partial string Detail { get; set; } = "Not checked yet";

    /// <summary>How long the fault has lasted, already written out: <c>for 2 h 10 min</c>. Empty if there is none.</summary>
    /// <remarks>
    /// A STORED property, written when a reading arrives, and not a getter that
    /// reads the clock: this way no timer is needed, and the row cannot change while
    /// nobody is watching. The price is that the text can lag behind until the next
    /// reading, and that is why <see cref="Downtime.Describe"/> truncates.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle), nameof(ToolTipText), nameof(AccessibleName))]
    public partial string DowntimeText { get; set; } = string.Empty;

    /// <summary>How hard this machine is working, when that is known.</summary>
    /// <remarks>
    /// <see cref="Record"/> writes it from the snapshot the probe already holds, and it stays
    /// <see cref="MachineLoad.None"/> for the WATCHED machine: there the numbers are in the gauges,
    /// big, two centimetres away, and repeating them small next to the name would mean
    /// two readings of the same machine that can contradict each other in plain sight - the probe
    /// runs every fifteen seconds, the main loop every second. The sidebar answers "do I have to
    /// switch machine?", and for the one you are already on the answer is already on screen.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle), nameof(ToolTipText), nameof(AccessibleName))]
    public partial MachineLoad MachineLoad { get; set; } = MachineLoad.None;

    /// <summary>The line under the name: either how long it has been down, or how hard it is working.</summary>
    /// <remarks>
    /// One line and not two, because the two contents exclude each other by construction: the load
    /// exists only when the reading went through, and <see cref="DowntimeText"/> is written only when
    /// it did NOT. The duration wins anyway, explicitly: if one day the two could
    /// coexist, "down for three minutes" is what has to be read.
    /// </remarks>
    public string Subtitle => DowntimeText.Length > 0 ? DowntimeText : MachineLoad.Caption;

    /// <summary>What the mouse tooltip says: the reason, and how long it has lasted.</summary>
    /// <remarks>
    /// Separated by a middle dot and not by a space: there is one prefix for ten
    /// different titles, and attached to some of them it changes the sense of the sentence. "Token
    /// rejected for 3 min" reads in English as "rejected FOR three minutes", that is a timed ban,
    /// which is the opposite of what is happening. The middle dot breaks the sentence and leaves
    /// two facts side by side, which is what they are.
    /// </remarks>
    /// <remarks>
    /// It says what the line says, not <see cref="DowntimeText"/>: this way a machine's load
    /// reaches whoever does not see the line as well. It does not change constantly, and that is
    /// no accident - the probe runs every fifteen seconds and the watched machine has no load, so
    /// the SELECTED entry, which is the only one a screen reader re-announces, has exactly the
    /// text it had before this addition.
    /// </remarks>
    public string ToolTipText => Subtitle.Length == 0 ? Detail : $"{Detail} · {Subtitle}";

    /// <summary>True until someone has probed it.</summary>
    public bool IsUnknown => Status == MachineStatus.Unknown;

    /// <summary>True when the last reading went through.</summary>
    public bool IsReachable => Status == MachineStatus.Reachable;

    /// <summary>True when there is a problem that might still clear by itself.</summary>
    public bool IsWarning => Status == MachineStatus.Warning;

    /// <summary>True on a real fault.</summary>
    public bool IsFaulted => Status == MachineStatus.Faulted;

    /// <summary>Name and state together, for whoever does not see the dot.</summary>
    /// <remarks>
    /// It goes through <see cref="ToolTipText"/> and not through <see cref="Detail"/>: this way the
    /// mouse tooltip and what a screen reader announces cannot diverge,
    /// and the duration is heard by whoever does not see the line too.
    /// </remarks>
    public string AccessibleName => $"{Name}: {ToolTipText}";

    /// <summary>Records the outcome of a reading, from the probe or from the main loop.</summary>
    /// <param name="outcome">How it went.</param>
    /// <param name="reason">The client's sentence, when it did not go through.</param>
    /// <param name="now">The time, to measure how long a fault has lasted.</param>
    /// <param name="snapshot">
    /// What the reading brought back, from which the load is derived. Null - and that is the
    /// default - for the WATCHED machine: see <see cref="MachineLoad"/>.
    /// </param>
    public void Record(
        ServiceOutcome outcome,
        string reason,
        DateTimeOffset now,
        MachineSnapshot? snapshot = null)
    {
        // It lives HERE and not in the callers for the same reason written in Update: there are
        // three callers, and one would forget to clear it - leaving the load it had the last
        // time it answered under the name of a machine that is not answering.
        MachineLoad = outcome == ServiceOutcome.Ok ? MachineLoad.From(snapshot) : MachineLoad.None;

        // Once, and never unset. It is what lets the wording below tell a machine that has never
        // answered from one that was measuring until a moment ago and has stopped - two states
        // that arrive as the SAME outcome, because a service which refuses to serve a sample
        // that stopped advancing answers exactly like a service that has not sampled yet.
        // Latched on purpose: read as "was it reachable last time", it would be true on the
        // first failed reading and false on the second, and the row would alternate between the
        // two sentences once a probe.
        hasMeasured |= outcome == ServiceOutcome.Ok;

        if (outcome == ServiceOutcome.Ok)
        {
            FailingSince = null;
            DowntimeText = string.Empty;
            Status = MachineStatus.Reachable;
            Detail = "Reachable";

            return;
        }

        FailingSince ??= now;

        StatusMessage message = StatusEscalation.MessageFor(
            outcome, reason, now - FailingSince.Value, Endpoint, hasMeasured);

        Status = message.Tone == StatusTone.Error ? MachineStatus.Faulted : MachineStatus.Warning;
        Detail = message.Title;

        // The gate is the TONE, not the state: within the ten seconds of tolerance the tone
        // is neutral and nothing is said yet, because a counter that starts on every
        // blip teaches you to ignore it - which is what StatusEscalation exists to
        // prevent. It is the tone and not the Faulted state because a "No readings yet" arrives
        // AFTER the tolerance but stays a warning, not a red, and can last days: filtering on
        // red would leave it out precisely while it is the thing that lasts longest.
        DowntimeText = message.Tone == StatusTone.Informational
            ? string.Empty
            : "for " + Downtime.Describe(now - FailingSince.Value);
    }
}