using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class RolePlanTests
{
    private static ChampionCatalog Catalog => ChampionCatalog.Parse("""{"version":"16.22.1","data":{"Annie":{"key":"1","name":"Annie"},"Olaf":{"key":"2","name":"Olaf"}}}""");

    // Catches publishing invalid edits, mutable revisions, or losing ordered/empty plans.
    [Test]
    public async Task Complete_edits_publish_immutable_revisions_and_reject_unknown_or_duplicate_champions()
    {
        using var folder = new TemporaryFolder();
        await using var plans = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, startWorker: false);
        int[] ids = [2, 1];
        Assert.That(plans.Replace(DraftRole.Top, PlanList.Picks, ids), Is.True);
        var first = plans.Current;
        ids[0] = 1;
        Assert.That(first.For(DraftRole.Top).Picks, Is.EqualTo(new[] { 2, 1 }));
        Assert.That(plans.Replace(DraftRole.Top, PlanList.Picks, [999]), Is.False);
        Assert.That(plans.Replace(DraftRole.Top, PlanList.Picks, [1, 1]), Is.False);
        Assert.That(plans.Current.Revision, Is.EqualTo(1));
        Assert.That(plans.Replace(DraftRole.Top, PlanList.Picks, [1, 2]), Is.True);
        Assert.That(plans.Replace(DraftRole.Top, PlanList.Picks, []), Is.True);
        Assert.That(plans.Current.Revision, Is.EqualTo(3));
        Assert.That(first.For(DraftRole.Top).Picks, Is.EqualTo(new[] { 2, 1 }));
        foreach (var role in Enum.GetValues<DraftRole>()) Assert.That(plans.Current.For(role).Bans, Is.Empty);
    }

    // Catches reverting effective edits on disk failure and failing to retry latest coherent revision.
    [Test]
    public async Task Failed_save_keeps_latest_plan_and_retry_recovers_across_restart()
    {
        using var folder = new TemporaryFolder();
        Directory.CreateDirectory(folder.File); // A directory in place of the file causes a real write failure.
        await using (var plans = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, startWorker: false))
        {
            plans.Replace(DraftRole.Jungle, PlanList.Bans, [1]);
            await plans.SaveLatestAsync();
            Assert.That(plans.Health, Is.EqualTo(PlanSaveHealth.SaveFailed));
            Assert.That(plans.Current.For(DraftRole.Jungle).Bans, Is.EqualTo(new[] { 1 }));
            plans.Replace(DraftRole.Jungle, PlanList.Bans, [2, 1]);
            Directory.Delete(folder.File);
            await plans.SaveLatestAsync();
            Assert.That(plans.Health, Is.EqualTo(PlanSaveHealth.Saved));
        }
        await using var reopened = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, startWorker: false);
        Assert.That(reopened.Current.Revision, Is.EqualTo(2));
        Assert.That(reopened.Current.For(DraftRole.Jungle).Bans, Is.EqualTo(new[] { 2, 1 }));
    }

    // Catches accepting partially valid corrupted files or arbitrary IDs.
    [TestCase("not-json")]
    [TestCase("{\"SchemaVersion\":1,\"Revision\":4,\"Roles\":[]}")]
    public async Task Invalid_storage_recovers_empty_and_reports_rejection(string json)
    {
        using var folder = new TemporaryFolder();
        File.WriteAllText(folder.File, json);
        await using var plans = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, startWorker: false);
        Assert.That(plans.Recovery, Is.EqualTo(PlanRecovery.Rejected));
        Assert.That(plans.Current.Revision, Is.Zero);
        Assert.That(plans.Current.For(DraftRole.Middle).Picks, Is.Empty);
        Assert.That(File.ReadAllText(folder.File), Is.EqualTo(json));
    }

    [Test]
    public void Catalog_resolves_real_identifiers_and_rejects_bad_data()
    {
        Assert.That(Catalog.Find(1)?.Name, Is.EqualTo("Annie"));
        Assert.That(Catalog.Find(999), Is.Null);
        Assert.Throws<InvalidDataException>(() => ChampionCatalog.Parse("""{"version":"x","data":{"X":{"key":"-1","name":"X"}}}"""));
    }

    [Test]
    public async Task Diagnostics_expose_effective_and_saved_revision_without_plan_payloads()
    {
        using var folder = new TemporaryFolder();
        await using var collector = new AFKtor.Core.Diagnostics.DiagnosticCollector();
        await using var plans = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, collector, startWorker: false);
        plans.Replace(DraftRole.Bottom, PlanList.Picks, [1]);
        Assert.That(collector.GetState().RolePlans?.Revision, Is.EqualTo(1));
        Assert.That(collector.GetState().RolePlans?.Health, Is.EqualTo(PlanSaveHealth.Unsaved));
        await plans.SaveLatestAsync();
        Assert.That(collector.GetState().RolePlans?.SavedRevision, Is.EqualTo(1));
        Assert.That(collector.GetState().RolePlans?.Health, Is.EqualTo(PlanSaveHealth.Saved));
    }

    [Test]
    public async Task Rapid_edits_autosave_the_latest_whole_revision_including_all_roles()
    {
        using var folder = new TemporaryFolder();
        await using var plans = new RolePlanService(new RolePlanFileStore(folder.File), Catalog);
        for (var i = 0; i < 101; i++)
            foreach (var role in Enum.GetValues<DraftRole>())
                plans.Replace(role, PlanList.Picks, i % 2 == 0 ? [2, 1] : [1, 2]);
        Assert.That(plans.Current.Revision, Is.EqualTo(505));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (plans.Health != PlanSaveHealth.Saved) await Task.Delay(20, timeout.Token);
        await using var reopened = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, startWorker: false);
        Assert.That(reopened.Current.Revision, Is.EqualTo(505));
        foreach (var role in Enum.GetValues<DraftRole>())
            Assert.That(reopened.Current.For(role).Picks, Is.EqualTo(new[] { 2, 1 }));
    }

    [Test]
    public async Task Editor_commands_reorder_atomically_and_leave_incomplete_or_invalid_input_unpublished()
    {
        using var folder = new TemporaryFolder();
        await using var plans = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, startWorker: false);
        var editor = new RolePlanEditor(plans);
        Assert.That(editor.Add(DraftRole.Support, PlanList.Bans, 1), Is.True);
        Assert.That(editor.Add(DraftRole.Support, PlanList.Bans, 2), Is.True);
        var before = plans.Current;
        Assert.That(editor.Move(DraftRole.Support, PlanList.Bans, 0, -1), Is.False);
        Assert.That(editor.Add(DraftRole.Support, PlanList.Bans, 999), Is.False);
        Assert.That(editor.Add(DraftRole.Support, PlanList.Bans, 2), Is.False);
        Assert.That(plans.Current.Revision, Is.EqualTo(2));
        Assert.That(editor.Move(DraftRole.Support, PlanList.Bans, 1, 0), Is.True);
        Assert.That(plans.Current.For(DraftRole.Support).Bans, Is.EqualTo(new[] { 2, 1 }));
        Assert.That(before.For(DraftRole.Support).Bans, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(editor.Remove(DraftRole.Support, PlanList.Bans, 1), Is.True);
        Assert.That(editor.Remove(DraftRole.Support, PlanList.Bans, 0), Is.True);
        Assert.That(plans.Current.For(DraftRole.Support).Bans, Is.Empty);
    }

    [Test]
    public void Bundled_catalog_identifies_real_champions_without_League_or_network()
    {
        var catalog = ChampionCatalog.LoadBundled();
        Assert.That(catalog.Available, Is.True);
        Assert.That(catalog.Find(1)?.Name, Is.EqualTo("Annie"));
        Assert.That(catalog.Find(103)?.Name, Is.EqualTo("Ahri"));
    }

    [TestCase("unknown")]
    [TestCase("duplicate")]
    [TestCase("role")]
    [TestCase("schema")]
    [TestCase("revision")]
    public async Task Corrupt_stored_revisions_are_rejected_as_a_whole(string corruption)
    {
        using var folder = new TemporaryFolder();
        await using (var plans = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, startWorker: false))
        {
            plans.Replace(DraftRole.Top, PlanList.Picks, [1]);
            await plans.SaveLatestAsync();
        }
        var json = File.ReadAllText(folder.File);
        json = corruption switch
        {
            "unknown" => json.Replace("\"Picks\":[1]", "\"Picks\":[999]"),
            "duplicate" => json.Replace("\"Picks\":[1]", "\"Picks\":[1,1]"),
            "role" => json.Replace("\"Role\":0", "\"Role\":99"),
            "schema" => json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2"),
            _ => json.Replace("\"Revision\":1", "\"Revision\":-1")
        };
        File.WriteAllText(folder.File, json);
        await using var reopened = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, startWorker: false);
        Assert.That(reopened.Recovery, Is.EqualTo(PlanRecovery.Rejected));
        Assert.That(reopened.Current.For(DraftRole.Top).Picks, Is.Empty);
        Assert.That(reopened.Current.Revision, Is.Zero);
    }

    [Test]
    public async Task Background_retry_recovers_after_storage_becomes_writable()
    {
        using var folder = new TemporaryFolder();
        Directory.CreateDirectory(folder.File);
        await using var plans = new RolePlanService(new RolePlanFileStore(folder.File), Catalog);
        plans.Replace(DraftRole.Middle, PlanList.Picks, [2]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (plans.Health != PlanSaveHealth.SaveFailed) await Task.Delay(20, timeout.Token);
        Assert.That(plans.Current.For(DraftRole.Middle).Picks, Is.EqualTo(new[] { 2 }));
        Directory.Delete(folder.File);
        while (plans.Health != PlanSaveHealth.Saved) await Task.Delay(20, timeout.Token);
        await using var reopened = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, startWorker: false);
        Assert.That(reopened.Current.For(DraftRole.Middle).Picks, Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public async Task Edit_during_slow_save_takes_effect_immediately_and_next_save_persists_latest_revision()
    {
        using var folder = new TemporaryFolder();
        var store = new PausedStore(new RolePlanFileStore(folder.File));
        await using var plans = new RolePlanService(store, Catalog, startWorker: false);
        plans.Replace(DraftRole.Top, PlanList.Picks, [1]);
        var firstSave = plans.SaveLatestAsync();
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            plans.Replace(DraftRole.Top, PlanList.Picks, [2, 1]);
            Assert.That(plans.Current.Revision, Is.EqualTo(2));
            Assert.That(plans.Current.For(DraftRole.Top).Picks, Is.EqualTo(new[] { 2, 1 }));
            Assert.That(plans.Health, Is.EqualTo(PlanSaveHealth.Unsaved));
        }
        finally { store.Release.TrySetResult(); }
        await firstSave;
        Assert.That(plans.SavedRevision, Is.EqualTo(1));
        Assert.That(plans.Health, Is.EqualTo(PlanSaveHealth.Unsaved));
        await plans.SaveLatestAsync();
        await using var reopened = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, startWorker: false);
        Assert.That(reopened.Current.Revision, Is.EqualTo(2));
        Assert.That(reopened.Current.For(DraftRole.Top).Picks, Is.EqualTo(new[] { 2, 1 }));
    }

    // Catches silently accepting an unknown stored policy and enabling its default watchdog.
    [Test]
    public async Task Unknown_stored_policy_rejects_complete_revision_and_preserves_evidence()
    {
        using var folder = new TemporaryFolder();
        await using (var plans = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, startWorker: false))
        {
            plans.Replace(DraftRole.Top, PlanList.Picks, [1]);
            await plans.SaveLatestAsync();
        }
        var stored = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(folder.File))!;
        stored["ManualPickPolicy"] = 99;
        var json = stored.ToJsonString();
        File.WriteAllText(folder.File, json);
        await using var reopened = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, startWorker: false);
        Assert.That(reopened.Recovery, Is.EqualTo(PlanRecovery.Rejected));
        Assert.That(reopened.Current.For(DraftRole.Top).Picks, Is.Empty);
        Assert.That(File.ReadAllText(folder.File), Is.EqualTo(json));
    }

    // Catches treating explicit malformed/null policy as an absent legacy setting and enabling a watchdog.
    [TestCase("null")]
    [TestCase("\"UseRolePlan\"")]
    [TestCase("1.5")]
    public async Task Invalid_policy_shape_recovers_safely_and_visibly(string policy)
    {
        using var folder = new TemporaryFolder();
        var json = "{\"SchemaVersion\":1,\"Revision\":0,\"Roles\":[{\"Role\":0,\"Picks\":[],\"Bans\":[]},{\"Role\":1,\"Picks\":[],\"Bans\":[]},{\"Role\":2,\"Picks\":[],\"Bans\":[]},{\"Role\":3,\"Picks\":[],\"Bans\":[]},{\"Role\":4,\"Picks\":[],\"Bans\":[]}],\"ManualPickPolicy\":" + policy + "}";
        File.WriteAllText(folder.File, json);
        await using var plans = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, startWorker: false);
        Assert.That(plans.Recovery, Is.EqualTo(PlanRecovery.Rejected));
        Assert.That(plans.Current.ManualPickPolicy, Is.EqualTo(ManualPickPolicy.DoNothing));
        Assert.That(File.ReadAllText(folder.File), Is.EqualTo(json));
    }

    // Catches dropping live policy from diagnostics, serial persistence or subsequent plan edits.
    [TestCase(ManualPickPolicy.LockCurrentHover)]
    [TestCase(ManualPickPolicy.UseRolePlan)]
    [TestCase(ManualPickPolicy.DoNothing)]
    public async Task Policy_is_live_immutable_diagnostic_and_saved_with_plans(ManualPickPolicy policy)
    {
        using var folder = new TemporaryFolder();
        await using var collector = new AFKtor.Core.Diagnostics.DiagnosticCollector();
        await using var plans = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, collector, startWorker: false);
        var original = plans.Current;
        plans.SetManualPickPolicy(policy);
        plans.Replace(DraftRole.Top, PlanList.Picks, [2, 1]);
        Assert.That(original.ManualPickPolicy, Is.EqualTo(ManualPickPolicy.LockCurrentHover));
        Assert.That(plans.Current.ManualPickPolicy, Is.EqualTo(policy));
        var json = System.Text.Json.JsonSerializer.SerializeToElement(collector.GetState().RolePlans);
        Assert.That(json.TryGetProperty("ManualPickPolicy", out var value), Is.True);
        Assert.That(value.GetInt32(), Is.EqualTo((int)policy));
        Assert.That(json.GetProperty("PolicyExplanation").GetString(), Does.Contain(policy == ManualPickPolicy.UseRolePlan ? "replacing" : policy == ManualPickPolicy.DoNothing ? "no further" : "never replaced"));
        await plans.SaveLatestAsync();
        await using var reopened = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, startWorker: false);
        Assert.That(reopened.Current.ManualPickPolicy, Is.EqualTo(policy));
        Assert.That(reopened.Current.For(DraftRole.Top).Picks, Is.EqualTo(new[] { 2, 1 }));
    }

    // Catches incompatibility with existing v1 files and silent rejection of old ordered plans.
    [Test]
    public async Task V1_plan_without_policy_recovers_ordered_plans_and_default_watchdog()
    {
        using var folder = new TemporaryFolder();
        File.WriteAllText(folder.File, """{"SchemaVersion":1,"Revision":7,"Roles":[{"Role":0,"Picks":[2,1],"Bans":[]},{"Role":1,"Picks":[],"Bans":[]},{"Role":2,"Picks":[],"Bans":[]},{"Role":3,"Picks":[],"Bans":[]},{"Role":4,"Picks":[],"Bans":[]}]}""");
        await using var plans = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, startWorker: false);
        Assert.That(plans.Recovery, Is.EqualTo(PlanRecovery.Loaded));
        Assert.That(plans.Current.Revision, Is.EqualTo(7));
        Assert.That(plans.Current.ManualPickPolicy, Is.EqualTo(ManualPickPolicy.LockCurrentHover));
        Assert.That(plans.Current.For(DraftRole.Top).Picks, Is.EqualTo(new[] { 2, 1 }));
        plans.SetManualPickPolicy(ManualPickPolicy.DoNothing); await plans.SaveLatestAsync();
        Assert.That(new RolePlanFileStore(folder.File).Load(Catalog).Plan.ManualPickPolicy, Is.EqualTo(ManualPickPolicy.DoNothing));
    }

    // Catches a failed policy save reverting effective policy or a stale revision starting after a policy edit.
    [Test]
    public async Task Policy_edit_invalidates_dispatch_and_failed_save_retries_latest_policy()
    {
        using var folder = new TemporaryFolder(); Directory.CreateDirectory(folder.File);
        await using var plans = new RolePlanService(new RolePlanFileStore(folder.File), Catalog, startWorker: false);
        var old = plans.Current.Revision;
        Assert.That(plans.SetManualPickPolicy(ManualPickPolicy.UseRolePlan), Is.True);
        Assert.That(plans.SetManualPickPolicy((ManualPickPolicy)99), Is.False);
        var started = false;
        Assert.That(plans.StartIfCurrent(old, () => { started = true; return Task.FromResult(1); }), Is.Null);
        Assert.That(started, Is.False);
        await plans.SaveLatestAsync();
        Assert.That(plans.Health, Is.EqualTo(PlanSaveHealth.SaveFailed));
        Assert.That(plans.Current.ManualPickPolicy, Is.EqualTo(ManualPickPolicy.UseRolePlan));
        plans.SetManualPickPolicy(ManualPickPolicy.DoNothing);
        Directory.Delete(folder.File); await plans.SaveLatestAsync();
        Assert.That(new RolePlanFileStore(folder.File).Load(Catalog).Plan.ManualPickPolicy, Is.EqualTo(ManualPickPolicy.DoNothing));
    }

    private sealed class PausedStore(RolePlanFileStore inner) : IRolePlanStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public (RolePlanRevision Plan, PlanRecovery Recovery) Load(ChampionCatalog catalog) => inner.Load(catalog);
        public void Save(RolePlanRevision revision)
        {
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            inner.Save(revision);
        }
    }

    private sealed class TemporaryFolder : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public TemporaryFolder() => Directory.CreateDirectory(directory);
        public string File => Path.Combine(directory, "plans.json");
        public void Dispose() => Directory.Delete(directory, true);
    }
}
