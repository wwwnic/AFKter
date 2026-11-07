using AFKtor.Core;

namespace AFKtor.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly CancellationTokenSource stopping = new();
    private readonly HttpClient client;
    private readonly ConnectionMonitor monitor;
    private readonly ToolStripMenuItem status = new("League: Disconnected") { Enabled = false };
    private readonly ContextMenuStrip menu = new();
    private readonly NotifyIcon icon;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private Task refresh = Task.CompletedTask;

    public TrayApplicationContext()
    {
        // LCU uses a self-signed certificate. This handler serves only the hard-coded
        // loopback URL in LcuConnectionProbe; never follow redirects or use a proxy.
        client = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                request.RequestUri is { Scheme: "https", Host: "127.0.0.1" }
        }) { Timeout = TimeSpan.FromSeconds(3) };
        monitor = new ConnectionMonitor(new LcuConnectionProbe(LeagueClientDiscovery.ReadLockfileAsync, client));
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await StopAsync());
        icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "AFKtor — League: Disconnected",
            ContextMenuStrip = menu,
            Visible = true
        };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            refresh = RefreshAsync();
            await refresh;
            if (!stopping.IsCancellationRequested)
            {
                timer.Start();
            }
        };
        timer.Start();
    }

    private async Task RefreshAsync()
    {
        try
        {
            await monitor.RefreshAsync(stopping.Token);
            if (!stopping.IsCancellationRequested)
            {
                status.Text = monitor.IsConnected ? "League: Connected" : "League: Disconnected";
                icon.Text = $"AFKtor — {status.Text}";
                icon.Icon = monitor.IsConnected ? SystemIcons.Information : SystemIcons.Application;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Exit cancels a pending discovery or HTTP request before disposing the tray.
        }
    }

    private async Task StopAsync()
    {
        if (stopping.IsCancellationRequested)
        {
            return;
        }

        timer.Stop();
        stopping.Cancel();
        icon.Visible = false;
        await refresh;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            stopping.Cancel();
            timer.Dispose();
            icon.Dispose();
            menu.Dispose();
            client.Dispose();
            stopping.Dispose();
        }

        base.Dispose(disposing);
    }
}
