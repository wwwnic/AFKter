using System.Diagnostics;

namespace AFKtor.Core;

/// <summary>Owns arming and one attempt per uninterrupted ready-check lifecycle.</summary>
public sealed class ReadyCheckController(
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<DateTimeOffset>? now = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync = delay ?? Task.Delay;
    // Only elapsed durations are used. Keep production countdowns independent
    // of wall-clock corrections; tests supply a manually advanced clock.
    private readonly Func<DateTimeOffset> clockNow = now ?? (() => DateTimeOffset.UnixEpoch + Stopwatch.GetElapsedTime(0));
    private readonly List<Task> work = [];
    private IReadyCheckClient? client;
    private long session;
    private bool armed;
    private bool inCheck;
    private bool hasBaseline;
    private bool disposed;
    private double lastElapsed;
    private PendingAccept? pending;
    private string status = "Disarmed";

    public bool IsArmed { get { lock (gate) { return armed; } } }
    public string Status { get { lock (gate) { return status; } } }
    public TimeSpan? Remaining
    {
        get
        {
            lock (gate)
            {
                return pending is null ? null : TimeSpan.FromSeconds(Math.Max(0, (pending.Deadline - clockNow()).TotalSeconds));
            }
        }
    }

    public void SetArmed(bool value)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            armed = value;
            if (!value) { CancelPending(); }
            status = value ? "Armed — waiting for next check" : "Disarmed";
        }
    }

    public long BeginSession(IReadyCheckClient readyCheckClient)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            CancelPending();
            client = readyCheckClient;
            inCheck = false;
            hasBaseline = false;
            lastElapsed = 0;
            status = armed ? "Armed — waiting for next check" : "Disarmed";
            return ++session;
        }
    }

    public void Update(long connectionSession, ReadyCheckUpdate update)
    {
        lock (gate)
        {
            if (disposed || connectionSession != session || client is null) { return; }
            var snapshot = update.Snapshot;
            bool initial = !hasBaseline || update.IsInitial;
            hasBaseline = true;
            if (!snapshot.IsPending)
            {
                CancelPending();
                // An answered check remains observed until its lifecycle ends.
                inCheck = snapshot.State is "InProgress" or "EveryoneReady";
                lastElapsed = snapshot.ElapsedSeconds;
                status = armed ? "Armed — waiting for next check" : "Disarmed";
                return;
            }
            if (inCheck)
            {
                if (snapshot.ElapsedSeconds < lastElapsed)
                {
                    CancelPending();
                    status = "Check uncertain — accept manually";
                }
                lastElapsed = snapshot.ElapsedSeconds;
                return;
            }
            inCheck = true;
            lastElapsed = snapshot.ElapsedSeconds;
            if (!armed || initial) { return; }
            var operation = new PendingAccept(session, client, clockNow().AddSeconds(3), snapshot.ElapsedSeconds);
            pending = operation;
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
            await delayAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            double minimumElapsed;
            lock (gate)
            {
                if (!IsCurrent(operation)) { return; }
                status = "Verifying ready check";
                // Timer compatibility must include the local elapsed countdown,
                // even if its continuation was delayed by a busy machine.
                minimumElapsed = operation.InitialElapsedSeconds +
                    Math.Max(3, 3 + (clockNow() - operation.Deadline).TotalSeconds);
            }
            var snapshot = await operation.Client.ReadAsync(token).ConfigureAwait(false);
            Task accepting;
            lock (gate)
            {
                if (!IsCurrent(operation)) { return; }
                if (!snapshot.IsPending || snapshot.ElapsedSeconds < lastElapsed ||
                    snapshot.ElapsedSeconds < minimumElapsed)
                {
                    status = "Check no longer eligible — accept manually";
                    return;
                }
                // Invocation and cancellation are serialized. Once dispatched, an
                // HTTP request cannot be recalled; never retry an ambiguous result.
                status = "Sending accept";
                accepting = operation.Client.AcceptAsync(token);
            }
            await accepting.ConfigureAwait(false);
            lock (gate)
            {
                if (IsCurrent(operation)) { status = "Accept sent"; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            lock (gate)
            {
                if (IsCurrent(operation)) { status = "Auto-accept failed — accept manually"; }
            }
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(pending, operation)) { pending = null; }
                operation.Cancellation.Dispose();
            }
        }
    }

    private bool IsCurrent(PendingAccept operation)
        => !disposed && armed && session == operation.Session && ReferenceEquals(pending, operation)
            && !operation.Cancellation.IsCancellationRequested;

    private void CancelPending()
    {
        var previous = pending;
        pending = null;
        previous?.Cancellation.Cancel();
    }

    public Task DisconnectAsync()
    {
        lock (gate)
        {
            CancelPending();
            session++;
            client = null;
            inCheck = false;
            status = armed ? "Armed — League disconnected" : "Disarmed";
            return Task.WhenAll(work.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task finishing;
        lock (gate)
        {
            disposed = true;
            armed = false;
            finishing = DisconnectAsync();
        }
        await finishing.ConfigureAwait(false);
    }

    private sealed record PendingAccept(long Session, IReadyCheckClient Client, DateTimeOffset Deadline, double InitialElapsedSeconds)
    {
        public CancellationTokenSource Cancellation { get; } = new();
    }
}
