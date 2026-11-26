using System.Text.Json;

namespace AFKtor.Core;

public sealed class AcceptDelaySettingsStore(string path)
{
    private readonly string settingsPath = Path.GetFullPath(path);

    public AcceptDelaySettings Load()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return AcceptDelaySettings.Default; }
            // Each preference falls back on its own, so a damaged delay never turns
            // arm-on-launch on, and only an explicit JSON true enables it.
            var seconds = root.TryGetProperty(nameof(AcceptDelaySettings.DelaySeconds), out var value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int stored) &&
                stored is >= AcceptDelaySettings.MinimumSeconds and <= AcceptDelaySettings.MaximumSeconds
                    ? stored : AcceptDelaySettings.Default.DelaySeconds;
            var armOnLaunch = root.TryGetProperty(nameof(AcceptDelaySettings.ArmOnLaunch), out var arm) &&
                arm.ValueKind == JsonValueKind.True;
            return new AcceptDelaySettings(seconds) { ArmOnLaunch = armOnLaunch };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or damaged preference must not stop the tray from starting.
        }

        return AcceptDelaySettings.Default;
    }

    public void Save(AcceptDelaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, settings);
                // Reach the disk before the rename so a power loss cannot leave an empty file.
                file.Flush(flushToDisk: true);
            }
            // Same-directory replacement keeps the previous setting intact until
            // the new file is fully written. Never truncate the destination first.
            File.Move(temporaryPath, settingsPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Cleanup must not hide the original save error.
            }
        }
    }
}
