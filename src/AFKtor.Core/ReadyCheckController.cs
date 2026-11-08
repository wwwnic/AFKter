using System.Diagnostics;
using AFKtor.Core.Diagnostics;

namespace AFKtor.Core;

/// <summary>Owns arming and one attempt per uninterrupted ready-check lifecycle.</summary>
public sealed class ReadyCheckController(
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<DateTimeOffset>? now = null,
    DiagnosticCollector? diagnostics = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync = delay ?? Task.Delay;
    // Only elapsed durations are used. Keep production countdowns independent
    // of wall-clock corrections; tests supply a manually advanced clock.
    private readonly Func<DateTimeOffset> clockNow = now ?? CreateMonotonicClock();
    private readonly List<Task> work = [];
    private IReadyCheckClient? client;
    private long session;
    private bool armed;
    private bool inCheck;
    private bool hasBaseline;
    private bool disposed;
    private double lastElapsed;
    private PendingAccept? pending;
    private TimeSpan acceptDelay = TimeSpan.FromSeconds(AcceptDelaySettings.Default.DelaySeconds);
    private long revision, nextCheck, nextAttempt;
    private long? checkId;
    private long duplicateCount;
    private DateTimeOffset? firstDuplicateAt, lastDuplicateAt;
    private bool lastInitial;
    private ReadyCheckSnapshot? observed;
    private DateTimeOffset? observedAt;
    private OperationDiagnosticState operationState = new("available");
    private string status = "Disarmed";

    public bool IsArmed { get { lock (gate) { return armed; } } }
    public string Status { get { lock (gate) { return status; } } }
    /// <summary>Live edits move undispatched work from its original detection time.</summary>
    public TimeSpan AcceptDelay
    {
        get { lock (gate) { return acceptDelay; } }
        set
        {
            if (value < TimeSpan.FromSeconds(AcceptDelaySettings.MinimumSeconds) ||
                value > TimeSpan.FromSeconds(AcceptDelaySettings.MaximumSeconds) ||
                value.Ticks % TimeSpan.TicksPerSecond != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Use whole seconds from 0 to 30.");
            }
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var previous = acceptDelay.TotalSeconds;
                if (acceptDelay == value) return;
                acceptDelay = value;
                if (pending is { DispatchedAt: null } operation)
                {
                    operation.ScheduleRevision++;
                    operation.Delay = value;
                    operation.Deadline = operation.DetectedAt + value;
                    operationState = Describe(operation);
                    var changed = operation.Changed;
                    operation.Changed = new();
                    changed.TrySetResult();
                }
                Emit(ControllerDecision.DelayChanged, previousDelay: previous);
            }
        }
    }
    public TimeSpan? Remaining
    {
        get
        {
            lock (gate)
            {
                return pending is not { IsWaiting: true } ? null :
                    TimeSpan.FromSeconds(Math.Max(0, (pending.Deadline - clockNow()).TotalSeconds));
            }
        }
    }

    public void SetArmed(bool value)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var previous = armed;
            armed = value;
            if (!value) { CancelPending(AcceptCancellationReason.Disarmed); }
            status = value ? "Armed — waiting for next check" : "Disarmed";
            Emit(ControllerDecision.ArmingChanged, previousArmed: previous);
        }
    }

    public long BeginSession(IReadyCheckClient readyCheckClient)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            FlushDuplicates();
            CancelPending(AcceptCancellationReason.SessionReplaced);
            client = readyCheckClient;
            inCheck = false;
            hasBaseline = false;
            lastElapsed = 0;
            status = armed ? "Armed — waiting for next check" : "Disarmed";
            session++;
            checkId = null; observed = null; observedAt = null;
            operationState = new("available", session);
            Emit(ControllerDecision.SessionStarted);
            return session;
        }
    }

    public void Update(long connectionSession, ReadyCheckUpdate update)
    {
        lock (gate)
        {
            if (disposed || connectionSession != session || client is null)
            {
                Emit(ControllerDecision.StaleUpdate, eventGeneration: connectionSession);
                return;
            }
            var snapshot = update.Snapshot;
            if (observed == snapshot && lastInitial == update.IsInitial)
            {
                duplicateCount++;
                firstDuplicateAt ??= clockNow(); lastDuplicateAt = clockNow();
                Emit(ControllerDecision.Duplicates, stateOnly: true);
                return;
            }
            FlushDuplicates();
            observed = snapshot; observedAt = clockNow(); lastInitial = update.IsInitial;
            bool initial = !hasBaseline || update.IsInitial;
            hasBaseline = true;
            if (initial) Emit(ControllerDecision.BaselineEstablished);
            if (!snapshot.IsPending)
            {
                CancelPending(AcceptCancellationReason.CheckEnded);
                // An answered check remains observed until its lifecycle ends.
                inCheck = snapshot.State is "InProgress" or "EveryoneReady";
                if (!inCheck) checkId = null;
                else checkId ??= ++nextCheck;
                lastElapsed = snapshot.ElapsedSeconds;
                status = armed ? "Armed — waiting for next check" : "Disarmed";
                Emit(ControllerDecision.Observed);
                return;
            }
            if (inCheck)
            {
                if (snapshot.ElapsedSeconds < lastElapsed)
                {
                    Emit(ControllerDecision.TimerBackwards);
                    CancelPending(AcceptCancellationReason.TimerBackwards);
                    status = "Check uncertain — accept manually";
                }
                lastElapsed = snapshot.ElapsedSeconds;
                Emit(ControllerDecision.Observed);
                return;
            }
            inCheck = true;
            checkId = ++nextCheck;
            lastElapsed = snapshot.ElapsedSeconds;
            Emit(ControllerDecision.CheckDetected);
            if (!armed || initial)
            {
                if (initial) Emit(ControllerDecision.InitialSkipped);
                if (!armed) Emit(ControllerDecision.DisarmedSkipped);
                return;
            }
            var operation = new PendingAccept(session, client, clockNow(), snapshot.ElapsedSeconds, acceptDelay)
            { AttemptId = ++nextAttempt, CheckId = checkId };
            pending = operation;
            operationState = Describe(operation);
            Emit(ControllerDecision.Scheduled, operation);
            status = "Ready check — countdown";
            work.RemoveAll(task => task.IsCompleted);
            work.Add(AcceptAfterDelayAsync(operation));
        }
    }

    private async Task AcceptAfterDelayAsync(PendingAccept operation)
    {
        var token = operation.Cancellation.Token;
        try
        {
            bool initialWait = true;
            while (true)
            {
                Task changed; TimeSpan wait;
                lock (gate)
                {
                    if (!IsCurrent(operation)) return;
                    operation.IsWaiting = true;
                    operation.Stage = AcceptOperationStage.Waiting;
                    operationState = Describe(operation);
                    status = "Ready check — countdown";
                    changed = operation.Changed.Task;
                    wait = operation.Deadline - clockNow();
                    if (!initialWait) Emit(ControllerDecision.Scheduled, operation);
                }
                // Wake on a live edit without creating a second accept owner. Cancel the superseded timer.
                if (wait > TimeSpan.Zero || initialWait)
                {
                    initialWait = false;
                    using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        var timer = delayAsync(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, waiting.Token);
                        var winner = await Task.WhenAny(timer, changed).ConfigureAwait(false);
                        waiting.Cancel();
                        if (winner == changed)
                        {
                            lock (gate) { if (operation.Deadline > clockNow()) continue; }
                        }
                        token.ThrowIfCancellationRequested();
                    }
                }
                double minimumElapsed;
                long verifiedSchedule;
                lock (gate)
                {
                    if (!IsCurrent(operation)) { return; }
                    if (clockNow() < operation.Deadline) continue;
                    verifiedSchedule = operation.ScheduleRevision;
                    operation.IsWaiting = false;
                    operation.Stage = AcceptOperationStage.Verifying;
                    operationState = Describe(operation);
                    status = "Verifying ready check";
                    minimumElapsed = operation.InitialElapsedSeconds +
                        Math.Max(0, (clockNow() - operation.DetectedAt).TotalSeconds);
                    Emit(ControllerDecision.Verifying, operation);
                }
                var snapshot = await operation.Client.ReadAsync(token).ConfigureAwait(false);
                Task<int?> accepting;
                lock (gate)
                {
                    var failures = EligibilityFailure.None;
                    if (!IsCurrent(operation)) failures |= EligibilityFailure.StaleOperation;
                    if (!snapshot.IsPending) failures |= EligibilityFailure.NotPending;
                    // Compare only the operation's own session timer, never a replacement session's timer.
                    var last = ReferenceEquals(pending, operation) ? lastElapsed : operation.LastElapsed;
                    if (snapshot.ElapsedSeconds < last) failures |= EligibilityFailure.TimerBackwards;
                    if (snapshot.ElapsedSeconds < minimumElapsed) failures |= EligibilityFailure.InsufficientTimer;
                    Emit(ControllerDecision.Verification, operation, failures: failures, lastTimer: last,
                        returnedTimer: snapshot.ElapsedSeconds, requiredTimer: minimumElapsed);
                    if (failures != EligibilityFailure.None)
                    {
                        if (IsCurrent(operation)) status = "Check no longer eligible — accept manually";
                        return;
                    }
                    if (verifiedSchedule != operation.ScheduleRevision || clockNow() < operation.Deadline)
                    {
                        // A live edit while the final read was held requires a new wait and a fresh verification.
                        operation.IsWaiting = true;
                        continue;
                    }
                    // Invocation and cancellation remain serialized, and uncertain outcomes are never retried.
                    status = "Sending accept";
                    operation.Stage = AcceptOperationStage.Dispatched;
                    operation.DispatchedAt = clockNow();
                    operationState = Describe(operation);
                    Emit(ControllerDecision.Dispatched, operation);
                    accepting = operation.Client.AcceptWithStatusAsync(token);
                }
                var statusCode = await accepting.ConfigureAwait(false);
                lock (gate)
                {
                    operation.Outcome = AcceptRequestOutcome.CompletedUnconfirmed;
                    if (ReferenceEquals(pending, operation)) operationState = Describe(operation);
                    Emit(ControllerDecision.Completed, operation, latency: RequestLatency(operation), httpStatus: statusCode);
                    if (IsCurrent(operation)) { status = "Accept sent"; }
                }
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (gate) { RecordFailure(operation, AcceptFailureCategory.Cancelled); }
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            lock (gate)
            {
                RecordFailure(operation, error switch
                {
                    HttpRequestException => AcceptFailureCategory.Http,
                    IOException => AcceptFailureCategory.Io,
                    InvalidDataException => AcceptFailureCategory.InvalidData,
                    _ => AcceptFailureCategory.Timeout
                }, error is HttpRequestException http ? (int?)http.StatusCode : null);
                if (IsCurrent(operation)) { status = "Auto-accept failed — accept manually"; }
            }
        }
        finally
        {
            lock (gate)
            {
                operation.Stage = AcceptOperationStage.Finished;
                if (ReferenceEquals(pending, operation)) { operationState = Describe(operation); pending = null; }
                Emit(ControllerDecision.Finished, operation);
                operation.Cancellation.Dispose();
            }
        }
    }

    private void RecordFailure(PendingAccept operation, AcceptFailureCategory category, int? status = null)
    {
        operation.Outcome = operation.DispatchedAt is not null ? AcceptRequestOutcome.Unknown : AcceptRequestOutcome.Failed;
        if (ReferenceEquals(pending, operation)) operationState = Describe(operation);
        Emit(ControllerDecision.Failed, operation, latency: RequestLatency(operation), failure: category, httpStatus: status);
    }

    private double? RequestLatency(PendingAccept operation) => operation.DispatchedAt is { } start
        ? Math.Max(0, (clockNow() - start).TotalMilliseconds) : null;

    private static OperationDiagnosticState Describe(PendingAccept operation) => new("available", operation.Session,
        operation.CheckId, operation.AttemptId, operation.Stage, operation.Delay.TotalSeconds, operation.Deadline, operation.Outcome);

    private void FlushDuplicates()
    {
        if (duplicateCount == 0) return;
        Emit(ControllerDecision.Duplicates);
        duplicateCount = 0; firstDuplicateAt = lastDuplicateAt = null;
    }

    private void Emit(ControllerDecision decision, PendingAccept? operation = null,
        bool? previousArmed = null, double? previousDelay = null, long? eventGeneration = null,
        bool stateOnly = false, AcceptCancellationReason cancellation = AcceptCancellationReason.None,
        EligibilityFailure failures = EligibilityFailure.None, double? lastTimer = null,
        double? returnedTimer = null, double? requiredTimer = null, double? latency = null,
        AcceptFailureCategory failure = AcceptFailureCategory.None, int? httpStatus = null)
    {
        if (diagnostics is null) return;
        var snapshot = new ReadyCheckDiagnosticState("available", session, checkId, hasBaseline,
            ParseState(observed?.State), ParseResponse(observed?.PlayerResponse), SafeTimer(observed?.ElapsedSeconds), observedAt,
            duplicateCount, firstDuplicateAt, lastDuplicateAt);
        diagnostics.ObserveController(new(++revision, clockNow(), decision, snapshot,
            new("available", armed), new("available", acceptDelay.TotalSeconds), operationState with
            { RemainingSeconds = pending is { IsWaiting: true } ? Math.Max(0, (pending.Deadline - clockNow()).TotalSeconds) : null },
            eventGeneration ?? operation?.Session ?? session, eventGeneration.HasValue ? null : operation is null ? checkId : operation.CheckId, operation?.AttemptId,
            previousArmed, previousDelay, cancellation, failures, SafeTimer(operation?.InitialElapsedSeconds),
            SafeTimer(lastTimer), SafeTimer(returnedTimer), operation is null ? null : Math.Max(0, (clockNow() - (operation.Deadline - operation.Delay)).TotalSeconds),
            SafeTimer(requiredTimer), latency, httpStatus is >= 100 and <= 599 ? httpStatus : null, failure, operation?.Stage, operation?.Outcome), stateOnly);
    }

    private static double? SafeTimer(double? value) => value is { } number && double.IsFinite(number) && number >= 0 ? number : null;

    private static Func<DateTimeOffset> CreateMonotonicClock()
    {
        var start = Stopwatch.GetTimestamp(); var utc = DateTimeOffset.UtcNow;
        return () => utc + Stopwatch.GetElapsedTime(start);
    }

    private static ReadyCheckValue ParseState(string? value) => value switch
    {
        "Invalid" => ReadyCheckValue.Invalid, "InProgress" => ReadyCheckValue.InProgress,
        "EveryoneReady" => ReadyCheckValue.EveryoneReady, "StrangerNotReady" => ReadyCheckValue.StrangerNotReady,
        "PartyNotReady" => ReadyCheckValue.PartyNotReady, "Error" => ReadyCheckValue.Error, _ => ReadyCheckValue.Unknown
    };
    private static PlayerResponseValue ParseResponse(string? value) => value switch
    {
        "None" => PlayerResponseValue.None, "Accepted" => PlayerResponseValue.Accepted,
        "Declined" => PlayerResponseValue.Declined, _ => PlayerResponseValue.Unknown
    };

    private bool IsCurrent(PendingAccept operation)
        => !disposed && armed && session == operation.Session && ReferenceEquals(pending, operation)
            && !operation.Cancellation.IsCancellationRequested;

    private void CancelPending(AcceptCancellationReason reason)
    {
        var previous = pending;
        if (previous is null) return;
        previous.LastElapsed = lastElapsed;
        if (previous.DispatchedAt is not null) previous.Outcome = AcceptRequestOutcome.Unknown;
        operationState = Describe(previous);
        Emit(ControllerDecision.Cancelled, previous, cancellation: reason);
        pending = null;
        operationState = Describe(previous) with { Stage = AcceptOperationStage.Finished };
        previous.Cancellation.Cancel();
    }

    public Task DisconnectAsync()
    {
        lock (gate)
        {
            FlushDuplicates();
            CancelPending(disposed ? AcceptCancellationReason.Disposed : AcceptCancellationReason.Disconnected);
            session++;
            client = null;
            inCheck = false; hasBaseline = false; observed = null; observedAt = null; checkId = null;
            Emit(ControllerDecision.Disconnected);
            status = armed ? "Armed — League disconnected" : "Disarmed";
            return Task.WhenAll(work.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task finishing;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            armed = false;
            finishing = DisconnectAsync();
        }
        await finishing.ConfigureAwait(false);
    }

    private sealed record PendingAccept(long Session, IReadyCheckClient Client, DateTimeOffset DetectedAt,
        double InitialElapsedSeconds, TimeSpan InitialDelay)
    {
        public DateTimeOffset Deadline { get; set; } = DetectedAt + InitialDelay;
        public TimeSpan Delay { get; set; } = InitialDelay;
        public TaskCompletionSource Changed { get; set; } = new();
        public long ScheduleRevision { get; set; }
        public long AttemptId { get; set; }
        public long? CheckId { get; set; }
        public AcceptOperationStage Stage { get; set; } = AcceptOperationStage.Waiting;
        public AcceptRequestOutcome Outcome { get; set; }
        public DateTimeOffset? DispatchedAt { get; set; }
        public double LastElapsed { get; set; } = InitialElapsedSeconds;
        public bool IsWaiting { get; set; } = true;
        public CancellationTokenSource Cancellation { get; } = new();
    }
}
