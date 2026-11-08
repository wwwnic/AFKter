using System.Collections.Immutable;
using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class PlannedBanTests
{
    // Catches dispatching a protected/unavailable candidate, ignoring the latest edit, or restarting pacing.
    [Test]
    public async Task PacesThenVerifiesLatestPlanAndIntentBeforeCompleting()
    {
        var clock = new Clock(); var observer = Admitted();
        var plan = Plan(86, 1, 2, 3);
        var client = new Client { Snapshot = DraftObserverTests.Draft() };
        var bans = new PlannedBanExecutor(observer, () => plan, true, clock);
        await bans.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
        clock.Advance(1100);
        plan = Plan(1, 2, 3) with { Revision = 2 };
        client.Available = [2, 3];
        client.Snapshot = client.Snapshot with { Players = [new(2, DraftRole.Middle, 0, 0), new(3, DraftRole.Top, 0, 2)] };
        await bans.RefreshAsync(client, DraftObserverTests.Draft(), default);
        Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 3) }));
        Assert.That(bans.State.Revision, Is.EqualTo(2));
        Assert.That(bans.State.Status, Is.EqualTo(BanStatus.AwaitingOutcome));
        client.Snapshot = client.Snapshot with { Actions = [new(11, 2, DraftActionKind.Ban, 3, false, true)] };
        await bans.RefreshAsync(client, client.Snapshot, default);
        Assert.That(bans.State.Status, Is.EqualTo(BanStatus.Completed));
        Assert.That(client.Writes, Has.Count.EqualTo(1));
    }

    // Catches removing the native readiness gate or blocking all enabled execution.
    [TestCase(false, 0, BanStatus.NativeVerificationRequired)]
    [TestCase(true, 1, BanStatus.AwaitingOutcome)]
    public async Task NativeGateAndUrgency(bool verified, int writes, BanStatus status)
    {
        var observer = Admitted(); var client = new Client { Snapshot = DraftObserverTests.Draft(remaining: 1500) };
        var bans = new PlannedBanExecutor(observer, () => Plan(1), verified, new Clock());
        await bans.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Has.Count.EqualTo(writes));
        Assert.That(bans.State.Status, Is.EqualTo(status));
    }

    // Catches treating a teammate's ban hover as protected play intent, or ignoring completed bans.
    [Test]
    public async Task BanHoverDoesNotProtectButCompletedBanDoes()
    {
        var observer = Admitted(); var client = new Client { Snapshot = DraftObserverTests.Draft(remaining: 1500) };
        client.Snapshot = client.Snapshot with { Players = [new(2, DraftRole.Middle, 0, 0), new(3, DraftRole.Top, 0, 0)],
            Actions = [new(11, 2, DraftActionKind.Ban, 0, true, false), new(12, 3, DraftActionKind.Ban, 86, true, false), new(13, 4, DraftActionKind.Ban, 1, false, true)] };
        var bans = new PlannedBanExecutor(observer, () => Plan(1, 86), true, new Clock());
        await bans.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 86) }));
    }

    // Catches manual selection being overwritten later in this draft, including a replacement window.
    [Test]
    public async Task ManualSelectionEndsBansForWholeDraft()
    {
        var observer = Admitted(); var client = new Client { Snapshot = DraftObserverTests.Draft(remaining: 1500) };
        var bans = new PlannedBanExecutor(observer, () => Plan(1), true, new Clock());
        await bans.RefreshAsync(client, client.Snapshot with { Actions = [new(11, 2, DraftActionKind.Ban, 2, true, false)] }, default);
        await bans.RefreshAsync(client, client.Snapshot, default);
        client.Snapshot = client.Snapshot with { Actions = [new(19, 2, DraftActionKind.Ban, 0, true, false)] };
        await bans.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
        Assert.That(bans.State.Status, Is.EqualTo(BanStatus.ManualControl));
    }

    // Catches blind retries after an ambiguous transport outcome or a still-empty acknowledgment.
    [Test]
    public async Task AmbiguousRequestIsReconciledWithoutRetry()
    {
        var observer = Admitted(); var client = new Client { Snapshot = DraftObserverTests.Draft(remaining: 1500), Outcome = BanRequestOutcome.Ambiguous };
        var bans = new PlannedBanExecutor(observer, () => Plan(1), true, new Clock());
        await bans.RefreshAsync(client, client.Snapshot, default);
        await bans.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Has.Count.EqualTo(1));
        Assert.That(bans.State.Status, Is.EqualTo(BanStatus.AwaitingOutcome));
        client.Snapshot = client.Snapshot with { Actions = [new(11, 2, DraftActionKind.Ban, 2, true, false)] };
        await bans.RefreshAsync(client, client.Snapshot, default);
        Assert.That(bans.State.Status, Is.EqualTo(BanStatus.ManualControl));
    }

    // Catches stale final reads escaping disarm/action replacement/expiry, or exhaustion becoming permanent.
    [TestCase("disarm")]
    [TestCase("replace")]
    [TestCase("expired")]
    [TestCase("exhausted")]
    public async Task FinalVerificationFailsClosed(string change)
    {
        var observer = Admitted(); var client = new Client { Snapshot = DraftObserverTests.Draft(remaining: 1500) };
        var bans = new PlannedBanExecutor(observer, () => Plan(1), true, new Clock());
        client.OnRead = () => {
            if (change == "disarm") observer.SetEnabled(false);
            if (change == "replace") client.Snapshot = client.Snapshot! with { Actions = [new(19, 2, DraftActionKind.Ban, 0, true, false)] };
            if (change == "expired") client.Snapshot = client.Snapshot! with { TimeLeftMilliseconds = 0 };
            if (change == "exhausted") client.Available = [];
        };
        if (change == "exhausted") client.Available = [];
        await bans.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
        if (change == "exhausted") {
            client.OnRead = null; client.Available = [1];
            await bans.RefreshAsync(client, client.Snapshot, default);
            Assert.That(client.Writes, Has.Count.EqualTo(1));
        }
    }

    // Catches an edit committed after a decision sending the superseded champion.
    [Test]
    public async Task PlanPublicationAndRequestStartShareRevisionGuard()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ban-plan-{Guid.NewGuid():N}.json");
        try
        {
            await using var plans = new RolePlanService(new RolePlanFileStore(path), ChampionCatalog.LoadBundled(), startWorker: false);
            plans.Replace(DraftRole.Middle, PlanList.Bans, [1]);
            var client = new Client { Snapshot = DraftObserverTests.Draft(remaining: 1500) };
            var bans = new PlannedBanExecutor(Admitted(), () => plans.Current, true, new Clock(),
                startIfCurrent: (revision, start) => {
                    plans.Replace(DraftRole.Middle, PlanList.Bans, [2]);
                    return plans.StartIfCurrent(revision, start);
                });
            await bans.RefreshAsync(client, client.Snapshot, default);
            Assert.That(client.Writes, Is.Empty);
            var current = plans.StartIfCurrent(plans.Current.Revision, () => client.CompleteBanAsync(11, 2, default));
            Assert.That(current, Is.Not.Null);
            await current!;
            Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 2) }));
        }
        finally { File.Delete(path); }
    }

    // Catches unbounded skipped-candidate diagnostics or losing why an exhausted plan did nothing.
    [Test]
    public async Task ExhaustedDecisionKeepsBoundedSkipEvidence()
    {
        await using var diagnostics = new AFKtor.Core.Diagnostics.DiagnosticCollector();
        var client = new Client { Snapshot = DraftObserverTests.Draft(remaining: 1500), Available = [] };
        var bans = new PlannedBanExecutor(Admitted(), () => Plan(Enumerable.Range(1, 1000).ToArray()), true, new Clock(), diagnostics);
        await bans.RefreshAsync(client, client.Snapshot, default);
        Assert.That(bans.State.Status, Is.EqualTo(BanStatus.Exhausted));
        Assert.That(bans.State.SkippedCount, Is.EqualTo(1000));
        Assert.That(bans.State.Skipped!.All(s => s.Reason == BanSkipReason.Unavailable), Is.True);
        Assert.That(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(diagnostics.GetMemoryHistory().Events[^1]).Length,
            Is.LessThanOrEqualTo(AFKtor.Core.Diagnostics.DiagnosticLimits.EventBytes));
        Assert.That(client.Writes, Is.Empty);
    }

    // Catches overlapping writes, edits replaying an in-flight request, or counting HTTP acceptance as completion.
    [Test]
    public async Task ConcurrentRefreshesAndInFlightEditsDoNotDuplicateRequest()
    {
        var observer = Admitted(); var plan = Plan(1);
        var pending = new TaskCompletionSource<BanRequestOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Snapshot = DraftObserverTests.Draft(remaining: 1500),
            WriteOverride = _ => { started.SetResult(); return pending.Task; } };
        var bans = new PlannedBanExecutor(observer, () => plan, true, new Clock());
        var first = bans.RefreshAsync(client, client.Snapshot, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        plan = Plan(2) with { Revision = 2 };
        var second = bans.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Has.Count.EqualTo(1));
        pending.SetResult(BanRequestOutcome.Accepted);
        await Task.WhenAll(first, second);
        Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 1) }));
        Assert.That(bans.State.Status, Is.EqualTo(BanStatus.AwaitingOutcome));
    }

    // Catches a late authoritative read from an invalid lifetime writing after cancellation.
    [TestCase("disarm")]
    [TestCase("disconnect")]
    [TestCase("replace")]
    [TestCase("end")]
    public async Task InvalidatedFinalReadCannotDispatch(string change)
    {
        var observer = Admitted();
        var pending = new TaskCompletionSource<DraftSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured = default;
        var client = new Client { Snapshot = DraftObserverTests.Draft(remaining: 1500),
            ReadOverride = token => { captured = token; started.SetResult(); return pending.Task; } };
        var bans = new PlannedBanExecutor(observer, () => Plan(1), true, new Clock());
        var refresh = bans.RefreshAsync(client, client.Snapshot, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (change == "disarm") observer.SetEnabled(false);
        if (change == "disconnect") observer.Disconnect();
        if (change == "replace") observer.Observe(observer.State.Generation, 3,
            client.Snapshot with { Actions = [new(19, 2, DraftActionKind.Ban, 0, true, false)] });
        if (change == "end") observer.Observe(observer.State.Generation, 3, null);
        Assert.That(captured.IsCancellationRequested, Is.True);
        pending.SetResult(client.Snapshot);
        await refresh;
        Assert.That(client.Writes, Is.Empty);
        Assert.That(bans.State.Status, Is.EqualTo(BanStatus.Cancelled));
    }

    // Catches manual takeover at final verification or carrying that takeover into a genuinely new draft.
    [Test]
    public async Task FinalManualSelectionYieldsUntilNextDraft()
    {
        var observer = Admitted(); var client = new Client { Snapshot = DraftObserverTests.Draft(remaining: 1500) };
        var initial = client.Snapshot;
        client.OnRead = () => client.Snapshot = initial with { Actions = [new(11, 2, DraftActionKind.Ban, 2, true, false)] };
        var bans = new PlannedBanExecutor(observer, () => Plan(1), true, new Clock());
        await bans.RefreshAsync(client, initial, default);
        Assert.That(client.Writes, Is.Empty);
        Assert.That(bans.State.Status, Is.EqualTo(BanStatus.ManualControl));
        observer.Observe(observer.State.Generation, 3, null);
        client.Snapshot = DraftObserverTests.Draft(102, remaining: 1500); client.OnRead = null;
        observer.Observe(observer.State.Generation, 4, client.Snapshot);
        await bans.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 1) }));
    }

    // Catches trusting a deadline after verification consumed its remaining monotonic budget.
    [Test]
    public async Task VerificationLatencyCannotDispatchPastDeadline()
    {
        var clock = new Clock(); var client = new Client { Snapshot = DraftObserverTests.Draft(remaining: 1500) };
        client.OnRead = () => clock.Advance(1500);
        var bans = new PlannedBanExecutor(Admitted(), () => Plan(1), true, clock);
        await bans.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
        Assert.That(bans.State.Status, Is.EqualTo(BanStatus.Cancelled));
    }

    // Catches dropping authoritative ban-list exclusions when completed actions are absent.
    [Test]
    public async Task BanListsExcludeBothTeamsAlreadyBannedChampions()
    {
        var client = new Client { Snapshot = DraftObserverTests.Draft(remaining: 1500) with { BannedChampions = [1, 2] } };
        var bans = new PlannedBanExecutor(Admitted(), () => Plan(1, 2, 3), true, new Clock());
        await bans.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 3) }));
        Assert.That(bans.State.Skipped!.Select(s => s.Reason), Is.EqualTo(new[] { BanSkipReason.AlreadyBanned, BanSkipReason.AlreadyBanned }));
    }

    internal static DraftObserver Admitted()
    {
        var observer = new DraftObserver(); observer.SetEnabled(true); var generation = observer.BeginSession();
        observer.Observe(generation, 1, null); observer.Observe(generation, 2, DraftObserverTests.Draft()); return observer;
    }
    // Catches resetting original pacing on an edit, ignoring new settings at verification, and timer-reset revival.
    [Test]
    public async Task LiveBanTimingReevaluatesPacingAndNeverRevivesAnExpiredAction()
    {
        var clock = new Clock(); var client = new Client { Snapshot = DraftObserverTests.Draft() };
        var plan = Plan(1) with { Timing = new(3, 5000, 5000, 3000, 250) };
        var bans = new PlannedBanExecutor(Admitted(), () => plan, true, clock);
        await bans.RefreshAsync(client, client.Snapshot, default); clock.Advance(2000);
        await bans.RefreshAsync(client, client.Snapshot, default); Assert.That(client.Writes, Is.Empty);
        plan = plan with { Revision = 2, Timing = new(3, 0, 0, 3000, 250) };
        client.Snapshot = client.Snapshot! with { TimeLeftMilliseconds = 0 };
        await bans.RefreshAsync(client, client.Snapshot, default);
        client.Snapshot = client.Snapshot! with { TimeLeftMilliseconds = 28000 };
        await bans.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
    }

    [Test]
    public async Task FinalReadReevaluatesLiveTimingBeforeBanDispatch()
    {
        var client = new Client { Snapshot = DraftObserverTests.Draft(remaining: 2000) }; var plan = Plan(1);
        client.OnRead = () => plan = plan with { Revision = 2, Timing = new(3, 5000, 5000, 1000, 250) };
        var bans = new PlannedBanExecutor(Admitted(), () => plan, true, new Clock());
        await bans.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
    }

    internal static RolePlanRevision Plan(params int[] ids) => new(1, RolePlanRevision.Empty.Roles.SetItem(DraftRole.Middle, new([], [..ids])));
    internal sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ticks;
        public void Advance(long milliseconds) => ticks += milliseconds;
    }
    internal sealed class Client : IBanDraftClient
    {
        public DraftSnapshot? Snapshot { get; set; }
        public HashSet<int> Available { get; set; } = [1, 2, 3, 86];
        public Action? OnRead { get; set; }
        public Func<CancellationToken, Task<DraftSnapshot?>>? ReadOverride { get; set; }
        public Func<CancellationToken, Task<BanRequestOutcome>>? WriteOverride { get; set; }
        public List<(long, int)> Writes { get; } = [];
        public BanRequestOutcome Outcome { get; set; } = BanRequestOutcome.Accepted;
        public Task<DraftSnapshot?> ReadAsync(CancellationToken cancellationToken) { OnRead?.Invoke(); return ReadOverride?.Invoke(cancellationToken) ?? Task.FromResult(Snapshot); }
        public Task<IReadOnlySet<int>> ReadBannableAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<int>>(Available);
        public Task<BanRequestOutcome> CompleteBanAsync(long actionId, int championId, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Writes.Add((actionId, championId)); return WriteOverride?.Invoke(cancellationToken) ?? Task.FromResult(Outcome); }
    }
}
