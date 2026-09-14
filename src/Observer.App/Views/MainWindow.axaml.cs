using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Views;

/// <summary>
/// The window. The code-behind does the things the view model cannot, because it does not know
/// what a window is: bringing the process panel into view when it opens, telling the view model
/// when the window is minimized, remembering where it was and how big, and scaling everything
/// when the zoom changes.
/// </summary>
/// <remarks>
/// The rules that can be tested without a window live elsewhere: what to remember on close is
/// <see cref="WindowPlacement.AtClose"/>, and whether a position sits on a screen is
/// <see cref="WindowPlacement.WithinAnyOf"/>. What stays here is only the reads and the writes
/// of the window's properties.
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>How much of the process panel to bring into view when it opens.</summary>
    /// <remarks>
    /// The title, the headers and the first rows: enough to see that it opened and what it
    /// contains. Not the whole panel — fifteen rows are taller than the default window, and
    /// bringing all of it into view pushed the gauges out, including the one that had just
    /// been clicked.
    /// </remarks>
    private const double PanelPeekHeight = 160d;

    private readonly double baseMinWidth;
    private readonly double baseMinHeight;

    private INotifyPropertyChanged? observedContext;
    private Preferences preferences;

    /// <summary>The last geometry seen in the normal state in this session, if there was one.</summary>
    private WindowPlacement? lastNormalGeometry;

    /// <summary>Whether the last state that was not minimized was maximized.</summary>
    private bool wasMaximized;

    /// <summary>The scale applied right now.</summary>
    private double scale = 1d;

    /// <summary>Who had the focus when the panel opened: normally, the gauge.</summary>
    private IInputElement? focusBeforeOpen;

    /// <summary>Constructor that Avalonia's XAML compiler demands, and that nobody calls.</summary>
    /// <remarks>
    /// The application always uses the one that takes the preferences, already read and already
    /// applied for the theme. This one exists only because without a public parameterless
    /// constructor the window's XAML does not compile (AVLN3000).
    /// </remarks>
    public MainWindow()
        : this(PreferencesStore.Read())
    {
    }

    /// <summary>Builds the window and puts it back where it was.</summary>
    /// <param name="preferences">The preferences already read, and already applied for the theme.</param>
    public MainWindow(Preferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        InitializeComponent();

        // The minimums written in the XAML are the ones at scale 1: at scale 1.3 the same
        // window has to be 30% bigger to hold the same layout, and at 0.75 it can be
        // 25% smaller.
        baseMinWidth = MinWidth;
        baseMinHeight = MinHeight;

        this.preferences = preferences;
        Reposition(preferences.Placement);

        // With a Post, not inside the handler: on Windows the maximized state arrives in the
        // same burst of events that carries the new position and size, and whoever reads
        // WindowState inside the handler risks recording the maximized geometry as if it were
        // the normal one. Deferred to the queue, the check runs once the burst is over.
        PositionChanged += (_, _) => Dispatcher.UIThread.Post(RecordIfNormal);
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(RecordIfNormal);

        DataContextChanged += (_, _) => Observe();
        Closing += (_, _) => SaveOnClose();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != WindowStateProperty)
        {
            return;
        }

        WindowState state = change.GetNewValue<WindowState>();

        // Minimized says nothing about how it was: what is remembered is the last state that was
        // not minimized.
        if (state != WindowState.Minimized)
        {
            wasMaximized = state == WindowState.Maximized;
        }

        // Minimized, the window reads every ten seconds instead of every second. Only the
        // window knows the state; the view model decides the cadence.
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.IsMinimized = state == WindowState.Minimized;
        }
    }

    /// <summary>Puts the window back where it was, if that place still exists.</summary>
    /// <remarks>
    /// The check against the screens is not pedantry: with an external monitor unplugged the
    /// window would reopen outside everything, invisible and with no way to grab it. In that case
    /// it opens where the system decides, as it did the first time. Maximized comes back even
    /// then: the state belongs to the screen that is there, not to the one that is missing. And it
    /// is set BEFORE the window is shown, so it appears already maximized instead of snapping
    /// to it afterwards.
    /// </remarks>
    private void Reposition(WindowPlacement? savedPosition)
    {
        if (savedPosition is null)
        {
            return;
        }

        if (savedPosition.WithinAnyOf(WorkingAreas()) is { } onScreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(onScreen.X, onScreen.Y);
            Width = onScreen.Width;
            Height = onScreen.Height;
            lastNormalGeometry = onScreen with { Maximized = false };
        }

        if (savedPosition.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private List<WindowPlacement.WorkArea> WorkingAreas() => [.. Screens.All.Select(WorkingAreaOf)];

    private static WindowPlacement.WorkArea WorkingAreaOf(Screen screen) => new(
        screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height);

    /// <summary>Records the geometry, if the window is normal and sits on a screen.</summary>
    /// <remarks>
    /// It is only reached from a Post: the two geometry handlers defer to the queue, and between
    /// the queuing and the turn the window may have closed. From then on
    /// <c>Screens.ScreenFromWindow</c>, which <c>ApplyMinimumSize</c> always calls, throws
    /// ObjectDisposedException: the contract says so, and the condition for the throw is
    /// exactly <c>PlatformImpl == null</c>, which is the guard just above. Unsubscribing the two
    /// handlers on close would not be enough: an operation already queued can only be removed
    /// with Abort, and Post does not return its handle.
    /// Measured on an Avalonia bench: with the close button, with Alt+F4 and by closing right
    /// after a real drag it never happened (0 out of 160 runs); with a bare Close() in the same
    /// turn as a burst of geometry it always happens (20 out of 20). Why the two paths behave
    /// differently has not been proven, so it is not written here: the guard covers both of them.
    /// Today the application never calls Close().
    /// </remarks>
    private void RecordIfNormal()
    {
        if (PlatformImpl is null)
        {
            return;
        }

        if (WindowState == WindowState.Normal && CurrentGeometry().WithinAnyOf(WorkingAreas()) is { } normalGeometry)
        {
            lastNormalGeometry = normalGeometry;
        }

        // The window may have moved to a smaller screen: the cap on the minimums is recomputed
        // against that one.
        ApplyMinimumSize();
    }

    /// <summary>Writes where the window is, the zoom, the theme and the rest, for next time.</summary>
    private void SaveOnClose()
    {
        MainViewModel? viewModel = DataContext as MainViewModel;
        double scaleToSave = viewModel?.Zoom ?? preferences.Zoom;
        string themeToSave = viewModel?.Theme ?? preferences.Theme;
        string periodToSave = viewModel?.HistoryPeriod ?? preferences.HistoryPeriod;

        WindowPlacement? position = WindowPlacement.AtClose(
            minimized: WindowState == WindowState.Minimized,
            maximized: wasMaximized,
            lastNormalGeometry,
            preferences.Placement,
            CurrentGeometry());

        // No ?? on the name: null here means "this computer", not "I do not know". With a
        // fallback to the old value, anyone moving from a remote machine to the local one would
        // find the remote one reopened for ever.
        preferences = new Preferences(
            position,
            scaleToSave,
            themeToSave,
            viewModel?.MachineToRemember,
            periodToSave);
        PreferencesStore.Write(preferences);
    }

    /// <summary>Writes the preferences, taking from the view model EVERYTHING it knows.</summary>
    /// <param name="viewModel">The view model on screen.</param>
    /// <remarks>
    /// Not <c>preferences with { the one thing that changed }</c>: <c>preferences</c> is still
    /// the record read at start-up, and period and machine change WITHOUT writing - only the
    /// close saves them. Choosing "7 days" and then changing the zoom put the new zoom on disk
    /// next to the start-up period; and if the process then died before the close (forced
    /// shutdown, kill) the choice made FIRST was lost and the one made AFTER survived, which is
    /// the opposite of what anyone expects. Not the position: the window knows that one, not the
    /// view model, and it has to be read at close - see <c>SaveOnClose</c>.
    /// </remarks>
    private void Save(MainViewModel viewModel)
    {
        // No ?? on the machine, for the same reason written in SaveOnClose: null means
        // "this computer", not "I do not know".
        preferences = preferences with
        {
            Zoom = viewModel.Zoom,
            Theme = viewModel.Theme,
            MachineName = viewModel.MachineToRemember,
            HistoryPeriod = viewModel.HistoryPeriod,
        };
        PreferencesStore.Write(preferences);
    }

    private WindowPlacement CurrentGeometry() => new(
        Position.X, Position.Y, (int)Math.Round(Width), (int)Math.Round(Height), Maximized: false);

    private void Observe()
    {
        if (observedContext is not null)
        {
            observedContext.PropertyChanged -= OnViewModelPropertyChanged;
        }

        observedContext = DataContext as INotifyPropertyChanged;

        if (DataContext is MainViewModel viewModel)
        {
            // The saved values enter the view model BEFORE subscribing to PropertyChanged:
            // the application has already applied the theme and the scale is applied by hand
            // just below, so the handler must not fire. It did fire, and rewrote the file it
            // had just read, identical, at every start-up.
            viewModel.Zoom = preferences.Zoom;
            ApplyScale(viewModel.Zoom);
            viewModel.Theme = preferences.Theme;
            viewModel.HistoryPeriod = preferences.HistoryPeriod;
        }

        if (observedContext is not null)
        {
            observedContext.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.Zoom))
        {
            ApplyScale(viewModel.Zoom);
            Save(viewModel);

            return;
        }

        if (e.PropertyName == nameof(MainViewModel.Theme))
        {
            (Application.Current as App)?.ApplyTheme(viewModel.Theme);
            Save(viewModel);

            return;
        }

        if (e.PropertyName != nameof(MainViewModel.IsProcessPanelOpen))
        {
            return;
        }

        if (!viewModel.IsProcessPanelOpen)
        {
            // With the panel closed, the focus goes back where it came from instead of
            // vanishing with the list: for someone using the keyboard, losing focus means
            // starting over from scratch.
            focusBeforeOpen?.Focus();
            focusBeforeOpen = null;

            return;
        }

        focusBeforeOpen = FocusManager?.GetFocusedElement();

        // AFTER the layout, not right away: the panel that has just been made visible has no
        // size yet, and bringing an empty rectangle into view goes nowhere. And the focus goes
        // into the list, so the arrow keys pick the row and Enter has something to act on.
        // Without this, at the default size a click on the gauge opened the panel below the
        // fold and looked as if it had done nothing: it was the worst defect the review found,
        // and this is the whole remedy.
        Dispatcher.UIThread.Post(
            () =>
            {
                Rect panelBounds = ProcessPanel.Bounds;
                ProcessPanel.BringIntoView(
                    new Rect(0d, 0d, panelBounds.Width, Math.Min(panelBounds.Height, PanelPeekHeight)));
                ProcessListBox.Focus();
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>Scales the whole window, and its minimum size with it.</summary>
    /// <remarks>
    /// Below 1 is the one new ordering constraint: the width saved in the file can be below the
    /// XAML minimum (at 0.75 the minimum is 540), so this has to run before the first layout. It
    /// runs from <see cref="Observe"/>, on the DataContext assignment, which in <c>App</c> comes
    /// before Show.
    /// </remarks>
    private void ApplyScale(double newScale)
    {
        scale = newScale;
        Root.LayoutTransform = newScale == 1d ? null : new ScaleTransform(newScale, newScale);
        ApplyMinimumSize();
    }

    /// <summary>The XAML minimums times the scale, but never bigger than the screen.</summary>
    /// <remarks>
    /// At 150% the XAML minimums become 1080x780 logical, and on a 1366x768 laptop the working
    /// area is 720 tall: with no cap the window would stretch past the screen and could no
    /// longer be made smaller. Below the design minimum the content scrolls, which is what the
    /// ScrollViewer is there for. The working area is in physical pixels and the minimums in
    /// logical ones: divide by the screen's scaling.
    /// </remarks>
    private void ApplyMinimumSize()
    {
        Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        double scaling = screen?.Scaling ?? 1d;
        double screenWidth = screen is null ? double.PositiveInfinity : screen.WorkingArea.Width / scaling;
        double screenHeight = screen is null ? double.PositiveInfinity : screen.WorkingArea.Height / scaling;

        MinWidth = Math.Min(baseMinWidth * scale, screenWidth);
        MinHeight = Math.Min(baseMinHeight * scale, screenHeight);
    }
}