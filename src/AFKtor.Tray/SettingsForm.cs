using AFKtor.Core;

namespace AFKtor.Tray;

internal sealed class SettingsForm : Form
{
    public SettingsForm(AcceptDelaySettings settings, Action<AcceptDelaySettings> saveSettings, RolePlanService plans)
    {
        Text = "Afkter settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(520, 440);

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
        var save = new Button { Text = "&Save delay and close", AutoSize = true, TabIndex = 0 };
        var cancel = new Button { Text = "Close without saving delay", AutoSize = true, TabIndex = 1 };
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
            RowCount = 8
        };
        layout.Controls.Add(input, 0, 0);
        layout.Controls.Add(note, 0, 1);
        layout.Controls.Add(new Label { Text = "After a manual pick change in League:", AutoSize = true }, 0, 2);
        var policy = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 470,
            AccessibleName = "Deadline behavior after a manual pick change" };
        policy.Items.AddRange(["Lock current hover (default)", "Use my role plan", "Do nothing"]);
        policy.SelectedIndex = (int)plans.Current.ManualPickPolicy;
        var explanation = new Label { AutoSize = true, MaximumSize = new Size(470, 0) };
        var health = new Label { AutoSize = true, MaximumSize = new Size(470, 0) };
        var retry = new Button { Text = "Retry saving policy / plans", AutoSize = true };
        void RenderPolicy()
        {
            explanation.Text = ManualPickPolicies.Explanation(plans.Current.ManualPickPolicy) +
                "\nPolicy changes apply immediately and autosave. Closing never reverts them. A sent request cannot be recalled.";
            health.Text = $"Policy / plans revision {plans.Current.Revision}: " + (plans.Health switch
            {
                PlanSaveHealth.Saved => $"Saved (revision {plans.SavedRevision})",
                PlanSaveHealth.SaveFailed => "UNSAVED — save failed; retrying automatically",
                _ => "UNSAVED — autosave pending"
            }) + (plans.Recovery is PlanRecovery.Rejected or PlanRecovery.Unavailable
                ? "\nStored policy / plans could not be recovered. Safe Do nothing policy and empty plans loaded; original file preserved until an edit." : "");
            retry.Enabled = plans.Health != PlanSaveHealth.Saved;
        }
        policy.SelectedIndexChanged += (_, _) => { plans.SetManualPickPolicy((ManualPickPolicy)policy.SelectedIndex); RenderPolicy(); };
        retry.Click += async (_, _) => { retry.Enabled = false; await plans.SaveLatestAsync(); if (!IsDisposed) RenderPolicy(); };
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) => RenderPolicy();
        FormClosed += (_, _) => timer.Dispose();
        RenderPolicy(); timer.Start();
        layout.Controls.Add(policy, 0, 3);
        layout.Controls.Add(explanation, 0, 4);
        layout.Controls.Add(health, 0, 5);
        layout.Controls.Add(retry, 0, 6);
        layout.Controls.Add(buttons, 0, 7);
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
