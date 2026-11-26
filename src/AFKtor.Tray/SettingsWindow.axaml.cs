using AFKtor.Core;
using Avalonia.Controls;

namespace AFKtor.Tray;

public sealed partial class SettingsWindow : Window
{
    private readonly Action<AcceptDelaySettings>? saveSettings;

    // Required by the Avalonia XAML loader and previewer.
    public SettingsWindow()
    {
        InitializeComponent();
        Delay.Minimum = AcceptDelaySettings.MinimumSeconds;
        Delay.Maximum = AcceptDelaySettings.MaximumSeconds;
        Cancel.Click += (_, _) => Close();
        Save.Click += (_, _) => SaveAndClose();
    }

    public SettingsWindow(AcceptDelaySettings settings, Action<AcceptDelaySettings> saveSettings) : this()
    {
        this.saveSettings = saveSettings;
        Delay.Value = settings.DelaySeconds;
    }

    private void SaveAndClose()
    {
        if (saveSettings is null) { return; }
        // Typed text is not always clamped, so validate before constructing the setting.
        if (Delay.Value is not { } value || value != decimal.Truncate(value) ||
            value < AcceptDelaySettings.MinimumSeconds || value > AcceptDelaySettings.MaximumSeconds)
        {
            ShowError($"Enter a whole number from {AcceptDelaySettings.MinimumSeconds} to {AcceptDelaySettings.MaximumSeconds} seconds.");
            return;
        }

        try
        {
            saveSettings(new AcceptDelaySettings(decimal.ToInt32(value)));
            Close();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowError("The delay could not be saved and has not changed. Check that your local application data folder is writable, then try again.");
        }
    }

    private void ShowError(string message)
    {
        Error.Text = message;
        Error.IsVisible = true;
    }
}
