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
    private static readonly string dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AFKtor");

    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly DiagnosticRuntime diagnostics = DiagnosticRuntime.Start(
        Path.Combine(dataDirectory, "diagnostics", "discovery"),
        historyDirectory: Path.Combine(dataDirectory, "diagnostics", "history"));
    private readonly CancellationTokenSource stopping = new();
    private readonly HttpClient client;
    private readonly ReadyCheckController controller;
    private readonly AcceptDelaySettingsStore settingsStore = new(Path.Combine(dataDirectory, "settings.json"));
    private readonly ReadyCheckSessionMonitor monitor;
    private readonly NativeMenuItem status = new("League: Disconnected") { IsEnabled = false };
    private readonly NativeMenuItem autoAcceptStatus = new("Auto-accept: Disarmed") { IsEnabled = false };
    private readonly NativeMenuItem arm = new("Arm auto-accept") { ToggleType = MenuItemToggleType.CheckBox };
    private readonly NativeMenuItem settings = new("Settings…");
    private readonly NativeMenuItem exit = new("Exit");
    private readonly WindowIcon disconnectedIcon = TrayIcons.Create(Color.FromRgb(0x80, 0x80, 0x80));
    private readonly WindowIcon connectedIcon = TrayIcons.Create(Color.FromRgb(0x1E, 0x88, 0xE5));
    private readonly TrayIcon icon;
    private readonly DispatcherTimer discoveryTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer displayTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private SettingsWindow? settingsWindow;
    private Task refresh = Task.CompletedTask;
    private bool disposed;

    public TrayController(Application application, IClassicDesktopStyleApplicationLifetime desktop)
    {
        this.desktop = desktop;
        controller = new ReadyCheckController(diagnostics: diagnostics.Collector);
        controller.AcceptDelay = TimeSpan.FromSeconds(settingsStore.Load().DelaySeconds);
        // LCU's self-signed certificate is trusted only by this loopback handler.
        // HTTP and WebSocket requests share the no-redirect/no-proxy policy.
        client = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                request.RequestUri is { Scheme: "https", Host: "127.0.0.1" }
        }) { Timeout = TimeSpan.FromSeconds(3) };
        monitor = new ReadyCheckSessionMonitor(async token =>
            LcuConnection.Parse(await LeagueClientDiscovery.ReadLockfileAsync(token)),
            connection => new LcuReadyCheckClient(connection, client), controller, diagnostics.Collector);

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

        settingsWindow = new SettingsWindow(new AcceptDelaySettings((int)controller.AcceptDelay.TotalSeconds), value =>
        {
            // A failed save leaves the controller's effective setting unchanged.
            settingsStore.Save(value);
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
        icon.ToolTipText = $"AFKtor — {status.Header} — {(controller.IsArmed ? "Armed" : "Disarmed")}";
        icon.Icon = monitor.IsConnected ? connectedIcon : disconnectedIcon;
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
