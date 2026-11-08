using AFKtor.Core.Diagnostics;

namespace AFKtor.Core;

public enum PickStatus { Idle, NativeVerificationRequired, Pacing, Exhausted, Dispatching, AwaitingOutcome, Completed, ManualControl, InvalidHover, Rejected, Cancelled }
public enum PickSkipReason { Unavailable, AlreadyBanned, AlreadyPicked }
public sealed record PickSkip(int ChampionId, PickSkipReason Reason);
public sealed record PickState(PickStatus Status = PickStatus.Idle, long Generation = 0, long? GameId = null,
    long? ActionId = null, long Revision = 0, int? ChampionId = null, bool Manual = false, bool Lock = false,
    double? RemainingMilliseconds = null, BanRequestOutcome? Outcome = null, PickSkip[]? Skipped = null, int SkippedCount = 0,
    ManualPickPolicy ManualPickPolicy = ManualPickPolicy.LockCurrentHover)
{
    public string PolicyExplanation => ManualPickPolicies.Explanation(ManualPickPolicy);
}
public interface IPickDraftClient : IDraftClient
{
    Task<IReadOnlySet<int>> ReadPickableAsync(CancellationToken cancellationToken);
    Task<BanRequestOutcome> PickAsync(long actionId, int championId, bool completed, CancellationToken cancellationToken);
}

