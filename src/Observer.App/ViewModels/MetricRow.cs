using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Observer.App.Services;

namespace Observer.App.ViewModels;

/// <summary>
/// A metric row on screen.
/// </summary>
/// <remarks>
/// Derives from <see cref="ObservableObject"/> and NOT from <see cref="ViewModelBase"/>, and the
/// name does not end in "ViewModel": both on purpose. ViewLocator picks up
/// any ViewModelBase and, finding no Observer.App.Views.MetricRowView, would draw
/// a "Not Found" TextBlock in place of the row. Here the drawing is decided by the DataTemplate
/// declared in MainWindow.axaml.
/// </remarks>
public sealed partial class MetricRow : ObservableObject
{
    /// <summary>Builds the row from its state.</summary>
    public MetricRow(MetricRowState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        Key = state.Key;
        Label = state.Label;
        Display = state.Display;
        HasGauge = state.Fraction.HasValue;

        // When the fraction is missing, the last one stays where it is instead of being zeroed.
        // Not to keep it - the gauge disappears anyway, because HasGauge is false - but
        // because the needle animates: writing zero would give it a target, and for a few
        // tenths of a second you would see it drop to the bottom of the scale before vanishing, as
        // if the machine had emptied out rather than simply stopped answering.
        if (state.Fraction is { } fraction)
        {
            Fraction = fraction;
        }
        Severity = state.Severity;
    }

    /// <summary>Stable identity of the row.</summary>
    public string Key { get; }

    /// <summary>True when the process list can be opened from this gauge.</summary>
    /// <remarks>
    /// False on disk space, and that gauge is then not clickable at all: better
    /// no invitation than an invitation leading to an empty panel. True on disk
    /// activity, which opens the list by I/O. The reason is in <see cref="ProcessResource"/>.
    /// </remarks>
    public bool CanShowProcesses => ProcessResource.From(Key) is not null;

    /// <summary>Readable name of the metric.</summary>
    [ObservableProperty]
    public partial string Label { get; set; }

    /// <summary>The formatted value, or the reason it is missing.</summary>
    [ObservableProperty]
    public partial string Display { get; set; }

    /// <summary>How full the gauge is, from 0 to 1.</summary>
    /// <remarks>
    /// The same fraction that arrives from the service, not multiplied by a hundred. It used to be
    /// scaled to 0..100 for the progress bar that was here; the gauge works on the
    /// fraction, and the conversion in between was just one more place to get it wrong.
    /// </remarks>
    [ObservableProperty]
    public partial double Fraction { get; set; }

    /// <summary>True when the metric is a fraction and the gauge makes sense.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHistory))]
    [NotifyPropertyChangedFor(nameof(ShowHistoryNote))]
    public partial bool HasGauge { get; set; }

    /// <summary>The history intervals, from the oldest to the most recent.</summary>
    /// <remarks>
    /// Null until the history has been read. The strip is shown only when there is
    /// something to show: a wholly empty strip under a live gauge would be a
    /// question with no answer.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHistory))]
    public partial IReadOnlyList<HistoryBar>? History { get; set; }

    /// <summary>Why the history is missing, when it is.</summary>
    /// <remarks>
    /// It is here and not in the status bar on purpose: a fault of the history is NOT a
    /// fault of the machine. The machine can respond to sampling perfectly well and have
    /// persistence turned off, and painting the window red for that would teach the reader to
    /// ignore the real alarms too.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHistoryNote))]
    public partial string HistoryNote { get; set; } = string.Empty;

    /// <summary>True when there is a strip to draw.</summary>
    public bool ShowHistory => HasGauge && History is { Count: > 0 };

    /// <summary>True when there is a reason to write in place of the strip.</summary>
    public bool ShowHistoryNote => HasGauge && HistoryNote.Length > 0;

    /// <summary>Severity of what the row is saying.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial MetricSeverity Severity { get; set; }

