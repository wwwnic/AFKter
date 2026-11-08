using System.Text.Json.Serialization;

namespace AFKtor.Core.Diagnostics;

public static class DiagnosticLimits
{
    public const int MemoryEvents = 1024;
    public const int SinkQueue = 256;
    public const int PageEvents = 100;
    public const int ResponseBytes = 262144;
    public const int ConcurrentRequests = 8;
    public const int EventBytes = 2048;
    public const int RequestSeconds = 2;
    public const int ShutdownMilliseconds = 2000;
    public const int SinkMilliseconds = 500;
}

public enum ConnectionStage { Unknown, Disconnected, Discovering, Connecting, Connected }
public enum DiagnosticReason { None, DiscoveryUnavailable, DiscoveryFailed, SessionFailure, Replaced, Stopped }
public enum DiagnosticCode { AppStarted, AppStopped, ConnectionChanged }

// Only allowlisted values enter the producer; no arbitrary text, credentials or exception objects.
public readonly record struct DiagnosticObservation(DiagnosticCode Code, ConnectionStage Stage = ConnectionStage.Unknown,
    long Generation = 0, DiagnosticReason Reason = DiagnosticReason.None);
public sealed record DiagnosticDetails(ConnectionStage Stage, long Generation, DiagnosticReason Reason,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ControllerDiagnosticEvidence? Controller = null, RolePlanDiagnosticState? RolePlans = null, DraftState? Draft = null, BanState? Ban = null, PickState? Pick = null);
public sealed record DiagnosticEvent(int SchemaVersion, Guid RunId, string Build, long Sequence,
    DateTimeOffset Timestamp, double ElapsedMilliseconds, string Code, string Severity, string Source,
    DiagnosticDetails Details, bool Truncated = false);
public sealed record ConnectionDiagnosticState(ConnectionStage Stage, long Generation, DiagnosticReason Reason,
    DateTimeOffset? ObservedAt, DateTimeOffset? LastSuccessfulObservation);
public sealed record UnavailableDiagnosticState(string Availability = "unavailable");
public sealed record DiagnosticHealth(string Api, string Discovery, string Storage, int QueueDepth,
    long ProducerDropped, long SinkDropped, long Evicted, long Truncated, string? FailureCategory,
    DiagnosticPersistenceStatus? Persistence = null);
public sealed record DiagnosticState(int SchemaVersion, Guid RunId, string Build, DateTimeOffset StartedAt,
    DateTimeOffset SnapshotAt, long SequenceWatermark, ConnectionDiagnosticState Connection,
    ReadyCheckDiagnosticState ReadyCheck, ArmingDiagnosticState Arming,
    DelayDiagnosticState Settings, OperationDiagnosticState Operation, DiagnosticHealth Health, RolePlanDiagnosticState? RolePlans = null, DraftState? Draft = null, BanState? Ban = null, PickState? Pick = null);
public sealed record DiagnosticRun(int SchemaVersion, Guid RunId, string Build, DateTimeOffset StartedAt,
    bool IsCurrent, string Persistence);
public sealed record DiagnosticEventPage(int SchemaVersion, Guid RunId, DiagnosticEvent[] Events,
    long NextCursor, long? FirstAvailableSequence, long? LastAvailableSequence, long SequenceWatermark,
    bool HasMore, bool HistoryEvicted, long DroppedEvents, bool Truncated, string Completeness,
    string[]? Gaps = null, string Storage = "unavailable", long? PersistedThroughSequence = null);

/// <summary>Background-only extension seam. Implementations must honor cancellation; a hung sink is abandoned.</summary>
public interface IDiagnosticSink
{
    ValueTask WriteAsync(DiagnosticEvent record, CancellationToken cancellationToken);
}

