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

    // Catches losing a shorter final-read deadline on expiry/exhaustion, including verification latency.
    [TestCase(100, 0, 2000)]
    [TestCase(1000, 0, 2000)]
    [TestCase(1000, 400, 700)]
    public async Task FinalReadDeadlineCannotBeRevivedByLaterTimerReset(long finalTimer, int readLatency, int laterElapsed)
    {
        var clock = new PlannedBanTests.Clock(); var client = new Client { Available = [] };
        var picks = new PlannedPickExecutor(Observer(), () => Plan(1), true, clock);
        await picks.RefreshAsync(client, client.Snapshot, default);
        clock.Advance(1000);
        client.OnRead = () => {
            clock.Advance(readLatency);
            client.Snapshot = client.Snapshot with { TimeLeftMilliseconds = finalTimer };
        };
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(picks.State.Status, Is.EqualTo(finalTimer <= 250 ? PickStatus.Cancelled : PickStatus.Exhausted));
        Assert.That(client.Writes, Is.Empty);

        client.OnRead = null; client.Available = [1];
        client.Snapshot = client.Snapshot with { TimeLeftMilliseconds = 28000 };
        clock.Advance(laterElapsed);
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty, "An authoritative final-read deadline remains expired after a reset.");
        Assert.That(picks.State.Status, Is.EqualTo(PickStatus.Cancelled));
        Assert.That(picks.State.RemainingMilliseconds, Is.LessThanOrEqualTo(250));
    }

    // Catches stale automatic selection/action evidence after an observed completion, including before any write.
    [TestCase(false, 86, true)]
    [TestCase(true, 86, true)]
    [TestCase(true, 1, false)]
    public async Task CompletedObservationPublishesActualPickWithoutAnotherWrite(bool ownHover, int champion, bool manual)
    {
        await using var diagnostics = new AFKtor.Core.Diagnostics.DiagnosticCollector();
        var clock = new PlannedBanTests.Clock(); var client = new Client();
        var picks = new PlannedPickExecutor(Observer(), () => Plan(1), true, clock, diagnostics);
        if (ownHover)
        {
            await picks.RefreshAsync(client, client.Snapshot, default);
            clock.Advance(1000);
            await picks.RefreshAsync(client, client.Snapshot, default);
        }
        client.Snapshot = client.Snapshot with
        { Actions = [new(ownHover ? 11 : 19, 2, DraftActionKind.Pick, champion, false, true)] };
        await picks.RefreshAsync(client, client.Snapshot, default);
        await picks.RefreshAsync(client, client.Snapshot, default);

        Assert.That(client.Writes, Has.Count.EqualTo(ownHover ? 1 : 0));
        Assert.That(picks.State.Status, Is.EqualTo(PickStatus.Completed));
        Assert.That(picks.State.ChampionId, Is.EqualTo(champion));
        Assert.That(picks.State.ActionId, Is.EqualTo(ownHover ? 11 : 19));
        Assert.That(picks.State.Manual, Is.EqualTo(manual));
        Assert.That(picks.State.Lock, Is.True);
        Assert.That(diagnostics.GetState().Pick, Is.EqualTo(picks.State));
        Assert.That(diagnostics.GetMemoryHistory().Events.Last(e => e.Code == "draft.pick").Details.Pick, Is.EqualTo(picks.State));
    }

    // Catches the watchdog ignoring policy, replacing invalid hover, or not taking ordered eligible fallback.
    [TestCase(ManualPickPolicy.LockCurrentHover, 86, 86)]
    [TestCase(ManualPickPolicy.LockCurrentHover, 99, 0)]
    [TestCase(ManualPickPolicy.UseRolePlan, 86, 2)]
    [TestCase(ManualPickPolicy.DoNothing, 86, 0)]
    [TestCase((ManualPickPolicy)99, 86, 0)]
    public async Task ManualDeadlineHonorsPolicy(ManualPickPolicy policy, int hover, int expected)
    {
        var client = new Client { Snapshot = Snapshot(1500), Available = [2, 86] }; client.Hover(hover);
        var picks = new PlannedPickExecutor(Observer(), () => Plan(1, 2) with { ManualPickPolicy = policy }, true, new PlannedBanTests.Clock());
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.EqualTo(expected == 0 ? Array.Empty<(long, int, bool)>() : new[] { (11L, expected, true) }));
        Assert.That(picks.State.Manual, Is.True);
    }

    // Catches applying Do nothing before any takeover, blocking ordinary role-plan selection.
    [Test]
    public async Task DoNothingStillSelectsNormallyBeforeTakeover()
    {
        var client = new Client { Snapshot = Snapshot(1500) };
        var picks = new PlannedPickExecutor(Observer(), () => Plan(1) with { ManualPickPolicy = ManualPickPolicy.DoNothing }, true, new PlannedBanTests.Clock());
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 1, true) }));
    }

    // Catches capturing policy before final verification, or resetting manual takeover on live edits.
    [TestCase(ManualPickPolicy.LockCurrentHover, ManualPickPolicy.UseRolePlan, 2)]
    [TestCase(ManualPickPolicy.UseRolePlan, ManualPickPolicy.LockCurrentHover, 86)]
    [TestCase(ManualPickPolicy.UseRolePlan, ManualPickPolicy.DoNothing, 0)]
    public async Task HeldVerificationUsesLatestPolicyAndPlan(ManualPickPolicy before, ManualPickPolicy after, int expected)
    {
        var client = new Client { Snapshot = Snapshot(1500) }; client.Hover(86);
        var plan = Plan(1) with { ManualPickPolicy = before };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<DraftSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ReadOverride = _ => { entered.TrySetResult(); return release.Task; };
        var picks = new PlannedPickExecutor(Observer(), () => plan, true, new PlannedBanTests.Clock());
        var refresh = picks.RefreshAsync(client, client.Snapshot, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        plan = Plan(2) with { Revision = 2, ManualPickPolicy = after };
        release.SetResult(client.Snapshot); await refresh;
        Assert.That(client.Writes, Is.EqualTo(expected == 0 ? Array.Empty<(long, int, bool)>() : new[] { (11L, expected, true) }));
        Assert.That(picks.State.Manual, Is.True);
    }

    // Catches fallback substituting an unconfigured hover when the role plan is exhausted.
    [Test]
    public async Task ExhaustedManualRolePlanDoesNotLockHover()
    {
        var client = new Client { Snapshot = Snapshot(1500), Available = [86] }; client.Hover(86);
        var picks = new PlannedPickExecutor(Observer(), () => Plan(1) with { ManualPickPolicy = ManualPickPolicy.UseRolePlan }, true, new PlannedBanTests.Clock());
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
        Assert.That(picks.State.Status, Is.EqualTo(PickStatus.Exhausted));
    }

    // Catches policy changes resuming normal automation early or clearing the draft-wide takeover latch.
    [Test]
    public async Task LiveSwitchFromDoNothingWaitsForDeadlineAndKeepsTakeover()
    {
        var clock = new PlannedBanTests.Clock(); var client = new Client(); client.Hover(86);
        var plan = Plan(1) with { ManualPickPolicy = ManualPickPolicy.DoNothing };
        var picks = new PlannedPickExecutor(Observer(), () => plan, true, clock);
        await picks.RefreshAsync(client, client.Snapshot, default);
        clock.Advance(1000);
        plan = Plan(2) with { Revision = 2, ManualPickPolicy = ManualPickPolicy.UseRolePlan };
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
        clock.Advance(25000);
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 2, true) }));
        Assert.That(picks.State.Manual, Is.True);
    }

    // Catches using a superseded decision after a policy publishes at the atomic dispatch boundary.
    [Test]
    public async Task PolicyEditAtDispatchBoundaryRejectsOldDecisionAndReevaluatesNextPoll()
    {
        var client = new Client { Snapshot = Snapshot(1500) }; client.Hover(86);
        var plan = Plan(1) with { ManualPickPolicy = ManualPickPolicy.UseRolePlan };
        var picks = new PlannedPickExecutor(Observer(), () => plan, true, new PlannedBanTests.Clock(),
            startIfCurrent: (revision, start) => { plan = plan with { Revision = 2, ManualPickPolicy = ManualPickPolicy.DoNothing }; return revision == plan.Revision ? start() : null; });
        await picks.RefreshAsync(client, client.Snapshot, default);
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
        Assert.That(picks.State.Manual, Is.True);
    }

    // Catches recalling a sent lock or dispatching a second write after live policy edits before reconciliation.
    [TestCase(ManualPickPolicy.LockCurrentHover)]
    [TestCase(ManualPickPolicy.UseRolePlan)]
    [TestCase(ManualPickPolicy.DoNothing)]
    public async Task SentLockIsIrrevocableAcrossPolicyChanges(ManualPickPolicy after)
    {
        var client = new Client { Snapshot = Snapshot(1500) }; client.Hover(86);
        var plan = Plan(1) with { ManualPickPolicy = ManualPickPolicy.UseRolePlan };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<BanRequestOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.WriteOverride = _ => { entered.TrySetResult(); return release.Task; };
        var picks = new PlannedPickExecutor(Observer(), () => plan, true, new PlannedBanTests.Clock());
        var first = picks.RefreshAsync(client, client.Snapshot, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        plan = Plan(2) with { Revision = 2, ManualPickPolicy = after };
        release.SetResult(BanRequestOutcome.Accepted); await first;
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 1, true) }));
        client.Hover(1, true); await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(picks.State.Status, Is.EqualTo(PickStatus.Completed));
        Assert.That(client.Writes, Has.Count.EqualTo(1));
    }

    // Catches watchdog bypassing disarm on a final read under any policy, including when policy changes live.
    [TestCase(ManualPickPolicy.LockCurrentHover)]
    [TestCase(ManualPickPolicy.UseRolePlan)]
    [TestCase(ManualPickPolicy.DoNothing)]
    public async Task DisarmCancelsManualDeadlineForEveryPolicy(ManualPickPolicy after)
    {
        var observer = Observer(); var client = new Client { Snapshot = Snapshot(1500) }; client.Hover(86);
        var plan = Plan(1);
        client.OnRead = () => { plan = plan with { Revision = 2, ManualPickPolicy = after }; observer.SetEnabled(false); };
        var picks = new PlannedPickExecutor(observer, () => plan, true, new PlannedBanTests.Clock());
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
        Assert.That(picks.State.Status, Is.EqualTo(PickStatus.Cancelled));
    }

    // Catches diagnostics publishing an obsolete policy or losing its explanation at no-write decisions.
    [TestCase(ManualPickPolicy.LockCurrentHover)]
    [TestCase(ManualPickPolicy.UseRolePlan)]
    [TestCase(ManualPickPolicy.DoNothing)]
    public async Task ManualPolicyDiagnosticMatchesDecisionAndExplanation(ManualPickPolicy policy)
    {
        await using var diagnostics = new AFKtor.Core.Diagnostics.DiagnosticCollector();
        var client = new Client { Snapshot = Snapshot(1500) }; client.Hover(86);
        var picks = new PlannedPickExecutor(Observer(), () => Plan(1) with { ManualPickPolicy = policy }, true, new PlannedBanTests.Clock(), diagnostics);
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(diagnostics.GetState().Pick!.ManualPickPolicy, Is.EqualTo(policy));
        Assert.That(diagnostics.GetState().Pick!.PolicyExplanation, Does.Contain(policy == ManualPickPolicy.UseRolePlan ? "replacing" : policy == ManualPickPolicy.DoNothing ? "no further" : "never replaced"));
        Assert.That(diagnostics.GetMemoryHistory().Events.Last(e => e.Code == "draft.pick").Details.Pick, Is.EqualTo(picks.State));
    }

    // Catches ignoring live pace/hover edits or restarting either original clock.
    [Test]
    public async Task LiveTimingUsesOriginalActionAndHoverStart()
    {
        var clock = new PlannedBanTests.Clock(); var client = new Client();
        var plan = Plan(1) with { Timing = new(3, 5000, 5000, 3000, 250) };
        var picks = new PlannedPickExecutor(Observer(), () => plan, true, clock);
        await picks.RefreshAsync(client, client.Snapshot, default); clock.Advance(2000);
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
        plan = plan with { Revision = 2, Timing = new(3, 0, 5000, 3000, 250) };
        await picks.RefreshAsync(client, client.Snapshot, default); client.Hover(1);
        clock.Advance(2000); await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 1, false) }));
        plan = plan with { Revision = 3, Timing = new(3, 0, 0, 3000, 250) };
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.EqualTo(new[] { (11L, 1, false), (11L, 1, true) }));
    }

    // Catches a timing edit during final read bypassing the latest normal pacing or urgent threshold.
    [Test]
    public async Task FinalReadReevaluatesLiveTimingBeforePickDispatch()
    {
        var client = new Client { Snapshot = Snapshot(2000) }; var plan = Plan(1);
        client.OnRead = () => plan = plan with { Revision = 2, Timing = new(3, 5000, 5000, 1000, 250) };
        var picks = new PlannedPickExecutor(Observer(), () => plan, true, new PlannedBanTests.Clock());
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
    }

    // Catches a stale incomplete observation plus a timing edit reviving a confirmed finished action.
    [Test]
    public async Task CompletedPickIsNeverRevivedByLiveTimingAndStaleObservation()
    {
        var client = new Client();
        var plan = Plan(1) with { ManualPickPolicy = ManualPickPolicy.UseRolePlan };
        var picks = new PlannedPickExecutor(Observer(), () => plan, true, new PlannedBanTests.Clock());
        await picks.RefreshAsync(client, client.Snapshot, default);
        client.Snapshot = Snapshot(1500); client.Hover(1, true);
        await picks.RefreshAsync(client, client.Snapshot, default);
        client.Hover(0); plan = plan with { Revision = 2, Timing = new(0, 0, 0, 10000, 250) };
        await picks.RefreshAsync(client, client.Snapshot, default);
        Assert.That(client.Writes, Is.Empty);
        Assert.That(picks.State.Status, Is.EqualTo(PickStatus.Completed));
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
