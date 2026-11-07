using System.Text.Json;
using AFKtor.Core.Diagnostics;

namespace AFKtor.Diagnostics;

public static class DiagnosticHistoryLimits
{
    public const long TotalBytes = 20_000_000;
    public const int SegmentBytes = 65_536;
    public const int Segments = 512;
    public const int Events = 32_768;
    public const int Runs = 128;
    public const int MetadataBytes = 262_144;
    public const int DirectoryEntries = 1024;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
}

/// <summary>Single-owner, bounded JSONL history. All I/O runs behind the collector/API isolation boundaries.</summary>
public sealed partial class DiagnosticHistoryStore : IDiagnosticSink, IDiagnosticHistory, IDiagnosticSinkHealth, IAsyncDisposable
{
    private readonly string directory;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task maintenance;
    private readonly List<Segment> segments = [];
    private readonly Dictionary<Guid, RunMetadata> runs = [];
    private readonly HashSet<string> gaps = [];
    private FileStream? ownership;
    private bool initialized, dirty;
    private int disposed, failures;
    private DateTimeOffset retryAfter;
    private long dropped, truncated, deleted;
    private DiagnosticPersistenceStatus status = new("starting", null, 0, 0, 0, []);
    public DiagnosticPersistenceStatus Status => Volatile.Read(ref status);

    public DiagnosticHistoryStore(string directory, TimeProvider? timeProvider = null)
    {
        this.directory = directory;
        clock = timeProvider ?? TimeProvider.System;
        maintenance = Task.Run(MaintainAsync);
    }

