using AFKtor.Core.Diagnostics;

namespace AFKtor.Diagnostics;

/// <summary>Memory wins duplicate keys; previous runs are evidence, never live snapshots.</summary>
public sealed class RetainedDiagnosticHistory(DiagnosticCollector collector, DiagnosticHistoryStore store, TimeProvider? timeProvider = null) : IDiagnosticHistory, IDiagnosticSinkHealth
{
    public DiagnosticPersistenceStatus Status => store.Status;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public DiagnosticRun[] GetRuns() => GetRuns(CancellationToken.None);
    public DiagnosticRun[] GetRuns(CancellationToken cancellationToken) => store.GetRuns(cancellationToken)
        .Where(r => r.RunId != collector.RunId).Prepend(collector.GetRuns().Single() with { Persistence = store.Status.Availability }).ToArray();

    public DiagnosticEventPage? GetEvents(Guid runId, long afterSequence, int limit) => GetEvents(runId, afterSequence, limit, CancellationToken.None);
    public DiagnosticEventPage? GetEvents(Guid runId, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        var disk = store.GetEvents(runId, afterSequence, limit, cancellationToken);
        if (runId != collector.RunId) return disk;
        var memory = collector.GetMemoryHistory();
        var cutoff = clock.GetUtcNow() - DiagnosticHistoryLimits.Lifetime;
        var retainedMemory = memory.Events.Where(e => e.Timestamp >= cutoff).ToArray();
        var events = retainedMemory.Concat(disk?.Events ?? []).Where(e => e.Sequence > afterSequence)
            .DistinctBy(e => e.Sequence).OrderBy(e => e.Sequence).Take(limit).ToArray();
        var cursor = events.LastOrDefault()?.Sequence ?? afterSequence;
        var reasons = (disk?.Gaps ?? []).Concat(store.Status.Gaps).ToHashSet();
        if (store.Status.Availability != "available") reasons.Add("storageUnavailable");
        if (memory.Events.Any(e => e.Timestamp < cutoff)) reasons.Add("expired");
        if (memory.DroppedEvents > 0) reasons.Add("producerDropped");
        // Memory eviction is repaired by disk when the requested prefix is present.
        if (events.FirstOrDefault() is { } first && first.Sequence > afterSequence + 1) reasons.Add("sequenceGap");
        var firstAvailable = new[] { disk?.FirstAvailableSequence, retainedMemory.FirstOrDefault()?.Sequence }
            .Where(s => s.HasValue).Select(s => s!.Value).DefaultIfEmpty().Min();
        var lastAvailable = new[] { disk?.LastAvailableSequence, retainedMemory.LastOrDefault()?.Sequence }
            .Where(s => s.HasValue).Select(s => s!.Value).DefaultIfEmpty().Max();
        if (firstAvailable > 1) reasons.Add("sequenceGap");
        if (events.Zip(events.Skip(1), (a, b) => b.Sequence != a.Sequence + 1).Any(g => g)) reasons.Add("sequenceGap");
        return new(1, runId, events, cursor, firstAvailable == 0 ? null : firstAvailable, lastAvailable == 0 ? null : lastAvailable,
            memory.SequenceWatermark, retainedMemory.Any(e => e.Sequence > cursor) ||
                (disk?.Events.Any(e => e.Sequence > cursor) ?? false) || (disk?.HasMore ?? false),
            reasons.Contains("expired") || reasons.Contains("evicted") || reasons.Contains("sequenceGap"), memory.DroppedEvents,
            events.Any(e => e.Truncated), reasons.Count > 0 ? "incomplete" : "currentRunMemoryAndDisk", reasons.Order().ToArray(),
            store.Status.Availability, disk?.PersistedThroughSequence);
    }
}