/// <summary>Serialized hover/lock execution. Acknowledgments, never transport success, advance the action.</summary>
public sealed class PlannedPickExecutor(DraftObserver observer, Func<RolePlanRevision> plans,
    bool nativeSemanticsVerified = false, TimeProvider? timeProvider = null, DiagnosticCollector? diagnostics = null,
    Func<long, Func<Task<BanRequestOutcome>>, Task<BanRequestOutcome>?>? startIfCurrent = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim owner = new(1, 1);
    private PickState state = new();
    private (long Generation, long Game)? draft;
    private long? actionId;
    private long detected, dispatchedRevision;
    private double deadline, hoverStarted;
    private int acknowledged, expected;
    private bool manual, pending, locking, completedAction;
    private BanRequestOutcome? outcome;
    public PickState State => Volatile.Read(ref state);
    public void ObserveCancellation() => Publish(State with { Status = PickStatus.Cancelled });
    private double Elapsed => clock.GetElapsedTime(detected).TotalMilliseconds;
    private double Remaining(DraftSnapshot snapshot) => Math.Min(deadline - Elapsed, snapshot.TimeLeftMilliseconds);

    public async Task RefreshAsync(IPickDraftClient client, DraftSnapshot? snapshot, CancellationToken cancellationToken)
    {
        await owner.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var admission = observer.State;
            if (snapshot is null || admission.Admission != DraftAdmission.Admitted)
            { Publish(State with { Status = PickStatus.Cancelled }); return; }
            var identity = (admission.Generation, snapshot.GameId);
            if (draft != identity)
            { draft = identity; actionId = null; manual = pending = locking = completedAction = false; acknowledged = expected = 0; outcome = null; }
            if (completedAction) return; // Confirmed completion is terminal even if a later observation regresses.
            var localPicks = snapshot.Actions.Where(a => a.ActorCellId == snapshot.LocalCellId && a.Kind == DraftActionKind.Pick).ToArray();
            if (localPicks.FirstOrDefault(a => a.Completed) is { } completed)
            {
                Complete(completed, admission.Generation, snapshot.GameId);
                return;
            }
            var active = LocalPick(snapshot);
            if (active is null) { Publish(State with { Status = PickStatus.Idle }); return; }
            if (admission.ActionId != active.Id || admission.Action != DraftActionKind.Pick)
            { Publish(State with { Status = PickStatus.Cancelled }); return; }
            if (actionId != active.Id)
            {
                actionId = active.Id; detected = clock.GetTimestamp(); deadline = snapshot.TimeLeftMilliseconds;
                pending = locking = false; acknowledged = expected = 0; outcome = null; hoverStarted = 0;
            }
            deadline = Math.Min(deadline, Elapsed + snapshot.TimeLeftMilliseconds);
            Reconcile(active);
            var remaining = Remaining(snapshot);
            Publish(new(manual ? PickStatus.ManualControl : PickStatus.Pacing, admission.Generation, snapshot.GameId,
                active.Id, plans().Revision, pending ? expected : active.ChampionId > 0 ? active.ChampionId : null, manual, RemainingMilliseconds: remaining, Outcome: outcome));
            if (!nativeSemanticsVerified) { Publish(State with { Status = PickStatus.NativeVerificationRequired }); return; }
            if (remaining <= plans().Timing.MinimumDispatchMilliseconds) { Publish(State with { Status = PickStatus.Cancelled }); return; }
            if (pending) { Publish(State with { Status = outcome == BanRequestOutcome.Rejected ? PickStatus.Rejected : PickStatus.AwaitingOutcome, Lock = locking }); return; }
            if (manual && plans().ManualPickPolicy is not (ManualPickPolicy.LockCurrentHover or ManualPickPolicy.UseRolePlan)) return;
            var timing = plans().Timing;
            if (manual ? remaining > timing.UrgentMilliseconds : remaining > timing.UrgentMilliseconds &&
                (Elapsed < timing.ActionPacingMilliseconds || acknowledged > 0 && plans().Revision == dispatchedRevision && Elapsed - hoverStarted < timing.HoverLockMilliseconds)) return;

            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, observer.WorkToken);
            var epoch = observer.ArmingEpoch;
            var verificationStarted = clock.GetTimestamp();
            try
            {
                var available = await client.ReadPickableAsync(lifetime.Token).ConfigureAwait(false);
                var verified = await client.ReadAsync(lifetime.Token).ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
                var now = observer.State;
                var verifiedAction = verified is null ? null : LocalPick(verified);
                if (verified is null || now.Admission != DraftAdmission.Admitted || now.Generation != admission.Generation ||
                    now.GameId != snapshot.GameId || now.ActionId != active.Id || now.Action != DraftActionKind.Pick || now.Role != admission.Role || observer.ArmingEpoch != epoch || verified.GameId != snapshot.GameId ||
                    verified.QueueId != 400 || verified.Custom || verified.Spectating || verified.Infinite ||
                    verified.LocalCellId != snapshot.LocalCellId ||
                    verified.Players.Count(p => p.CellId == verified.LocalCellId) != 1 ||
                    verified.Players.Single(p => p.CellId == verified.LocalCellId).Role != admission.Role)
                { Publish(State with { Status = PickStatus.Cancelled }); return; }
                // A completed action is no longer active, but is authoritative terminal evidence.
                if (verified.Actions.FirstOrDefault(a => a.Id == active.Id && a.ActorCellId == verified.LocalCellId &&
                    a.Kind == DraftActionKind.Pick && a.Completed) is { } finalCompleted)
                { Complete(finalCompleted, admission.Generation, verified.GameId); return; }
                if (verifiedAction?.Id != active.Id)
                { Publish(State with { Status = PickStatus.Cancelled }); return; }
                // Every valid same-action observation constrains future polls, even when this pass cannot dispatch.
                deadline = Math.Min(deadline, Elapsed + verified.TimeLeftMilliseconds - clock.GetElapsedTime(verificationStarted).TotalMilliseconds);
                remaining = Remaining(verified);
                var revision = plans();
                timing = revision.Timing;
                if (remaining <= timing.MinimumDispatchMilliseconds)
                { Publish(State with { Status = PickStatus.Cancelled, RemainingMilliseconds = remaining }); return; }
                Reconcile(verifiedAction!);
                if (pending) return;
                if (remaining > timing.UrgentMilliseconds && (manual || Elapsed < timing.ActionPacingMilliseconds)) return;
                var skipped = new List<PickSkip>();
                int? candidate = null;
                if (manual && revision.ManualPickPolicy is not (ManualPickPolicy.LockCurrentHover or ManualPickPolicy.UseRolePlan))
                { Publish(State with { Status = PickStatus.ManualControl, Manual = true, Revision = revision.Revision }); return; }
                if (manual && revision.ManualPickPolicy == ManualPickPolicy.LockCurrentHover)
                {
                    if (remaining > timing.UrgentMilliseconds) { Publish(State with { Status = PickStatus.ManualControl, Manual = true }); return; }
                    if (verifiedAction!.ChampionId > 0 && Reason(verifiedAction.ChampionId, available, verified) is null) candidate = verifiedAction.ChampionId;
                }
                else if (!manual || remaining <= timing.UrgentMilliseconds) foreach (var id in revision.For(admission.Role!.Value).Picks)
                {
                    if (Reason(id, available, verified) is { } reason) skipped.Add(new(id, reason));
                    else { candidate = id; break; }
                }
                var complete = manual || remaining <= timing.UrgentMilliseconds || candidate == acknowledged && acknowledged > 0;
                if (complete && !manual && remaining > timing.UrgentMilliseconds && Elapsed - hoverStarted < timing.HoverLockMilliseconds) return;
                var evidence = State with { Status = candidate is null ? manual && revision.ManualPickPolicy == ManualPickPolicy.LockCurrentHover ? PickStatus.InvalidHover : PickStatus.Exhausted : PickStatus.Dispatching,
                    Revision = revision.Revision, ChampionId = candidate, Manual = manual, Lock = complete, RemainingMilliseconds = remaining,
                    Skipped = skipped.Take(8).ToArray(), SkippedCount = skipped.Count };
                Publish(evidence);
                if (candidate is null) return;
                Task<BanRequestOutcome> Start()
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    dispatchedRevision = revision.Revision;
                    pending = true; expected = candidate.Value; locking = complete;
                    if (!complete) hoverStarted = Elapsed;
                    return client.PickAsync(active.Id, candidate.Value, complete, lifetime.Token);
                }
                var request = startIfCurrent is not null ? startIfCurrent(revision.Revision, Start)
                    : plans().Revision == revision.Revision ? Start() : null;
                if (request is null) { Publish(evidence with { Status = PickStatus.Cancelled }); return; }
                outcome = await request.ConfigureAwait(false);
                Publish(evidence with { Status = lifetime.IsCancellationRequested ? PickStatus.Cancelled : outcome == BanRequestOutcome.Rejected
                    ? PickStatus.Rejected : PickStatus.AwaitingOutcome, Outcome = outcome });
            }
            catch (OperationCanceledException) { Publish(State with { Status = PickStatus.Cancelled }); }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException)
            { if (pending) outcome = BanRequestOutcome.Ambiguous; Publish(State with { Status = pending ? PickStatus.AwaitingOutcome : PickStatus.Cancelled, Outcome = outcome }); }
        }
        finally { owner.Release(); }
    }
    private void Complete(DraftAction completed, long generation, long gameId)
    {
        var ownSelection = actionId == completed.Id && completed.ChampionId > 0 &&
            (pending && outcome == BanRequestOutcome.Accepted && expected == completed.ChampionId ||
             acknowledged == completed.ChampionId);
        manual |= !ownSelection;
        completedAction = true;
        pending = false; acknowledged = completed.ChampionId; actionId = completed.Id;
        Publish(new(PickStatus.Completed, generation, gameId, completed.Id,
            plans().Revision, completed.ChampionId, manual, Lock: true, Outcome: outcome));
    }
    private void Reconcile(DraftAction action)
    {
        if (pending)
        {
            if (locking && action.ChampionId == expected && outcome == BanRequestOutcome.Accepted) return;
            if (!locking && action.ChampionId == expected)
            {
                pending = false;
                if (outcome == BanRequestOutcome.Accepted) acknowledged = expected;
                else { manual = true; acknowledged = action.ChampionId; }
            }
            else if (action.ChampionId != acknowledged)
            { manual = true; if (!locking) { pending = false; acknowledged = action.ChampionId; } }
        }
        else if (action.ChampionId != acknowledged) { manual = true; acknowledged = action.ChampionId; }
    }
    private static PickSkipReason? Reason(int id, IReadOnlySet<int> available, DraftSnapshot snapshot) =>
        !available.Contains(id) ? PickSkipReason.Unavailable :
        (!snapshot.BannedChampions.IsDefaultOrEmpty && snapshot.BannedChampions.Contains(id) || snapshot.Actions.Any(a => a.Kind == DraftActionKind.Ban && a.Completed && a.ChampionId == id)) ? PickSkipReason.AlreadyBanned :
        (!snapshot.EnemyPicks.IsDefaultOrEmpty && snapshot.EnemyPicks.Contains(id) || snapshot.Players.Any(p => p.CellId != snapshot.LocalCellId && p.ChampionId == id) ||
         snapshot.Actions.Any(a => a.Kind == DraftActionKind.Pick && a.ActorCellId != snapshot.LocalCellId && a.Completed && a.ChampionId == id)) ? PickSkipReason.AlreadyPicked : null;
    private static DraftAction? LocalPick(DraftSnapshot snapshot)
    {
        var actions = snapshot.Actions.Where(a => a.ActorCellId == snapshot.LocalCellId && a.InProgress && !a.Completed).ToArray();
        return actions.Length == 1 && actions[0].Kind == DraftActionKind.Pick ? actions[0] : null;
    }
    private void Publish(PickState value)
    {
        value = value with { Generation = observer.State.Generation, ManualPickPolicy = plans().ManualPickPolicy };
        Volatile.Write(ref state, value); diagnostics?.ObservePick(value);
    }
}
