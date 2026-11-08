using AFKtor.Core;

namespace AFKtor.Tray;

internal sealed class RolePlansForm : Form
{
    private readonly RolePlanService plans;
    private readonly ChampionCatalog catalog;
    private readonly RolePlanEditor editor;
    private readonly ComboBox roles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, AccessibleName = "Role" };
    private readonly ComboBox champions = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, AccessibleName = "Champion" };
    private readonly Label health = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Button retry = new() { Text = "&Retry saving", AutoSize = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private readonly ListBox picks = new() { Dock = DockStyle.Fill, AccessibleName = "Ordered picks" };
    private readonly ListBox bans = new() { Dock = DockStyle.Fill, AccessibleName = "Ordered bans" };
    private bool retrying;
    private DraftRole Role => (DraftRole)roles.SelectedItem!;

    public RolePlansForm(RolePlanService plans, ChampionCatalog catalog)
    {
        this.plans = plans; this.catalog = catalog; editor = new(plans);
        Text = "Afkter role plans";
        ClientSize = new Size(760, 510);
        MinimumSize = new Size(700, 500);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        roles.Items.AddRange(Enum.GetValues<DraftRole>().Cast<object>().ToArray());
        roles.SelectedIndex = 0;
        var search = new TextBox { Width = 180, PlaceholderText = "Search champion name or ID", AccessibleName = "Search champions" };
        void Filter()
        {
            champions.Items.Clear();
            champions.Items.AddRange(catalog.Champions.Where(c => c.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) ||
                c.Id.ToString().Contains(search.Text, StringComparison.OrdinalIgnoreCase)).Cast<object>().ToArray());
            if (champions.Items.Count > 0) champions.SelectedIndex = 0;
        }
        Filter();
        search.TextChanged += (_, _) => Filter();
        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        top.Controls.Add(new Label { Text = "&Role:", AutoSize = true }); top.Controls.Add(roles);
        top.Controls.Add(search); top.Controls.Add(champions);
        var lists = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.Controls.Add(CreateList("Pick priority", PlanList.Picks, picks), 0, 0);
        lists.Controls.Add(CreateList("Ban priority", PlanList.Bans, bans), 1, 0);
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        footer.Controls.Add(health); footer.Controls.Add(retry);
        var note = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(720, 0),
            Text = "Edits apply immediately and autosave. Highest priority is first. Empty lists mean no planned action.\n" +
                (catalog.Available ? $"Champion identities: Riot Data Dragon {catalog.Version} (bundled; updated with Afkter releases)." :
                "Champion catalog unavailable. Editing is disabled; stored plans are preserved.") +
                (plans.Recovery is PlanRecovery.Rejected or PlanRecovery.Unavailable ?
                "\nStored plans could not be loaded. Effective plans are empty; the original file is preserved until you edit." : "")
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(top, 0, 0); layout.Controls.Add(lists, 0, 1); layout.Controls.Add(note, 0, 2); layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout);
        lists.Enabled = catalog.Available;
        roles.SelectedIndexChanged += (_, _) => RenderLists();
        retry.Click += async (_, _) =>
        {
            retrying = true; retry.Enabled = false;
            try { await plans.SaveLatestAsync(); }
            finally { retrying = false; if (!IsDisposed) RenderHealth(); }
        };
        timer.Tick += (_, _) => RenderHealth();
        timer.Start(); RenderLists(); RenderHealth();
    }

    private Control CreateList(string title, PlanList kind, ListBox list)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8) };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom };
        void Button(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (_, _) => { action(); RenderHealth(); };
            buttons.Controls.Add(button);
        }
        Button("Add", () =>
        {
            if (champions.SelectedItem is Champion champion && editor.Add(Role, kind, champion.Id))
            { RenderLists(); list.SelectedIndex = list.Items.Count - 1; }
        });
        Button("Remove", () =>
        {
            int selected = list.SelectedIndex;
            if (editor.Remove(Role, kind, selected)) { RenderLists(); list.SelectedIndex = Math.Min(selected, list.Items.Count - 1); }
        });
        Button("Up", () => Move(-1)); Button("Down", () => Move(1));
        void Move(int delta)
        {
            int selected = list.SelectedIndex;
            if (editor.Move(Role, kind, selected, selected + delta)) { RenderLists(); list.SelectedIndex = selected + delta; }
        }
        group.Controls.Add(list); group.Controls.Add(buttons);
        return group;
    }

    private void RenderLists()
    {
        var plan = plans.Current.For(Role);
        void Fill(ListBox target, IEnumerable<int> ids)
        {
            target.Items.Clear();
            target.Items.AddRange(ids.Select(id => (object)(catalog.Find(id) ?? new Champion(id, "Unknown champion"))).ToArray());
        }
        Fill(picks, plan.Picks); Fill(bans, plan.Bans);
    }

    private void RenderHealth()
    {
        health.Text = $"Effective revision {plans.Current.Revision} · " + (plans.Health switch
        {
            PlanSaveHealth.Saved => $"Saved (revision {plans.SavedRevision})",
            PlanSaveHealth.SaveFailed => $"UNSAVED — save failed; retrying automatically (disk revision {plans.SavedRevision})",
            _ => "Unsaved — autosave pending"
        });
        retry.Enabled = !retrying && plans.Health != PlanSaveHealth.Saved;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
