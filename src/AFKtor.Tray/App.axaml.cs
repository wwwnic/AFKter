using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

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
            tray = new TrayController(this, desktop);
            desktop.Exit += (_, _) => tray.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
