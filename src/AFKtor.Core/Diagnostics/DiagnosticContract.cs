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
public sealed record DiagnosticDetails(ConnectionStage Stage, long Generation, DiagnosticReason Reason);
public sealed record DiagnosticEvent(int SchemaVersion, Guid RunId, string Build, long Sequence,
    DateTimeOffset Timestamp, double ElapsedMilliseconds, string Code, string Severity, string Source,
    DiagnosticDetails Details, bool Truncated = false);
public sealed record ConnectionDiagnosticState(ConnectionStage Stage, long Generation, DiagnosticReason Reason,
    DateTimeOffset? ObservedAt, DateTimeOffset? LastSuccessfulObservation);
public sealed record UnavailableDiagnosticState(string Availability = "unavailable");
public sealed record DiagnosticHealth(string Api, string Discovery, string Storage, int QueueDepth,
    long ProducerDropped, long SinkDropped, long Evicted, long Truncated, string? FailureCategory);
public sealed record DiagnosticState(int SchemaVersion, Guid RunId, string Build, DateTimeOffset StartedAt,
    DateTimeOffset SnapshotAt, long SequenceWatermark, ConnectionDiagnosticState Connection,
    UnavailableDiagnosticState ReadyCheck, UnavailableDiagnosticState Arming,
    UnavailableDiagnosticState Settings, UnavailableDiagnosticState Operation, DiagnosticHealth Health);
public sealed record DiagnosticRun(int SchemaVersion, Guid RunId, string Build, DateTimeOffset StartedAt,
    bool IsCurrent, string Persistence);
public sealed record DiagnosticEventPage(int SchemaVersion, Guid RunId, DiagnosticEvent[] Events,
    long NextCursor, long? FirstAvailableSequence, long? LastAvailableSequence, long SequenceWatermark,
    bool HasMore, bool HistoryEvicted, long DroppedEvents, bool Truncated, string Completeness);

/// <summary>Background-only extension seam. Implementations must honor cancellation; a hung sink is abandoned.</summary>
public interface IDiagnosticSink
{
    ValueTask WriteAsync(DiagnosticEvent record, CancellationToken cancellationToken);
}

/// <summary>Read-model extension seam for retained history. Methods execute only on API workers.</summary>
public interface IDiagnosticHistory
{
    DiagnosticRun[] GetRuns();
    DiagnosticEventPage? GetEvents(Guid runId, long afterSequence, int limit);
}
