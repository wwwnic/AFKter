using Avalonia;

namespace AFKtor.Tray;

internal static class Program
{
    // Per Windows session, so each signed-in user can run their own copy.
    private const string InstanceName = @"Local\AFKtor.Tray.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        // Two copies would both auto-accept and fight over diagnostic history.
        using var instance = new Mutex(initiallyOwned: true, InstanceName, out var created);
        if (!created) { return 0; }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ErrorLog.Write("Unhandled exception (process terminating)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // A forgotten background task must not take the tray down later.
            ErrorLog.Write("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
