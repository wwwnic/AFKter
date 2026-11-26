using System.Security;
using Microsoft.Win32;

namespace AFKtor.Tray;

/// <summary>
/// Starts AFKtor when the user signs in to Windows, through the per-user Run key.
/// This is Windows' own state, not part of settings.json, so it is read back live.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AFKtor";

    /// <summary>The executable to register, or null when there is no stable AFKtor.exe to start.</summary>
    private static string? ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            // Under "dotnet AFKtor.Tray.dll" the process is the shared dotnet host, which
            // would start nothing useful at sign-in.
            return path is not null && File.Exists(path) &&
                !string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase)
                    ? path : null;
        }
    }

    public static bool IsSupported => OperatingSystem.IsWindows() && ExecutablePath is not null;

    public static bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) { return false; }
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string { Length: > 0 };
            }
            catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
            {
                // Unknown counts as off; saving again re-applies the user's choice.
                return false;
            }
        }
    }

    /// <summary>Registers the current executable, which also repairs an entry left by a moved install.</summary>
    /// <exception cref="IOException">The Run key could not be changed.</exception>
    public static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Start with Windows is available only on Windows."); }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                var path = ExecutablePath ?? throw new IOException("AFKtor is not running from its own executable.");
                key.SetValue(ValueName, $"\"{path}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException)
        {
            throw new IOException("Windows startup settings could not be changed.", error);
        }
    }
}
