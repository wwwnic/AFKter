using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AFKtor.Tray;

public sealed class App : Application
{
    private TrayController? tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // There is no main window: only the tray menu's Exit ends the process.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // A UI handler bug must not end a play session: record it and keep the tray alive.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                ErrorLog.Write("Unhandled UI exception", e.Exception);
                e.Handled = true;
            };
            tray = new TrayController(this, desktop);
            desktop.Exit += (_, _) => tray.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
