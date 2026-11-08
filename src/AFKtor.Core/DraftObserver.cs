using System.Collections.Immutable;

namespace AFKtor.Core;

public enum DraftAdmission { Disconnected, Ended, Disarmed, MidDraft, UnsupportedQueue, UnknownRole, InsufficientState, Admitted }
public enum DraftActionKind { None, Pick, Ban }
public sealed record DraftAction(long Id, long ActorCellId, DraftActionKind Kind, int ChampionId, bool InProgress, bool Completed);
public sealed record DraftPlayer(long CellId, DraftRole? Role, int ChampionId, int PickIntent);
public sealed record DraftSnapshot(long GameId, int QueueId, long LocalCellId, ImmutableArray<DraftPlayer> Players,
    ImmutableArray<DraftAction> Actions, long TimeLeftMilliseconds, bool Infinite = false, bool Custom = false, bool Spectating = false);
public sealed record DraftState(long Generation = 0, long Sequence = 0, long? GameId = null,
    int? QueueId = null, DraftRole? Role = null, DraftAdmission Admission = DraftAdmission.Disconnected,
    long? ActionId = null, DraftActionKind Action = DraftActionKind.None, long? TimeLeftMilliseconds = null);

/// <summary>Read-only draft lifecycle projection. Inputs must be fresh authoritative reads.</summary>
public sealed class DraftObserver(AFKtor.Core.Diagnostics.DiagnosticCollector? diagnostics = null)
{
    private readonly object gate = new();
    private DraftState state = new();
    private long generation, sequence, armingEpoch;
    private bool enabled, connected, baseline, admitted;
    private long? gameId;
    public DraftState State { get { lock (gate) return state; } }
    /// <summary>Capture before starting an asynchronous read; either enabled-state edge invalidates it.</summary>
    public long ArmingEpoch { get { lock (gate) return armingEpoch; } }

    public long BeginSession()
    {
        lock (gate)
        {
            generation++;
            connected = true;
            baseline = admitted = false;
            sequence = 0;
            gameId = null;
            Publish(new(Generation: generation, Admission: DraftAdmission.InsufficientState));
            return generation;
        }
    }

    public void SetEnabled(bool value)
    {
        lock (gate)
        {
            if (enabled == value) return;
            enabled = value;
            armingEpoch++;
            if (enabled && gameId is null) baseline = false;
            // Any ongoing draft loses admission on either edge; idle stays a useful baseline.
            if (gameId is not null)
            {
                admitted = false;
                Publish(state with { Admission = enabled ? DraftAdmission.MidDraft : DraftAdmission.Disarmed });
            }
        }
    }

    public void Observe(long eventGeneration, long eventSequence, DraftSnapshot? snapshot, long? readArmingEpoch = null)
    {
        lock (gate)
        {
            if (!connected || eventGeneration != generation || eventSequence <= sequence) return;
            if (readArmingEpoch is { } epoch && epoch != armingEpoch) return;
            sequence = eventSequence;
            if (snapshot is null)
            {
                baseline = true;
                gameId = null;
                admitted = false;
                Publish(new(generation, sequence, Admission: DraftAdmission.Ended));
                return;
            }
            if (gameId != snapshot.GameId)
            {
                admitted = baseline && enabled;
                gameId = snapshot.GameId;
            }
            baseline = true;
            var locals = snapshot.Players.Where(p => p.CellId == snapshot.LocalCellId).ToArray();
            var role = locals.Length == 1 ? locals[0].Role : null;
            var actions = snapshot.Actions.Where(a => a.ActorCellId == snapshot.LocalCellId && a.InProgress && !a.Completed).ToArray();
            var action = actions.Length == 1 ? actions[0] : null;
            var admission = snapshot.GameId <= 0 || actions.Length > 1 || snapshot.Infinite || snapshot.TimeLeftMilliseconds < 0
                ? DraftAdmission.InsufficientState
                : snapshot.QueueId != 400 || snapshot.Custom || snapshot.Spectating ? DraftAdmission.UnsupportedQueue
                : role is null ? DraftAdmission.UnknownRole
                : !enabled ? DraftAdmission.Disarmed : !admitted ? DraftAdmission.MidDraft : DraftAdmission.Admitted;
            Publish(new(generation, sequence, snapshot.GameId, snapshot.QueueId, role, admission,
                action?.Id, action?.Kind ?? DraftActionKind.None, snapshot.Infinite ? null : snapshot.TimeLeftMilliseconds));
        }
    }

    public void Disconnect()
    {
        lock (gate)
        {
            connected = baseline = admitted = false;
            gameId = null;
            Publish(new(Generation: generation, Admission: DraftAdmission.Disconnected));
        }
    }

    private void Publish(DraftState value)
    {
        state = value;
        diagnostics?.ObserveDraft(value);

    }
}
