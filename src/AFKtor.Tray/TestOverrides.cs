namespace AFKtor.Tray;

/// <summary>
/// Environment variables that point the tray app at a fake League client for manual testing
/// (see docs/testing.md). Unset, the app behaves exactly as shipped.
/// </summary>
internal static class TestOverrides
{
    /// <summary>Read this lockfile instead of finding the running LeagueClient process.</summary>
    public const string LockfileVariable = "AFKTOR_LCU_LOCKFILE";
    /// <summary>Keep settings, diagnostics and the error log here instead of %LOCALAPPDATA%\AFKtor.</summary>
    public const string DataDirectoryVariable = "AFKTOR_DATA_DIR";

    public static string? LockfilePath => Read(LockfileVariable);

    public static string DataDirectory { get; } = Read(DataDirectoryVariable) ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AFKtor");

    private static string? Read(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
