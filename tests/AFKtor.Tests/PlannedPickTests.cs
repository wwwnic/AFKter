using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class PlannedPickTests
{
    // Catches missing hover/lock pacing, treating own acknowledgment as takeover, and resetting the lock clock.
    [Test]
    public async Task OrderedHoverThenAcknowledgedLockUsesOriginalClock()
    {
        var clock = new PlannedBanTests.Clock(); var client = new Client(); var plan = Plan(86, 1, 2);
        client.Available = [1, 2];
        var picks = new PlannedPickExecutor(Observer(), () => plan, true, clock);
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
        clock.Advance(1000);
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 1, false) }));
        client.Hover(1);
        clock.Advance(1000);
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 1, false), (11L, 1, true) }));
        client.Hover(1, true);
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(picks.State.Status, Is.EqualTo(PickStatus.Completed));
    }

    // Catches substituting the role plan after takeover, locking too early, or ignoring a valid outside-plan hover.
    [TestCase(86, 1)] [TestCase(99, 0)] [TestCase(0, 0)]
    public async Task ManualWatchdogOnlyLocksCurrentEligibleHover(int hover, int writes)
    {
        var clock = new PlannedBanTests.Clock(); var client = new Client(); client.Hover(hover == 0 ? 86 : hover);
        var picks = new PlannedPickExecutor(Observer(), () => Plan(1), true, clock);
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
        client.Hover(hover);
        clock.Advance(26000);
        client.Snapshot = client.Snapshot with { TimeLeftMilliseconds = 2000 };
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Has.Count.EqualTo(writes));
        if (writes > 0) Assert.That(client.Writes[0], Is.EqualTo((11L, 86, true)));
    }

    // Catches urgency waiting out the host poll and the readiness gate opening by default.
    [TestCase(true, 1)] [TestCase(false, 0)]
    public async Task UrgentActionCompletesWithoutAvoidableHoverWait(bool verified, int count)
    {
        var client = new Client { Snapshot = Snapshot(1500) };
        var picks = new PlannedPickExecutor(Observer(), () => Plan(1), verified, new PlannedBanTests.Clock());
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Has.Count.EqualTo(count));
        if (count > 0) Assert.That(client.Writes[0], Is.EqualTo((11L, 1, true)));
    }

    // Catches overwriting a newer plan before the previous sent hover has been acknowledged.
    [Test]
    public async Task LiveEditWaitsForOwnHoverAcknowledgmentThenReplacesIt()
    {
        var clock = new PlannedBanTests.Clock(); var client = new Client(); var plan = Plan(1);
        var picks = new PlannedPickExecutor(Observer(), () => plan, true, clock);
        await picks.RefreshAsync(client, client.Snapshot, default); clock.Advance(1000);
        await picks.RefreshAsync(client, client.Snapshot, default);
        plan = Plan(2) with { Revision = 2 };
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Has.Count.EqualTo(1));
        client.Hover(1);
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 1, false), (11L, 2, false) }));
    }

    // Catches ignoring either team's authoritative picks/bans while choosing ordered fallback.
    [Test]
    public async Task EligibilitySkipsBothTeamsPicksAndBans()
    {
        var client = new Client { Snapshot = Snapshot(1500) with { EnemyPicks = [1], BannedChampions = [2],
            Players = [new(2, DraftRole.Middle, 0, 0), new(3, DraftRole.Top, 3, 86)] } };
        var picks = new PlannedPickExecutor(Observer(), () => Plan(1, 2, 3, 86), true, new PlannedBanTests.Clock());
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 86, true) }));
        Assert.That(picks.State.Skipped!.Select(x => x.Reason), Is.EqualTo(new[] { PickSkipReason.AlreadyPicked, PickSkipReason.AlreadyBanned, PickSkipReason.AlreadyPicked }));
    }

    // Catches a failed request being blindly replayed or an ambiguous acknowledgment treated as automatic ownership.
    [TestCase(BanRequestOutcome.Accepted, false)]
    [TestCase(BanRequestOutcome.Ambiguous, true)]
    [TestCase(BanRequestOutcome.Rejected, true)]
    public async Task HoverOutcomeRequiresObservationBeforeAnotherRequest(BanRequestOutcome outcome, bool manual)
    {
        var clock = new PlannedBanTests.Clock(); var client = new Client { Outcome = outcome };
        var picks = new PlannedPickExecutor(Observer(), () => Plan(1), true, clock);
        await picks.RefreshAsync(client, client.Snapshot, default); clock.Advance(1000);
        await picks.RefreshAsync(client, client.Snapshot, default);
        clock.Advance(1000);
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Has.Count.EqualTo(1));
        client.Hover(1);
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(picks.State.Manual, Is.EqualTo(manual));
        Assert.That(client.Writes, Has.Count.EqualTo(manual ? 1 : 2));
    }

    // Catches edits resuming automatic selection after an external hover, or timer resets extending the deadline.
    [Test]
    public async Task TakeoverPersistsThroughEditsAndSuppliedTimerReset()
    {
        var clock = new PlannedBanTests.Clock(); var client = new Client(); client.Hover(86);
        var plan = Plan(1); var picks = new PlannedPickExecutor(Observer(), () => plan, true, clock);
        await picks.RefreshAsync(client, client.Snapshot, default);
        plan = Plan(2) with { Revision = 2 }; clock.Advance(26000);
        client.Snapshot = client.Snapshot with { TimeLeftMilliseconds = 28000 };
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 86, true) }));
        Assert.That(picks.State.Manual, Is.True);
    }

    // Catches final verification trusting a changed role/action/lifecycle, expired budget or stale plan publication.
    [TestCase("role")] [TestCase("action")] [TestCase("disarm")] [TestCase("disconnect")]
    [TestCase("expiry")] [TestCase("latency")] [TestCase("revision")]
    public async Task FinalVerificationCannotDispatchInvalidatedWork(string change)
    {
        var clock = new PlannedBanTests.Clock(); var observer = Observer(); var client = new Client { Snapshot = Snapshot(1500) }; var plan = Plan(1);
        client.OnRead = () => {
            if (change == "role") client.Snapshot = client.Snapshot with { Players = [new(2, DraftRole.Top, 0, 0)] };
            if (change == "action") client.Snapshot = client.Snapshot with { Actions = [new(19, 2, DraftActionKind.Pick, 0, true, false)] };
            if (change == "disarm") observer.SetEnabled(false);
            if (change == "disconnect") observer.Disconnect();
            if (change == "expiry") client.Snapshot = client.Snapshot with { TimeLeftMilliseconds = 0 };
            if (change == "latency") clock.Advance(1500);
        };
        var picks = new PlannedPickExecutor(observer, () => plan, true, clock,
            startIfCurrent: (revision, start) => { if (change == "revision") plan = Plan(2) with { Revision = 2 }; return revision == plan.Revision ? start() : null; });
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
    }

    // Catches queued concurrent refreshes duplicating a sent request, including after a live edit.
    [Test]
    public async Task SerializesInFlightRequestAndLiveEdit()
    {
        var client = new Client { Snapshot = Snapshot(1500) }; var plan = Plan(1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<BanRequestOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.WriteOverride = _ => { started.TrySetResult(); return pending.Task; };
        var picks = new PlannedPickExecutor(Observer(), () => plan, true, new PlannedBanTests.Clock());
        var first = picks.RefreshAsync(client, client.Snapshot, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        plan = Plan(2) with { Revision = 2 };
        var second = picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Has.Count.EqualTo(1));
        pending.SetResult(BanRequestOutcome.Ambiguous); await Task.WhenAll(first, second);
        Assert.That(client.Writes, Has.Count.EqualTo(1));
    }

    // Catches late reads ignoring cancellation after replacement, end, disarm or shutdown.
    [TestCase("action")] [TestCase("end")] [TestCase("disarm")] [TestCase("disconnect")]
    public async Task LateReadCannotEscapeObserverCancellation(string change)
    {
        var observer = Observer(); var client = new Client { Snapshot = Snapshot(1500) };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<DraftSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        client.ReadOverride = value => { token = value; started.SetResult(); return pending.Task; };
        var picks = new PlannedPickExecutor(observer, () => Plan(1), true, new PlannedBanTests.Clock());
        var refresh = picks.RefreshAsync(client, client.Snapshot, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (change == "action") observer.Observe(observer.State.Generation, 3, client.Snapshot with { Actions = [new(19, 2, DraftActionKind.Pick, 0, true, false)] });
        if (change == "end") observer.Observe(observer.State.Generation, 3, null);
        if (change == "disarm") observer.SetEnabled(false);
        if (change == "disconnect") observer.Disconnect();
        Assert.That(token.IsCancellationRequested, Is.True);
        pending.SetResult(client.Snapshot); await refresh;
        Assert.That(client.Writes, Is.Empty);
    }

    // Catches permanent exhaustion or unbounded diagnostics for a large unavailable plan.
    [Test]
    public async Task ExhaustionIsBoundedAndRecoversWithLivePlan()
    {
        await using var diagnostics = new AFKtor.Core.Diagnostics.DiagnosticCollector();
        var client = new Client { Snapshot = Snapshot(1500), Available = [] }; var plan = Plan(Enumerable.Range(1, 1000).ToArray());
        var picks = new PlannedPickExecutor(Observer(), () => plan, true, new PlannedBanTests.Clock(), diagnostics);
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(picks.State.Status, Is.EqualTo(PickStatus.Exhausted));
        Assert.That(picks.State.SkippedCount, Is.EqualTo(1000));
        Assert.That(picks.State.Skipped, Has.Length.EqualTo(8));
        Assert.That(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(diagnostics.GetMemoryHistory().Events[^1]).Length, Is.LessThanOrEqualTo(AFKtor.Core.Diagnostics.DiagnosticLimits.EventBytes));
        plan = Plan(86) with { Revision = 2 }; client.Available = [86];
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 86, true) }));
    }

    // Catches attributing an own intermediate lock acknowledgment to manual control.
    [Test]
    public async Task OwnLockIntermediateSelectionDoesNotCauseTakeover()
    {
        var client = new Client { Snapshot = Snapshot(1500) };
        var picks = new PlannedPickExecutor(Observer(), () => Plan(1), true, new PlannedBanTests.Clock());
        await picks.RefreshAsync(client, client.Snapshot, default);
        client.Hover(1);
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(picks.State.Manual, Is.False);
        Assert.That(client.Writes, Has.Count.EqualTo(1));
    }

    // Catches an old queued observation borrowing the replacement action's fresh work token.
    [Test]
    public async Task QueuedOldActionCannotBorrowReplacementLifetime()
    {
        var observer = Observer(); var client = new Client { Snapshot = Snapshot(1500) };
        observer.Observe(observer.State.Generation, 3, Snapshot(1500) with { Actions = [new(19, 2, DraftActionKind.Pick, 0, true, false)] });
        var picks = new PlannedPickExecutor(observer, () => Plan(1), true, new PlannedBanTests.Clock());
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
    }

    // Catches unrelated revision changes bypassing the deliberate lock wait for an unchanged hover.
    [Test]
    public async Task RevisionChangeDoesNotBypassLockPacingForSameChampion()
    {
        var clock = new PlannedBanTests.Clock(); var client = new Client(); var plan = Plan(1);
        var picks = new PlannedPickExecutor(Observer(), () => plan, true, clock);
        await picks.RefreshAsync(client, client.Snapshot, default); clock.Advance(1000);
        await picks.RefreshAsync(client, client.Snapshot, default); client.Hover(1);
        plan = plan with { Revision = 2 };
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Has.Count.EqualTo(1));
    }

    internal static RolePlanRevision Plan(params int[] ids) => new(1, RolePlanRevision.Empty.Roles.SetItem(DraftRole.Middle, new([..ids], [])));
    internal static DraftSnapshot Snapshot(long remaining = 28000) => DraftObserverTests.Draft(remaining: remaining) with
    { Actions = [new(11, 2, DraftActionKind.Pick, 0, true, false)] };
    internal static DraftObserver Observer()
    {
        var observer = new DraftObserver(); observer.SetEnabled(true); var g = observer.BeginSession();
        observer.Observe(g, 1, null); observer.Observe(g, 2, Snapshot()); return observer;
    }
    internal sealed class Client : IPickDraftClient
    {
        public DraftSnapshot Snapshot { get; set; } = PlannedPickTests.Snapshot();
        public HashSet<int> Available { get; set; } = [1, 2, 3, 86];
        public List<(long, int, bool)> Writes { get; } = [];
        public Action? OnRead { get; set; }
        public Func<CancellationToken, Task<DraftSnapshot?>>? ReadOverride { get; set; }
        public Func<CancellationToken, Task<BanRequestOutcome>>? WriteOverride { get; set; }
        public BanRequestOutcome Outcome { get; set; } = BanRequestOutcome.Accepted;
        public void Hover(int champion, bool completed = false) => Snapshot = Snapshot with
        { Actions = [new(11, 2, DraftActionKind.Pick, champion, !completed, completed)] };
        public Task<DraftSnapshot?> ReadAsync(CancellationToken token) { OnRead?.Invoke(); return ReadOverride?.Invoke(token) ?? Task.FromResult<DraftSnapshot?>(Snapshot); }
        public Task<IReadOnlySet<int>> ReadPickableAsync(CancellationToken token) => Task.FromResult<IReadOnlySet<int>>(Available);
        public Task<BanRequestOutcome> PickAsync(long action, int champion, bool completed, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Writes.Add((action, champion, completed)); return WriteOverride?.Invoke(token) ?? Task.FromResult(Outcome); }
    }
}
