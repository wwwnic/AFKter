using AFKtor.Core;

namespace AFKtor.Tray;

internal sealed class SettingsForm : Form
{
    public SettingsForm(RolePlanService plans, ReadyCheckController controller)
    {
        Text = "Afkter settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(590, 640);
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(12) };
        var fields = new Dictionary<TimingField, TextBox>();
        var errors = new ErrorProvider { ContainerControl = this };
        bool rendering = false;
        int Value(TimingField field) => field switch
        {
            TimingField.AcceptDelay => plans.Current.Timing.AcceptDelaySeconds,
            TimingField.ActionPacing => plans.Current.Timing.ActionPacingMilliseconds,
            TimingField.HoverLock => plans.Current.Timing.HoverLockMilliseconds,
            TimingField.Urgent => plans.Current.Timing.UrgentMilliseconds,
            _ => plans.Current.Timing.MinimumDispatchMilliseconds
        };
        void AddField(Control parent, TimingField field, string name)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            row.Controls.Add(new Label { Text = name, Width = 390, AutoSize = false, Height = 28 });
            var input = new TextBox { Width = 85, Text = Value(field).ToString(), AccessibleName = name };
            fields.Add(field, input);
            input.TextChanged += (_, _) =>
            {
                if (rendering) return;
                var valid = plans.EditTiming(field, input.Text) || input.Text == Value(field).ToString();
                errors.SetError(input, valid ? "" : "Use a whole number in the displayed range; urgent time must exceed the request safety margin. Effective settings have not changed.");
            };
            row.Controls.Add(input); parent.Controls.Add(row);
        }
        AddField(layout, TimingField.AcceptDelay, "Accept delay (seconds, 0–30):");
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(540, 0), Text =
            "Valid changes apply immediately and autosave. Delay changes use the original check detection time; 0 verifies immediately. Skipped checks stay skipped. A sent request cannot be recalled." });
        layout.Controls.Add(new Label { Text = "After a manual pick change in League:", AutoSize = true, Margin = new Padding(3, 15, 3, 3) });
        var policy = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 530,
            AccessibleName = "Deadline behavior after a manual pick change" };
        policy.Items.AddRange(["Lock current hover (default)", "Use my role plan", "Do nothing"]);
        policy.SelectedIndex = (int)plans.Current.ManualPickPolicy;
        var explanation = new Label { AutoSize = true, MaximumSize = new Size(540, 0) };
        policy.SelectedIndexChanged += (_, _) => plans.SetManualPickPolicy((ManualPickPolicy)policy.SelectedIndex);
        layout.Controls.Add(policy); layout.Controls.Add(explanation);
        var toggle = new CheckBox { Text = "Show advanced timing", AutoSize = true, Margin = new Padding(3, 15, 3, 3) };
        var advanced = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Visible = false };
        AddField(advanced, TimingField.ActionPacing, "Action pacing (ms, 0–5000):");
        AddField(advanced, TimingField.HoverLock, "Hover to lock (ms, 0–5000):");
        AddField(advanced, TimingField.Urgent, "Urgent threshold (ms, 1000–10000):");
        AddField(advanced, TimingField.MinimumDispatch, "Request safety margin (ms, 250–1000):");
        advanced.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(530, 0), Text =
            "Normal actions wait from the original action / hover start. Urgent actions skip pacing and hover waits; the safety margin always prevents dispatch too close to expiry. Urgent time must exceed the safety margin. These are best-effort engineering bounds, not measured League latency guarantees." });
        toggle.CheckedChanged += (_, _) => advanced.Visible = toggle.Checked;
        layout.Controls.Add(toggle); layout.Controls.Add(advanced);
        var reset = new Button { Text = "Restore author timing defaults", AutoSize = true };
        reset.Click += (_, _) =>
        {
            plans.ResetTiming(); rendering = true;
            foreach (var (field, input) in fields) { input.Text = Value(field).ToString(); errors.SetError(input, ""); }
            rendering = false;
        };
        layout.Controls.Add(reset);
        var health = new Label { AutoSize = true, MaximumSize = new Size(540, 0) };
        var countdown = new Label { AutoSize = true, MaximumSize = new Size(540, 0) };
        var retry = new Button { Text = "Retry saving settings / plans", AutoSize = true };
        retry.Click += async (_, _) => { retry.Enabled = false; await plans.SaveLatestAsync(); };
        void Render()
        {
            explanation.Text = ManualPickPolicies.Explanation(plans.Current.ManualPickPolicy);
            health.Text = $"Effective settings / plans revision {plans.Current.Revision}; saved revision {plans.SavedRevision}. " +
                (plans.Health switch { PlanSaveHealth.Saved => "Saved", PlanSaveHealth.SaveFailed => "UNSAVED — save failed; retrying automatically", _ => "UNSAVED — autosave pending" }) +
                (plans.Recovery is PlanRecovery.Rejected or PlanRecovery.Unavailable ? "\nStored settings / plans could not be recovered. Safe defaults loaded; original file preserved until an edit." : "");
            countdown.Text = controller.Status + (controller.Remaining is { } remaining ? $" — {remaining.TotalSeconds:0.0}s remaining" : "");
            retry.Enabled = plans.Health != PlanSaveHealth.Saved;
        }
        layout.Controls.Add(health); layout.Controls.Add(countdown); layout.Controls.Add(retry);
        var close = new Button { Text = "Close", AutoSize = true }; close.Click += (_, _) => Close();
        layout.Controls.Add(close); CancelButton = close;
        var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) => Render(); FormClosed += (_, _) => { timer.Dispose(); errors.Dispose(); };
        Render(); timer.Start(); Controls.Add(layout);
    }
}
