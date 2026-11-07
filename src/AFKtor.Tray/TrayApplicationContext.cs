using AFKtor.Core;
using AFKtor.Diagnostics;

namespace AFKtor.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly DiagnosticRuntime diagnostics = DiagnosticRuntime.Start(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AFKtor", "diagnostics", "discovery"), historyDirectory: Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AFKtor", "diagnostics", "history"));
    private readonly CancellationTokenSource stopping = new();
    private readonly HttpClient client;
    private readonly ReadyCheckController controller;
    private readonly AcceptDelaySettingsStore settingsStore = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AFKtor", "settings.json"));
    private readonly ReadyCheckSessionMonitor monitor;
    private readonly ToolStripMenuItem status = new("League: Disconnected") { Enabled = false };
    private readonly ToolStripMenuItem autoAcceptStatus = new("Auto-accept: Disarmed") { Enabled = false };
    private readonly ToolStripMenuItem arm = new("Arm auto-accept") { CheckOnClick = true };
    private readonly ToolStripMenuItem settings = new("Settings…");
    private SettingsForm? settingsForm;
    private readonly ContextMenuStrip menu = new();
    private readonly NotifyIcon icon;
    private readonly System.Windows.Forms.Timer discoveryTimer = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer displayTimer = new() { Interval = 100 };
    private Task refresh = Task.CompletedTask;

    public TrayApplicationContext()
    {
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
        arm.CheckedChanged += (_, _) =>
        {
            controller.SetArmed(arm.Checked);
            RenderStatus();
        };
        menu.Items.Add(status);
        menu.Items.Add(autoAcceptStatus);
        menu.Items.Add(arm);
        settings.Click += (_, _) => ShowSettings();
        menu.Items.Add(settings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await StopAsync());
        icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "AFKtor — League: Disconnected — Disarmed",
            ContextMenuStrip = menu,
            Visible = true
        };
        discoveryTimer.Tick += async (_, _) =>
        {
            discoveryTimer.Stop();
            refresh = RefreshAsync();
            await refresh;
            if (!stopping.IsCancellationRequested) { discoveryTimer.Start(); }
        };
        // State is sampled on the WinForms thread; network callbacks never touch controls.
        displayTimer.Tick += (_, _) => RenderStatus();
        discoveryTimer.Start();
        displayTimer.Start();
    }

    private void ShowSettings()
    {
        if (stopping.IsCancellationRequested) { return; }
        if (settingsForm is not null)
        {
            settingsForm.Activate();
            return;
        }

        settingsForm = new SettingsForm(new AcceptDelaySettings((int)controller.AcceptDelay.TotalSeconds), value =>
        {
            // A failed save leaves the controller's effective setting unchanged.
            settingsStore.Save(value);
            controller.AcceptDelay = TimeSpan.FromSeconds(value.DelaySeconds);
        });
        settingsForm.FormClosed += (_, _) => settingsForm = null;
        // Modeless so tray interaction, timers and shutdown remain available.
        settingsForm.Show();
    }

    private void RenderStatus()
    {
        if (stopping.IsCancellationRequested) { return; }
        status.Text = monitor.IsConnected ? "League: Connected" : "League: Disconnected";
        var remaining = controller.Remaining;
        autoAcceptStatus.Text = remaining is { } time && time > TimeSpan.Zero
            ? $"Auto-accept: accepting in {time.TotalSeconds:0.0}s"
            : $"Auto-accept: {controller.Status}";
        icon.Text = $"AFKtor — {status.Text} — {(controller.IsArmed ? "Armed" : "Disarmed")}";
        icon.Icon = monitor.IsConnected ? SystemIcons.Information : SystemIcons.Application;
    }

    private async Task RefreshAsync()
    {
        try { await monitor.RefreshAsync(stopping.Token); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private async Task StopAsync()
    {
        if (stopping.IsCancellationRequested) { return; }
        discoveryTimer.Stop();
        displayTimer.Stop();
        arm.Enabled = false;
        settings.Enabled = false;
        settingsForm?.Close();
        stopping.Cancel();
        icon.Visible = false;
        // Stop invalidates the controller immediately, then drains HTTP/event work.
        await monitor.StopAsync();
        await refresh;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            stopping.Cancel();
            discoveryTimer.Dispose();
            displayTimer.Dispose();
            settingsForm?.Dispose();
            // Core teardown never captures the UI context, including on OS exit.
            monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            diagnostics.DisposeAsync().AsTask().GetAwaiter().GetResult();
            icon.Dispose();
            menu.Dispose();
            client.Dispose();
            stopping.Dispose();
        }
        base.Dispose(disposing);
    }
}