    public async ValueTask WriteAsync(DiagnosticEvent record, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed != 0 || !EnsureInitialized(cancellationToken)) { Drop(record.RunId); return; }
            cancellationToken.ThrowIfCancellationRequested();
            var run = Register(record);
            if (record.Timestamp < clock.GetUtcNow() - DiagnosticHistoryLimits.Lifetime)
            { run.Gaps.Add("expired"); dirty = true; Drop(record.RunId); Cleanup(0, cancellationToken); SaveMetadata(cancellationToken); return; }
            if (segments.Any(s => s.Events.Any(e => e.RunId == record.RunId && e.Sequence == record.Sequence))) return;
            var encoded = Encode(record);
            record = encoded.Record;
            if (record.Truncated) truncated++;
            Cleanup(encoded.Bytes.Length, cancellationToken);
            while (segments.Sum(s => s.Events.Count) >= DiagnosticHistoryLimits.Events)
                RemoveSegment(segments.MinBy(s => s.Oldest)!, "evicted");
            var segment = segments.LastOrDefault(s => s.RunId == record.RunId && s.Appendable && s.Bytes + encoded.Bytes.Length <= DiagnosticHistoryLimits.SegmentBytes);
            if (segment is null)
            {
                while (segments.Count >= DiagnosticHistoryLimits.Segments || segments.Sum(s => s.Events.Count) >= DiagnosticHistoryLimits.Events)
                    RemoveSegment(segments.MinBy(s => s.Oldest)!, "evicted");
                var path = Path.Combine(directory, $"{record.RunId:N}-{record.Sequence:D20}-{Guid.NewGuid():N}.jsonl");
                segment = new Segment(path, record.RunId);
                segments.Add(segment);
            }
            // New writes never append to a recovered segment (its final framing may be damaged).
            try
            {
                await using var file = new FileStream(segment.Path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
                await file.WriteAsync(encoded.Bytes, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            catch { segment.Appendable = false; throw; }
            segment.Bytes += encoded.Bytes.Length;
            segment.Events.Add(record);
            run.Watermark = Math.Max(run.Watermark, record.Sequence);
            dirty = true;
            SaveMetadata(cancellationToken);
            Healthy();
        }
        catch (OperationCanceledException) { Drop(record.RunId); throw; }
        catch (Exception) { Drop(record.RunId); Fail("storageUnavailable"); }
        finally { gate.Release(); }
    }

    public DiagnosticRun[] GetRuns() => GetRuns(CancellationToken.None);
    public DiagnosticRun[] GetRuns(CancellationToken cancellationToken)
    {
        gate.Wait(cancellationToken);
        try
        {
            if (!PrepareRead(cancellationToken)) return [];
            return runs.Values.OrderByDescending(r => r.StartedAt).ThenBy(r => r.RunId)
                .Select(r => new DiagnosticRun(1, r.RunId, r.Build, r.StartedAt, false, "persistedPrefixOnly")).ToArray();
        }
        finally { gate.Release(); }
    }

    public DiagnosticEventPage? GetEvents(Guid runId, long afterSequence, int limit) => GetEvents(runId, afterSequence, limit, CancellationToken.None);
    public DiagnosticEventPage? GetEvents(Guid runId, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        if (afterSequence < 0 || limit is < 1 or > DiagnosticLimits.PageEvents) throw new ArgumentOutOfRangeException(nameof(limit));
        gate.Wait(cancellationToken);
        try
        {
            if (!PrepareRead(cancellationToken) || !runs.TryGetValue(runId, out var run)) return null;
            var all = segments.SelectMany(s => s.Events).Where(e => e.RunId == runId && e.Timestamp >= clock.GetUtcNow() - DiagnosticHistoryLimits.Lifetime)
                .DistinctBy(e => e.Sequence).OrderBy(e => e.Sequence).ToArray();
            var records = all.Where(e => e.Sequence > afterSequence).Take(limit).ToArray();
            var cursor = records.LastOrDefault()?.Sequence ?? afterSequence;
            var reasons = run.Gaps.Concat(gaps).Distinct().Order().ToArray();
            var first = all.FirstOrDefault()?.Sequence;
            if (run.Watermark > (all.LastOrDefault()?.Sequence ?? 0) || first > 1 || all.Zip(all.Skip(1), (a, b) => b.Sequence != a.Sequence + 1).Any(x => x))
                reasons = reasons.Append("sequenceGap").Distinct().ToArray();
            return new(1, runId, records, cursor, first, all.LastOrDefault()?.Sequence, run.Watermark,
                all.Any(e => e.Sequence > cursor), reasons.Contains("expired") || reasons.Contains("evicted"), run.Dropped,
                records.Any(e => e.Truncated), reasons.Length > 0 ? "incomplete" : "persistedPrefixOnly", reasons,
                Status.Availability, all.LastOrDefault()?.Sequence);
        }
        finally { gate.Release(); }
    }

    private bool PrepareRead(CancellationToken token)
    {
        if (disposed != 0 || (!initialized && !EnsureInitialized(token))) return false;
        if (clock.GetUtcNow() < retryAfter) return true; // Keep the age-filtered index readable without retrying I/O.
        try { Cleanup(0, token); SaveMetadata(token); Healthy(); return true; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { Fail("storageUnavailable"); return true; } // Already indexed evidence remains readable, filtered by age.
    }

    private void Healthy()
    {
        failures = 0; retryAfter = default;
        Volatile.Write(ref status, new("available", null, dropped, truncated, deleted, gaps.Order().ToArray()));
    }
    private void Fail(string category)
    {
        failures = Math.Min(failures + 1, 7);
        retryAfter = clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(60, 1 << (failures - 1)));
        Volatile.Write(ref status, new("unavailable", category, dropped, truncated, deleted, gaps.Order().ToArray()));
    }
    private void Drop(Guid runId)
    {
        dropped++; gaps.Add("unpersisted"); dirty = true;
        if (runs.TryGetValue(runId, out var run)) { run.Dropped++; run.Gaps.Add("unpersisted"); dirty = true; }
        Volatile.Write(ref status, Status with { Dropped = dropped, Gaps = gaps.Order().ToArray() });
    }
    private async Task MaintainAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(DiagnosticHistoryLimits.CleanupInterval, clock);
            while (await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
            {
                await gate.WaitAsync(stopping.Token).ConfigureAwait(false);
                try { PrepareRead(stopping.Token); }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Fail("storageUnavailable"); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        await gate.WaitAsync().ConfigureAwait(false);
        try { ownership?.Dispose(); ownership = null; }
        finally { gate.Release(); }
        await maintenance.ConfigureAwait(false);
    }

    private sealed class Segment(string path, Guid runId)
    {
        public string Path { get; } = path;
        public Guid RunId { get; } = runId;
        public long Bytes { get; set; }
        public bool Appendable { get; set; } = true;
        public List<DiagnosticEvent> Events { get; } = [];
        public DateTimeOffset Oldest => Events.Count == 0 ? DateTimeOffset.MinValue : Events.Min(e => e.Timestamp);
    }
    public sealed class RunMetadata
    {
        public Guid RunId { get; set; }
        public string Build { get; set; } = "unavailable";
        public DateTimeOffset StartedAt { get; set; }
        public long Watermark { get; set; }
        public long Dropped { get; set; }
        public HashSet<string> Gaps { get; set; } = [];
    }
    private sealed record Manifest(RunMetadata[] Runs, string[] Gaps);
}
