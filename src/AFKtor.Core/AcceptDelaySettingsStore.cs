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
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(nameof(AcceptDelaySettings.DelaySeconds), out var value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int seconds))
            {
                return new AcceptDelaySettings(seconds);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            JsonException or ArgumentOutOfRangeException)
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
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings));
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