    /// <summary>
    /// True only for a real fault. A Warmup at start-up or a metric that cannot be measured on
    /// this platform must NOT turn red: they are information, and alarming the viewer
    /// over something normal teaches them to ignore the real alarms too.
    /// </summary>
    public bool Problem => Severity == MetricSeverity.Problem;

    /// <summary>Updates the row in place, without recreating it: avoids the flicker every second.</summary>
    public void Update(MetricRowState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        Label = state.Label;
        Display = state.Display;
        HasGauge = state.Fraction.HasValue;

        // When the fraction is missing, the last one stays where it is instead of being zeroed.
        // Not to keep it - the gauge disappears anyway, because HasGauge is false - but
        // because the needle animates: writing zero would give it a target, and for a few
        // tenths of a second you would see it drop to the bottom of the scale before vanishing, as
        // if the machine had emptied out rather than simply stopped answering.
        if (state.Fraction is { } fraction)
        {
            Fraction = fraction;
        }
        Severity = state.Severity;
    }
}

/// <summary>
/// A panel on screen: a collector with its rows. Same reasons as
/// <see cref="MetricRow"/> for not deriving from <see cref="ViewModelBase"/>.
/// </summary>
public sealed partial class MetricGroup : ObservableObject
{
    /// <summary>Builds the panel from its state.</summary>
    public MetricGroup(MetricGroupState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        CollectorId = state.CollectorId;
        Title = state.Title;
        Note = state.Note ?? string.Empty;
        ShowNote = state.Note is not null;
        Severity = state.Severity;

        foreach (MetricRowState row in state.Rows)
        {
            Rows.Add(new MetricRow(row));
        }

        RefreshShowRows();
    }

    /// <summary>Identifier of the collector.</summary>
    public string CollectorId { get; }

    /// <summary>Readable title of the panel.</summary>
    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>Why the source is degraded.</summary>
    [ObservableProperty]
    public partial string Note { get; set; }

    /// <summary>True when there is a note to show.</summary>
    [ObservableProperty]
    public partial bool ShowNote { get; set; }

    /// <summary>Severity of the source's state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial MetricSeverity Severity { get; set; }

    /// <summary>True only for a real fault: see <see cref="MetricRow.Problem"/>.</summary>
    public bool Problem => Severity == MetricSeverity.Problem;

    /// <summary>The measured rows.</summary>
    public ObservableCollection<MetricRow> Rows { get; } = [];

    /// <summary>True when this panel has at least one row to WRITE OUT.</summary>
    /// <remarks>
    /// Metrics that are a fraction are read on the gauge, at the top, and are not
    /// repeated down here. A collector that emits only those would leave a title
    /// hanging over nothing: this property is what makes it disappear.
    /// </remarks>
    [ObservableProperty]
    public partial bool ShowRows { get; set; }

    /// <summary>Updates the panel in place.</summary>
    public void Update(MetricGroupState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        Title = state.Title;
        Note = state.Note ?? string.Empty;
        ShowNote = state.Note is not null;
        Severity = state.Severity;

        // As long as the keys match it updates in place; as soon as the list really changes
        // it rebuilds. Always rebuilding would make the window flash every second.
        if (!HasSameKeys(state.Rows))
        {
            Rows.Clear();

            foreach (MetricRowState row in state.Rows)
            {
                Rows.Add(new MetricRow(row));
            }

            RefreshShowRows();

            return;
        }

        for (int i = 0; i < state.Rows.Count; i++)
        {
            Rows[i].Update(state.Rows[i]);
        }

        RefreshShowRows();
    }

    private void RefreshShowRows() =>
        ShowRows = Rows.Any(row => !row.HasGauge);

    private bool HasSameKeys(IReadOnlyList<MetricRowState> states)
    {
        if (Rows.Count != states.Count)
        {
            return false;
        }

        for (int i = 0; i < states.Count; i++)
        {
            if (!string.Equals(Rows[i].Key, states[i].Key, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
