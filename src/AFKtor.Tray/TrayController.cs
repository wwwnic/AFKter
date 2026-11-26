using AFKtor.Core;
using AFKtor.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;

namespace AFKtor.Tray;

/// <summary>Owns the tray icon, its menu and the Core services for the app's lifetime.</summary>
internal sealed class TrayController : IDisposable
{
    private static readonly string dataDirectory = TestOverrides.DataDirectory;

    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly DiagnosticRuntime diagnostics = DiagnosticRuntime.Start(
        Path.Combine(dataDirectory, "diagnostics", "discovery"),
        historyDirectory: Path.Combine(dataDirectory, "diagnostics", "history"));
    private readonly CancellationTokenSource stopping = new();
    private readonly HttpClient client;
    private readonly ReadyCheckController controller;
    private readonly AcceptDelaySettingsStore settingsStore = new(Path.Combine(dataDirectory, "settings.json"));
    private readonly ReadyCheckSessionMonitor monitor;
    private readonly MatchStatusTracker match = new();
    private readonly NativeMenuItem status = new("League: Disconnected") { IsEnabled = false };
    private readonly NativeMenuItem matchStatus = new("Match: unknown") { IsEnabled = false, IsVisible = false };
    private readonly NativeMenuItem autoAcceptStatus = new("Auto-accept: Disarmed") { IsEnabled = false };
    private readonly NativeMenuItem arm = new("Arm auto-accept") { ToggleType = MenuItemToggleType.CheckBox };
    private readonly NativeMenuItem settings = new("Settings…");
    private readonly NativeMenuItem exit = new("Exit");
    private readonly WindowIcon disconnectedIcon = TrayIcons.Create(Color.FromRgb(0x80, 0x80, 0x80));
    private readonly WindowIcon connectedIcon = TrayIcons.Create(Color.FromRgb(0x1E, 0x88, 0xE5));
    private readonly WindowIcon inMatchIcon = TrayIcons.Create(Color.FromRgb(0x43, 0xA0, 0x47));
    private readonly WindowIcon attentionIcon = TrayIcons.Create(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private readonly TrayIcon icon;
    private readonly DispatcherTimer discoveryTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer displayTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private SettingsWindow? settingsWindow;
    private AcceptDelaySettings savedSettings;
    private Task refresh = Task.CompletedTask;
    private bool disposed;

    public TrayController(Application application, IClassicDesktopStyleApplicationLifetime desktop)
    {
        this.desktop = desktop;
        controller = new ReadyCheckController(diagnostics: diagnostics.Collector);
        savedSettings = settingsStore.Load();
        controller.AcceptDelay = TimeSpan.FromSeconds(savedSettings.DelaySeconds);
        // Opt-in only. The controller's usual rules still apply: a check already pending
        // when League first connects is skipped, so arming early never accepts a stale check.
        if (savedSettings.ArmOnLaunch) { controller.SetArmed(true); }
        // LCU's self-signed certificate is trusted only for loopback.
        // HTTP and WebSocket requests share the no-redirect/no-proxy policy.
        client = LcuHttp.CreateClient();
        monitor = new ReadyCheckSessionMonitor(async token =>
            LcuConnection.Parse(await LeagueClientDiscovery.ReadLockfileAsync(token)),
            connection => new LcuReadyCheckClient(connection, client), controller, diagnostics.Collector, match);

        arm.Click += (_, _) =>
        {
            // The controller is the source of truth; the check mark follows it.
            controller.SetArmed(!controller.IsArmed);
            RenderStatus();
        };
        settings.Click += (_, _) => ShowSettings();
        exit.Click += async (_, _) =>
        {
            try { await StopAsync(); }
            catch (Exception error)
            {
                // Exit must always exit, even if draining League work failed.
                ErrorLog.Write("Exit drain failed", error);
                desktop.Shutdown();
            }
        };
        var menu = new NativeMenu();
        menu.Items.Add(status);
        menu.Items.Add(matchStatus);
        menu.Items.Add(autoAcceptStatus);
        menu.Items.Add(arm);
        menu.Items.Add(settings);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);
        icon = new TrayIcon
        {
            Icon = disconnectedIcon,
            ToolTipText = "AFKtor — League: Disconnected — Disarmed",
            Menu = menu,
            IsVisible = true
        };
        TrayIcon.SetIcons(application, [icon]);

        discoveryTimer.Tick += async (_, _) =>
        {
            discoveryTimer.Stop();
            try
            {
                refresh = RefreshAsync();
                await refresh;
            }
            finally
            {
                // Polling must survive any single failed refresh, or League would stay
                // "disconnected" for the rest of the session.
                if (!stopping.IsCancellationRequested) { discoveryTimer.Start(); }
            }
        };
        // State is sampled on the UI thread; network callbacks never touch controls.
        displayTimer.Tick += (_, _) =>
        {
            try { RenderStatus(); }
            catch (Exception error) { ErrorLog.Write("Tray status refresh failed", error); }
        };
        discoveryTimer.Start();
        displayTimer.Start();
    }

