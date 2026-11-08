using AFKtor.Core;

namespace AFKtor.Tray;

internal sealed class SettingsForm : Form
{
    public SettingsForm(AcceptDelaySettings settings, Action<AcceptDelaySettings> saveSettings)
    {
        Text = "Afkter settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(400, 180);

        var delay = new NumericUpDown
        {
            Minimum = AcceptDelaySettings.MinimumSeconds,
            Maximum = AcceptDelaySettings.MaximumSeconds,
            Value = settings.DelaySeconds,
            DecimalPlaces = 0,
            AccessibleName = "Accept delay in seconds",
            Width = 80,
            TabIndex = 1
        };
        var label = new Label { Text = "Accept &delay (seconds):", AutoSize = true, TabIndex = 0 };
        var input = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        input.Controls.Add(label);
        input.Controls.Add(delay);

        var note = new Label
        {
            Text = "Changes apply to the next ready check.\n0 seconds verifies and accepts immediately.",
            AutoSize = true,
            Margin = new Padding(3, 12, 3, 12)
        };
        var save = new Button { Text = "&Save", AutoSize = true, TabIndex = 0 };
        var cancel = new Button { Text = "Cancel", AutoSize = true, TabIndex = 1 };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            TabIndex = 2
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3
        };
        layout.Controls.Add(input, 0, 0);
        layout.Controls.Add(note, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) =>
        {
            try
            {
                saveSettings(new AcceptDelaySettings(decimal.ToInt32(delay.Value)));
                Close();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this,
                    "The delay could not be saved and has not changed. Check that your local application data folder is writable, then try again.",
                    "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
    }
}