/// <summary>Read-model extension seam for retained history. Methods execute only on API workers.</summary>
public interface IDiagnosticHistory
{
    DiagnosticRun[] GetRuns();
    DiagnosticRun[] GetRuns(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return GetRuns(); }
    DiagnosticEventPage? GetEvents(Guid runId, long afterSequence, int limit);
    DiagnosticEventPage? GetEvents(Guid runId, long afterSequence, int limit, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return GetEvents(runId, afterSequence, limit); }
}

/// <summary>Immutable fixed-category health; implementations must return immediately.</summary>
public sealed record DiagnosticPersistenceStatus(string Availability, string? FailureCategory, long Dropped,
    long Truncated, long DeletedSegments, string[] Gaps);
public interface IDiagnosticSinkHealth
{
    DiagnosticPersistenceStatus Status { get; }
}

public enum ReadyCheckValue { Unknown, Invalid, InProgress, EveryoneReady, StrangerNotReady, PartyNotReady, Error }
public enum PlayerResponseValue { Unknown, None, Accepted, Declined }
public enum AcceptOperationStage { None, Waiting, Verifying, Dispatched, Finished }
public enum ControllerDecision
{
    Initialized, ArmingChanged, DelayChanged, SessionStarted, BaselineEstablished, CheckDetected,
    InitialSkipped, DisarmedSkipped, Observed, Duplicates, StaleUpdate, TimerBackwards,
    Scheduled, Cancelled, Verifying, Verification, Dispatched, Completed, Failed, Finished, Disconnected
}
public enum AcceptCancellationReason { None, Disarmed, SessionReplaced, CheckEnded, TimerBackwards, Disconnected, Disposed }
[Flags]
public enum EligibilityFailure { None = 0, NotPending = 1, TimerBackwards = 2, InsufficientTimer = 4, StaleOperation = 8 }
public enum AcceptRequestOutcome { None, CompletedUnconfirmed, Failed, Unknown }
public enum AcceptFailureCategory { None, Http, Io, InvalidData, Timeout, Cancelled }
public sealed record ReadyCheckDiagnosticState(string Availability = "unavailable", long Generation = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CheckId = null, bool Baseline = false, ReadyCheckValue State = ReadyCheckValue.Unknown,
    PlayerResponseValue Response = PlayerResponseValue.Unknown, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Timer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ObservedAt = null, long DuplicateCount = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? FirstDuplicateAt = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastDuplicateAt = null);
public sealed record ArmingDiagnosticState(string Availability = "unavailable", bool Armed = false);
public sealed record DelayDiagnosticState(string Availability = "unavailable", double DelaySeconds = 0);
public sealed record OperationDiagnosticState(string Availability = "unavailable", long Generation = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CheckId = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? AttemptId = null, AcceptOperationStage Stage = AcceptOperationStage.None,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? CapturedDelaySeconds = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? Deadline = null,
    AcceptRequestOutcome Outcome = AcceptRequestOutcome.None, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? RemainingSeconds = null);
// All fields are enums, bounded scalars or immutable fixed-shape records. No external text enters ingress.
public sealed record ControllerDiagnosticEvidence(long Revision, DateTimeOffset ObservedAt,
    ControllerDecision Decision, ReadyCheckDiagnosticState ReadyCheck, ArmingDiagnosticState Arming,
    DelayDiagnosticState Settings, OperationDiagnosticState Operation,
    long EventGeneration = 0, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? EventCheckId = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? EventAttemptId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PreviousArmed = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PreviousDelaySeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] AcceptCancellationReason Cancellation = AcceptCancellationReason.None,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] EligibilityFailure Failures = EligibilityFailure.None, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? InitialTimer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? LastTimer = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ReturnedTimer = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? LocalElapsedSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? RequiredTimer = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? LatencyMilliseconds = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? HttpStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] AcceptFailureCategory Failure = AcceptFailureCategory.None,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AcceptOperationStage? EventStage = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AcceptRequestOutcome? EventOutcome = null);

public sealed record RolePlanDiagnosticState(long Revision, long SavedRevision, PlanSaveHealth Health, PlanRecovery Recovery);
