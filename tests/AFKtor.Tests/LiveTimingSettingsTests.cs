using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class LiveTimingSettingsTests
{
    // Catches partial reset/publication, invalid input leaking into dispatch, and lost settings on list edits.
    [Test]
    public async Task Timing_edits_publish_whole_revisions_and_reset_once_without_changing_plans_or_policy()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            await using var plans = new RolePlanService(new RolePlanFileStore(path), ChampionCatalog.LoadBundled(), startWorker: false);
            plans.Replace(DraftRole.Top, PlanList.Picks, [1]);
            plans.SetManualPickPolicy(ManualPickPolicy.DoNothing);
            Assert.That(plans.SetTiming(new(30, 5000, 5000, 10000, 1000)), Is.True);
            var old = plans.Current;
            Assert.That(plans.SetTiming(new(31, 0, 0, 1000, 250)), Is.False);
            Assert.That(plans.SetTiming(new(0, 0, 0, 1000, 1000)), Is.False);
            Assert.That(plans.Current, Is.SameAs(old));
            Assert.That(plans.ResetTiming(), Is.True);
            Assert.That(plans.Current.Revision, Is.EqualTo(4));
            Assert.That(plans.Current.Timing, Is.EqualTo(TimingSettings.Default));
            Assert.That(plans.Current.For(DraftRole.Top).Picks, Is.EqualTo(new[] { 1 }));
            Assert.That(plans.Current.ManualPickPolicy, Is.EqualTo(ManualPickPolicy.DoNothing));
            Assert.That(old.Timing.AcceptDelaySeconds, Is.EqualTo(30));
            await plans.SaveLatestAsync();
            Assert.That(new RolePlanFileStore(path).Load(ChampionCatalog.LoadBundled()).Plan.Timing, Is.EqualTo(TimingSettings.Default));
        }
        finally { File.Delete(path); }
    }

    // Catches unfinished numeric text publishing a zero/partial setting or overwriting another field.
    [TestCase("")][TestCase("-")][TestCase("1.5")][TestCase("30001")][TestCase(" 1")]
    public async Task Invalid_transient_input_keeps_effective_revision(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            await using var plans = new RolePlanService(new RolePlanFileStore(path), ChampionCatalog.LoadBundled(), startWorker: false);
            var before = plans.Current;
            Assert.That(plans.EditTiming(TimingField.AcceptDelay, text), Is.False);
            Assert.That(plans.Current, Is.SameAs(before));
            Assert.That(plans.EditTiming(TimingField.AcceptDelay, "0"), Is.True);
            Assert.That(plans.Current.Timing.AcceptDelaySeconds, Is.Zero);
            Assert.That(plans.Current.Timing.ActionPacingMilliseconds, Is.EqualTo(1000));
        }
        finally { File.Delete(path); }
    }

    // Catches effective settings reverting on storage failure and an older reset save masking newer edits.
    [Test]
    public async Task Failed_timing_save_retries_latest_coherent_reset_and_edits()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "plans.json"); Directory.CreateDirectory(path);
        try
        {
            await using var plans = new RolePlanService(new RolePlanFileStore(path), ChampionCatalog.LoadBundled(), startWorker: false);
            plans.SetTiming(new(30, 5000, 5000, 10000, 1000)); await plans.SaveLatestAsync();
            Assert.That(plans.Health, Is.EqualTo(PlanSaveHealth.SaveFailed));
            Assert.That(plans.Current.Timing.AcceptDelaySeconds, Is.EqualTo(30));
            plans.ResetTiming(); plans.EditTiming(TimingField.AcceptDelay, "0");
            Directory.Delete(path); await plans.SaveLatestAsync();
            var loaded = new RolePlanFileStore(path).Load(ChampionCatalog.LoadBundled()).Plan;
            Assert.That(loaded.Revision, Is.EqualTo(3));
            Assert.That(loaded.Timing, Is.EqualTo(new TimingSettings(0, 1000, 1000, 3000, 250)));
            Assert.That(plans.SavedRevision, Is.EqualTo(3));
        }
        finally { Directory.Delete(directory, true); }
    }

    // Catches malformed timing being treated as an absent legacy field and publishing partial settings.
    [TestCase("null")][TestCase("{}")][TestCase("{\"AcceptDelaySeconds\":31}")]
    [TestCase("{\"AcceptDelaySeconds\":0,\"ActionPacingMilliseconds\":0,\"HoverLockMilliseconds\":0,\"UrgentMilliseconds\":1000,\"MinimumDispatchMilliseconds\":1000}")]
    public async Task Malformed_timing_rejects_complete_file_without_overwriting_it(string timing)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var json = "{\"SchemaVersion\":1,\"Revision\":7,\"Roles\":[{\"Role\":0,\"Picks\":[1],\"Bans\":[]},{\"Role\":1,\"Picks\":[],\"Bans\":[]},{\"Role\":2,\"Picks\":[],\"Bans\":[]},{\"Role\":3,\"Picks\":[],\"Bans\":[]},{\"Role\":4,\"Picks\":[],\"Bans\":[]}],\"Timing\":" + timing + "}";
            File.WriteAllText(path, json);
            await using var plans = new RolePlanService(new RolePlanFileStore(path), ChampionCatalog.LoadBundled(), startWorker: false);
            Assert.That(plans.Recovery, Is.EqualTo(PlanRecovery.Rejected));
            Assert.That(plans.Current.ManualPickPolicy, Is.EqualTo(ManualPickPolicy.DoNothing));
            Assert.That(plans.Current.For(DraftRole.Top).Picks, Is.Empty);
            Assert.That(File.ReadAllText(path), Is.EqualTo(json));
        }
        finally { File.Delete(path); }
    }

    // Catches forgetting legacy delay, dropping old policy/plans, or letting legacy delay overwrite a new saved revision.
    [Test]
    public async Task Legacy_delay_migrates_and_new_saved_timing_takes_precedence()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "plans.json"); var legacy = Path.Combine(directory, "settings.json");
        try
        {
            new AcceptDelaySettingsStore(legacy).Save(new(17));
            await using (var plans = new RolePlanService(new RolePlanFileStore(path, legacy), ChampionCatalog.LoadBundled(), startWorker: false))
            {
                Assert.That(plans.Current.Timing.AcceptDelaySeconds, Is.EqualTo(17));
                plans.SetTiming(new(0, 0, 0, 1000, 250));
                plans.Replace(DraftRole.Top, PlanList.Picks, [1]);
                await plans.SaveLatestAsync();
            }
            Assert.That(new RolePlanFileStore(path, legacy).Load(ChampionCatalog.LoadBundled()).Plan.Timing.AcceptDelaySeconds, Is.Zero);
        }
        finally { Directory.Delete(directory, true); }
    }
}
