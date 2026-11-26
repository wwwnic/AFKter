namespace AFKtor.Tray;

/// <summary>
/// Last-resort record of errors the app survived or died from, so a failure during a
/// game leaves a trace. Only exception types, messages and stack traces are written:
/// LCU credentials live in request headers and never appear in exception text.
/// </summary>
internal static class ErrorLog
{
    private const long MaximumBytes = 1_000_000;
    private static readonly object gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AFKtor", "errors.log");

    public static void Write(string context, Exception? error)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                // Keep the file bounded across long sessions instead of growing forever.
                if (File.Exists(Path) && new FileInfo(Path).Length > MaximumBytes) { File.Delete(Path); }
                File.AppendAllText(Path, $"{DateTimeOffset.Now:O} {context}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never become a second failure.
        }
    }
}
