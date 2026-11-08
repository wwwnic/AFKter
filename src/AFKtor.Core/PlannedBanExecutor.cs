using AFKtor.Core.Diagnostics;

namespace AFKtor.Core;

public enum BanStatus { Idle, NativeVerificationRequired, Pacing, Exhausted, Dispatching, AwaitingOutcome, Completed, ManualControl, Rejected, Cancelled }
public enum BanRequestOutcome { Accepted, Rejected, Ambiguous }
public enum BanSkipReason { Unavailable, AlreadyBanned, TeammateIntent }
public sealed record BanSkip(int ChampionId, BanSkipReason Reason);
public sealed record BanState(BanStatus Status = BanStatus.Idle, long? GameId = null, long? ActionId = null,
    long Revision = 0, int? ChampionId = null, BanRequestOutcome? Outcome = null, BanSkip[]? Skipped = null, int SkippedCount = 0, long Generation = 0, double? RemainingMilliseconds = null);
public interface IBanDraftClient : IDraftClient
{
    Task<IReadOnlySet<int>> ReadBannableAsync(CancellationToken cancellationToken);
    Task<BanRequestOutcome> CompleteBanAsync(long actionId, int championId, CancellationToken cancellationToken);
}

/// <summary>Single-owner planned bans. Native readiness defaults closed; transport success requires observation.</summary>
public sealed class PlannedBanExecutor(DraftObserver observer, Func<RolePlanRevision> plans,
    bool nativeSemanticsVerified = false, TimeProvider? timeProvider = null, DiagnosticCollector? diagnostics = null,
    Func<long, Func<Task<BanRequestOutcome>>, Task<BanRequestOutcome>?>? startIfCurrent = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim owner = new(1, 1);
    private BanState state = new();
    private (long Generation, long Game)? draft;
    private long? actionId;
    private long detected;
    private double deadline;
    private double Elapsed => clock.GetElapsedTime(detected).TotalMilliseconds;
    private bool manual, sent;
    private int? expected;
    public BanState State => Volatile.Read(ref state);
    // The observer owns work-token cancellation; hosts publish its invalidation immediately as well.
    public void ObserveCancellation() => Publish(State with { Status = BanStatus.Cancelled });

    public async Task RefreshAsync(IBanDraftClient client, DraftSnapshot? snapshot, CancellationToken cancellationToken)
    {
        await owner.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var admission = observer.State;
            if (snapshot is null || admission.Admission != DraftAdmission.Admitted)
            { Publish(new(BanStatus.Cancelled, snapshot?.GameId)); return; }
            var identity = (admission.Generation, snapshot.GameId);
            if (draft != identity)
            {
                draft = identity; actionId = null; manual = sent = false; expected = null;
            }
            // Any observed local ban selection without matching dispatch evidence yields for the whole draft.
            foreach (var local in snapshot.Actions.Where(a => a.ActorCellId == snapshot.LocalCellId && a.Kind == DraftActionKind.Ban && a.ChampionId > 0))
            {
                if (!sent || local.Id != actionId || local.ChampionId != expected || !local.Completed) manual = true;
                else { Publish(State with { Status = BanStatus.Completed }); return; }
            }
            if (manual) { Publish(new(BanStatus.ManualControl, snapshot.GameId, actionId)); return; }
            var active = LocalBan(snapshot);
            if (active is null) { if (!sent) Publish(new(BanStatus.Idle, snapshot.GameId)); return; }
            if (actionId != active.Id)
            {
                actionId = active.Id; detected = clock.GetTimestamp(); deadline = snapshot.TimeLeftMilliseconds; sent = false; expected = null;
            }
            deadline = Math.Min(deadline, Elapsed + snapshot.TimeLeftMilliseconds);
            var remaining = Math.Min(deadline - Elapsed, snapshot.TimeLeftMilliseconds);
            var timing = plans().Timing;
            if (sent) return; // Accepted, ambiguous and rejected requests all require observation; never blindly resend.
            if (!nativeSemanticsVerified)
            { Publish(new(BanStatus.NativeVerificationRequired, snapshot.GameId, actionId, plans().Revision, RemainingMilliseconds: remaining)); return; }
            if (remaining <= timing.MinimumDispatchMilliseconds) { Publish(new(BanStatus.Cancelled, snapshot.GameId, actionId, plans().Revision, RemainingMilliseconds: remaining)); return; }
            if (remaining > timing.UrgentMilliseconds && Elapsed < timing.ActionPacingMilliseconds)
            { Publish(new(BanStatus.Pacing, snapshot.GameId, actionId, plans().Revision, RemainingMilliseconds: remaining)); return; }

            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, observer.WorkToken);
            var epoch = observer.ArmingEpoch;
            var verificationStarted = clock.GetTimestamp();
            try
            {
                var available = await client.ReadBannableAsync(lifetime.Token).ConfigureAwait(false);
                var verified = await client.ReadAsync(lifetime.Token).ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
                var now = observer.State;
                if (verified is null || now.Admission != DraftAdmission.Admitted || now.Generation != admission.Generation ||
                    now.GameId != snapshot.GameId || observer.ArmingEpoch != epoch ||
                    verified.GameId != snapshot.GameId || verified.QueueId != 400 || verified.Custom || verified.Spectating || verified.Infinite ||
                    verified.LocalCellId != snapshot.LocalCellId || LocalBan(verified)?.Id != active.Id ||
                    verified.Players.Count(p => p.CellId == verified.LocalCellId) != 1 ||
                    verified.Players.Single(p => p.CellId == verified.LocalCellId).Role != admission.Role)
                { Publish(new(BanStatus.Cancelled, snapshot.GameId, actionId, plans().Revision, RemainingMilliseconds: remaining)); return; }
                // Keep every valid same-action timer constraint, including no-dispatch/expiry exits.
                deadline = Math.Min(deadline, Elapsed + verified.TimeLeftMilliseconds - clock.GetElapsedTime(verificationStarted).TotalMilliseconds);
                remaining = Math.Min(deadline - Elapsed, verified.TimeLeftMilliseconds);
                var revision = plans(); timing = revision.Timing;
                if (remaining <= timing.MinimumDispatchMilliseconds)
                { Publish(new(BanStatus.Cancelled, snapshot.GameId, actionId, plans().Revision, RemainingMilliseconds: remaining)); return; }
                if (remaining > timing.UrgentMilliseconds && Elapsed < timing.ActionPacingMilliseconds)
                { Publish(new(BanStatus.Pacing, snapshot.GameId, actionId, plans().Revision, RemainingMilliseconds: remaining)); return; }
                if (verified.Actions.Any(a => a.ActorCellId == verified.LocalCellId && a.Kind == DraftActionKind.Ban && a.ChampionId > 0))
                { manual = true; Publish(new(BanStatus.ManualControl, snapshot.GameId, actionId)); return; }
                var skipped = new List<BanSkip>();
                int? candidate = null;
                foreach (var id in revision.For(admission.Role!.Value).Bans)
                {
                    BanSkipReason? reason = !available.Contains(id) ? BanSkipReason.Unavailable
                        : (!verified.BannedChampions.IsDefaultOrEmpty && verified.BannedChampions.Contains(id) || verified.Actions.Any(a => a.Kind == DraftActionKind.Ban && a.Completed && a.ChampionId == id)) ? BanSkipReason.AlreadyBanned
                        : verified.Players.Any(p => p.CellId != verified.LocalCellId && (p.PickIntent == id || p.ChampionId == id)) ? BanSkipReason.TeammateIntent : null;
                    if (reason is { } why) skipped.Add(new(id, why));
                    else { candidate = id; break; }
                }
                var evidence = new BanState(candidate is null ? BanStatus.Exhausted : BanStatus.Dispatching,
                    snapshot.GameId, active.Id, revision.Revision, candidate, Skipped: skipped.Take(8).ToArray(), SkippedCount: skipped.Count, RemainingMilliseconds: remaining);
                Publish(evidence);
                if (candidate is null) return;
                // No await between the latest in-memory revision/eligibility decision and dispatch.
                lifetime.Token.ThrowIfCancellationRequested();
                Task<BanRequestOutcome> Start()
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    sent = true; expected = candidate;
                    return client.CompleteBanAsync(active.Id, candidate.Value, lifetime.Token);
                }
                var request = startIfCurrent is not null ? startIfCurrent(revision.Revision, Start)
                    : plans().Revision == revision.Revision ? Start() : null;
                if (request is null) { Publish(evidence with { Status = BanStatus.Cancelled }); return; }
                var outcome = await request.ConfigureAwait(false);
                Publish(evidence with { Status = lifetime.IsCancellationRequested ? BanStatus.Cancelled
                    : outcome == BanRequestOutcome.Rejected ? BanStatus.Rejected : BanStatus.AwaitingOutcome, Outcome = outcome });
            }
            catch (OperationCanceledException)
            { Publish(State with { Status = BanStatus.Cancelled }); }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException)
            { Publish(State with { Status = sent ? BanStatus.AwaitingOutcome : BanStatus.Cancelled, Outcome = sent ? BanRequestOutcome.Ambiguous : null }); }
        }
        finally { owner.Release(); }
    }
    private static DraftAction? LocalBan(DraftSnapshot snapshot)
    {
        var actions = snapshot.Actions.Where(a => a.ActorCellId == snapshot.LocalCellId && a.InProgress && !a.Completed).ToArray();
        return actions.Length == 1 && actions[0].Kind == DraftActionKind.Ban ? actions[0] : null;
    }
    private void Publish(BanState value)
    {
        value = value with { Generation = observer.State.Generation };
        Volatile.Write(ref state, value);
        diagnostics?.ObserveBan(value);
    }
}
