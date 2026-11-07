using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;

namespace AFKtor.Core.Diagnostics;

/// <summary>Sealed nonblocking ingress: producers invoke no injected callbacks, formatting or I/O.</summary>
public sealed class DiagnosticCollector : IAsyncDisposable, IDiagnosticHistory
{
    private readonly object gate = new();
    private readonly Queue<DiagnosticEvent> events = new(DiagnosticLimits.MemoryEvents);
    private readonly long startedTimestamp = Stopwatch.GetTimestamp();
    private readonly Channel<DiagnosticEvent>? queue;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task sinkWorker;
    private long sequence, producerDropped, sinkDropped, evicted;
    private int queueDepth, stopped;
    private string api = "starting", discovery = "starting", storage = "unavailable";
    private string? failure;
    private ConnectionDiagnosticState connection = new(ConnectionStage.Unknown, 0, DiagnosticReason.None, null, null);
    public Guid RunId { get; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public string Build { get; } = ReadBuild();

    private static string ReadBuild()
    {
        var value = typeof(DiagnosticCollector).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return value is { Length: > 0 and <= 128 } ? value : "unavailable";
    }

    public DiagnosticCollector(IDiagnosticSink? sink = null)
    {
        if (sink is not null)
        {
            queue = Channel.CreateBounded<DiagnosticEvent>(new BoundedChannelOptions(DiagnosticLimits.SinkQueue)
            { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
            storage = "starting";
            sinkWorker = Task.Run(() => DrainAsync(sink));
        }
        else sinkWorker = Task.CompletedTask;
        Publish(new(DiagnosticCode.AppStarted));
    }

    public void ConnectionChanged(ConnectionStage stage, long generation, DiagnosticReason reason) =>
        Publish(new(DiagnosticCode.ConnectionChanged, stage, generation, reason));

    public void Publish(DiagnosticObservation observation)
    {
        if (Volatile.Read(ref stopped) != 0) return;
        if (!Monitor.TryEnter(gate)) { Interlocked.Increment(ref producerDropped); return; }
        try
        {
            if (stopped != 0) return;
            if (!Enum.IsDefined(observation.Code) || !Enum.IsDefined(observation.Stage) || !Enum.IsDefined(observation.Reason) || observation.Generation < 0)
            { Interlocked.Increment(ref producerDropped); return; }
            if (observation.Code == DiagnosticCode.ConnectionChanged && connection.Stage == observation.Stage &&
                connection.Generation == observation.Generation && connection.Reason == observation.Reason) return;
            var now = DateTimeOffset.UtcNow;
            if (observation.Code == DiagnosticCode.ConnectionChanged)
                connection = new(observation.Stage, observation.Generation, observation.Reason, now,
                    observation.Stage == ConnectionStage.Connected ? now : connection.LastSuccessfulObservation);
            var (code, source) = observation.Code switch
            {
                DiagnosticCode.AppStarted => ("app.started", "app"),
                DiagnosticCode.AppStopped => ("app.stopped", "app"),
                _ => ("connection.changed", "session")
            };
            var record = new DiagnosticEvent(1, RunId, Build, ++sequence, now,
                Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds, code, "information", source,
                new(observation.Stage, observation.Generation, observation.Reason));
            if (events.Count == DiagnosticLimits.MemoryEvents) { events.Dequeue(); evicted++; }
            events.Enqueue(record);
            if (queue is not null)
            {
                Interlocked.Increment(ref queueDepth);
                if (!queue.Writer.TryWrite(record)) { Interlocked.Decrement(ref queueDepth); Interlocked.Increment(ref sinkDropped); }
            }
        }
        finally { Monitor.Exit(gate); }
    }

    public DiagnosticState GetState()
    {
        lock (gate) return new(1, RunId, Build, StartedAt, DateTimeOffset.UtcNow, sequence, connection, new(), new(), new(), new(),
            new(Volatile.Read(ref api), Volatile.Read(ref discovery), Volatile.Read(ref storage), Volatile.Read(ref queueDepth),
                Interlocked.Read(ref producerDropped), Interlocked.Read(ref sinkDropped), evicted, 0, Volatile.Read(ref failure)));
    }

    public DiagnosticRun[] GetRuns() => [new(1, RunId, Build, StartedAt, true, "memoryOnly")];
    public DiagnosticEventPage? GetEvents(Guid runId, long afterSequence, int limit)
    {
        if (runId != RunId) return null;
        if (afterSequence < 0 || limit < 1 || limit > DiagnosticLimits.PageEvents) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (gate)
        {
            var records = events.Where(e => e.Sequence > afterSequence).Take(limit).ToArray();
            var first = events.Count == 0 ? (long?)null : events.Peek().Sequence;
            var cursor = records.Length == 0 ? afterSequence : records[^1].Sequence;
            var lost = Interlocked.Read(ref producerDropped);
            var gap = first is { } start && afterSequence < start - 1;
            return new(1, RunId, records, cursor, first, events.Count == 0 ? null : sequence, sequence,
                sequence > cursor, gap, lost, false, gap || lost > 0 ? "incomplete" : "currentRunMemoryOnly");
        }
    }

    // These health setters accept only a boolean and fixed categories, never raw exception metadata.
    public void SetApiHealth(bool available) { Volatile.Write(ref api, available ? "available" : "unavailable"); if (!available) Volatile.Write(ref failure, "apiUnavailable"); }
    public void SetDiscoveryHealth(bool available) { Volatile.Write(ref discovery, available ? "available" : "unavailable"); if (!available) Volatile.Write(ref failure, "discoveryUnavailable"); }

    private async Task DrainAsync(IDiagnosticSink sink)
    {
        try
        {
            await foreach (var record in queue!.Reader.ReadAllAsync(stopping.Token).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref queueDepth);
                try
                {
                    // Isolate even a synchronously blocking implementation from producer and shutdown threads.
                    await Task.Run(async () => await sink.WriteAsync(record, stopping.Token).ConfigureAwait(false))
                        .WaitAsync(TimeSpan.FromMilliseconds(DiagnosticLimits.SinkMilliseconds), stopping.Token).ConfigureAwait(false);
                    Volatile.Write(ref storage, "available");
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref sinkDropped);
                    Volatile.Write(ref storage, "unavailable");
                    Volatile.Write(ref failure, "sinkUnavailable");
                    queue.Writer.TryComplete();
                    while (queue.Reader.TryRead(out _)) { Interlocked.Decrement(ref queueDepth); Interlocked.Increment(ref sinkDropped); }
                    return; // One abandoned operation at most; never spawn unbounded retries.
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref stopped) != 0) return;
        Publish(new(DiagnosticCode.AppStopped));
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        queue?.Writer.TryComplete();
        try { await sinkWorker.WaitAsync(TimeSpan.FromMilliseconds(DiagnosticLimits.ShutdownMilliseconds)).ConfigureAwait(false); }
        catch (TimeoutException) { stopping.Cancel(); Volatile.Write(ref failure, "shutdownTimeout"); }
        // A timed-out sink may still hold the token: do not dispose its cancellation source.
    }
}
