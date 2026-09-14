using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.App.Views;

namespace Observer.App;

/// <summary>
/// The application's composition root.
/// </summary>
/// <remarks>
/// No dependency injection container: there are three pieces to wire together, and a container
/// would add a level of indirection without removing a single line of code.
/// </remarks>
public partial class App : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The preferences are read ONCE, here, and handed to the window: the theme must be
            // applied before the window exists, because a TopLevel copies the theme at
            // construction; applied any later, the window would open light and then flick to
            // the saved theme.
            Preferences preferences = PreferencesStore.Read();
            ApplyTheme(preferences.Theme);

            MachineListResult machineList = MachineDirectory.Read();

            // Every open client must be closed on exit, including the ones created by switching
            // machine in the sidebar: closing only the last one would leave behind a socket for
            // every machine watched during the session.
            List<MetricsClient> openClients = [];

            MetricsClient Open(ObserverEndpoint endpoint)
            {
                // If one already exists for that endpoint, it is reused. Without that, a token
                // the service keeps rejecting would spawn one client per second for ever: the
                // re-read fires on every 401, and every client carries its own pool of
                // connections.
                if (openClients.FirstOrDefault(openClient => openClient.Endpoint == endpoint) is { } existing)
                {
                    return existing;
                }

                MetricsClient newClient = new(endpoint);
                openClients.Add(newClient);

                return newClient;
            }

            // The machine that was being watched last time, if it is still in the list;
            // otherwise the first entry, which is ALWAYS the local channel and needs no
            // configuration: after the install there is nothing to set up for the window to
            // start. Anyone watching a machine over the network no longer has to pick
            // it at every start and wait for it to connect.
            MetricsClient client = Open(
                Preferences.RememberedMachine(machineList.Machines, preferences.MachineName)
                ?? machineList.Machines[0]);

            MainViewModel? viewModel = null;

            viewModel = new MainViewModel(
                client,
                configurationProblem: null,
                // The clipboard lives on a TopLevel, that is, on a control: the view model does
                // not reference Avalonia.Controls, so the wiring is done here, where the
                // window already exists.
                copyToClipboard: text => desktop.MainWindow?.Clipboard?.SetTextAsync(text)
                    ?? Task.CompletedTask,
                rereadConfiguration: () =>
                {
                    // Re-reads from disk the entry of the machine being watched. It is needed
                    // when its token is rotated: without it the window would stay stuck on
                    // "Token rejected" until a restart, even after the file has been fixed.
                    if (viewModel?.SelectedMachine?.Endpoint is not { } currentEndpoint)
                    {
                        return null;
                    }

                    ObserverEndpoint? updatedEndpoint = MachineDirectory.Read().Machines.FirstOrDefault(
                        endpoint => endpoint.Kind == currentEndpoint.Kind && endpoint.BaseAddress == currentEndpoint.BaseAddress);

                    return updatedEndpoint is null || updatedEndpoint == currentEndpoint ? null : Open(updatedEndpoint);
                },
                machineList: machineList,
                openMachine: Open,

                // The same re-read as above, for a machine that is NOT being watched whose probe
                // comes back with a rejected token: the new entry carries the new credential.
                rereadEndpoint: endpoint => MachineDirectory.Read().Machines.FirstOrDefault(
                    candidate => candidate.Kind == endpoint.Kind && candidate.BaseAddress == endpoint.BaseAddress));

            CancellationTokenSource shutdown = new();

            desktop.MainWindow = new MainWindow(preferences)
            {
                DataContext = viewModel,
            };

            desktop.Exit += (_, _) =>
            {
                // Cancel but NOT Dispose: the refresh loop is still suspended on that token and
                // its resumption is asynchronous, so releasing the source here would open a window
                // of time in which the loop touches an already disposed object. The process is
                // exiting anyway, and there is nothing to reclaim.
                shutdown.Cancel();

                foreach (MetricsClient openClient in openClients)
                {
                    openClient.Dispose();
                }
            };

            // Post and not a direct call: at this point the dispatcher loop has not started yet
            // and Avalonia's SynchronizationContext may not be installed, so the await
            // continuations would risk resuming on any thread at all and touching the
            // ObservableCollection instances off the UI thread.
            Dispatcher.UIThread.Post(() => _ = viewModel.RunAsync(shutdown.Token));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Applies the theme to the whole application: windows, popups and the title bar.</summary>
    /// <param name="theme">The key: <c>system</c>, <c>light</c> or <c>dark</c>.</param>
    /// <remarks>
    /// On the application and never on the window: popups and tooltips are separate windows and
    /// take the theme from here. And before the window exists: a TopLevel copies the theme at
    /// construction. Re-reading the system theme when going back to "system" is belt and braces:
    /// from the decompiled code, the default variant merely clears the effective theme and the
    /// dictionaries could fall back to the light one until the next system colour change.
    /// MEASURED on 2026-09-04 on a Windows 11 machine in the dark theme, with Avalonia 12.1.1:
    /// without this line, going from Dark to System leaves the window dark, as it should, and the
    /// trap does not show up. The line stays because it costs one line, has no side effects
    /// (Windows overwrites it at the next change, as it would on its own) and it has not been
    /// measured on the other backends.
    /// </remarks>
    public void ApplyTheme(string theme)
    {
        ThemeVariant variant = ThemeOption.VariantFor(theme);

        RequestedThemeVariant = variant;

        if (variant == ThemeVariant.Default && PlatformSettings is { } settings)
        {
            bool isDark = settings.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;

            SetValue(ActualThemeVariantProperty, isDark ? ThemeVariant.Dark : ThemeVariant.Light);
        }
    }
}