    private void ShowSettings()
    {
        if (stopping.IsCancellationRequested) { return; }
        if (settingsWindow is not null)
        {
            // Activate alone does not bring back a minimized window.
            if (settingsWindow.WindowState == WindowState.Minimized) { settingsWindow.WindowState = WindowState.Normal; }
            settingsWindow.Activate();
            return;
        }

        var startupSupported = StartupRegistration.IsSupported;
        var startupEnabled = startupSupported && StartupRegistration.IsEnabled;
        settingsWindow = new SettingsWindow(savedSettings, startupSupported, startupEnabled, (value, startWithWindows) =>
        {
            // Change Windows startup first because it can be put back; then the file.
            // A failed save leaves both and the controller's effective delay unchanged.
            var changeStartup = startupSupported && (startWithWindows || startupEnabled);
            if (changeStartup) { StartupRegistration.SetEnabled(startWithWindows); }
            try { settingsStore.Save(value); }
            catch
            {
                if (changeStartup)
                {
                    try { StartupRegistration.SetEnabled(startupEnabled); }
                    catch (IOException error) { ErrorLog.Write("Restoring start with Windows failed", error); }
                }
                throw;
            }
            savedSettings = value;
            controller.AcceptDelay = TimeSpan.FromSeconds(value.DelaySeconds);
        });
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        // Modeless so tray interaction, timers and shutdown remain available.
        settingsWindow.Show();
    }

    private void RenderStatus()
    {
        if (stopping.IsCancellationRequested) { return; }
        status.Header = monitor.IsConnected ? "League: Connected" : "League: Disconnected";
        var remaining = controller.Remaining;
        autoAcceptStatus.Header = remaining is { } time && time > TimeSpan.Zero
            ? $"Auto-accept: accepting in {time.TotalSeconds:0.0}s"
            : $"Auto-accept: {controller.Status}";
        arm.IsChecked = controller.IsArmed;
        // The phase is only meaningful while connected; the tracker resets on disconnect.
        var current = monitor.IsConnected ? match.Current : MatchStatus.Unknown;
        var description = current.Describe();
        matchStatus.IsVisible = description is not null;
        if (description is not null) { matchStatus.Header = $"Match: {description}"; }
        var tooltip = $"AFKtor — {status.Header} — {(controller.IsArmed ? "Armed" : "Disarmed")}";
        icon.ToolTipText = description is null ? tooltip : $"{tooltip} — {description}";
        icon.Icon = !monitor.IsConnected ? disconnectedIcon
            : current.NeedsAttention ? attentionIcon
            : current.IsInMatch ? inMatchIcon
            : connectedIcon;
    }

    private async Task RefreshAsync()
    {
        try { await monitor.RefreshAsync(stopping.Token); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Keep running: the next tick starts a fresh discovery and session.
            ErrorLog.Write("League refresh failed", error);
        }
    }

    private async Task StopAsync()
    {
        if (stopping.IsCancellationRequested) { return; }
        discoveryTimer.Stop();
        displayTimer.Stop();
        arm.IsEnabled = false;
        settings.IsEnabled = false;
        exit.IsEnabled = false;
        settingsWindow?.Close();
        stopping.Cancel();
        icon.IsVisible = false;
        // Stop invalidates the controller immediately, then drains HTTP/event work.
        await monitor.StopAsync();
        await refresh;
        desktop.Shutdown();
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        stopping.Cancel();
        discoveryTimer.Stop();
        displayTimer.Stop();
        settingsWindow?.Close();
        // Core teardown never captures the UI context, including on OS exit.
        monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        diagnostics.DisposeAsync().AsTask().GetAwaiter().GetResult();
        icon.Dispose();
        client.Dispose();
        stopping.Dispose();
    }
}
