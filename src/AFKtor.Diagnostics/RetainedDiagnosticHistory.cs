using AFKtor.Core.Diagnostics;

namespace AFKtor.Diagnostics;

/// <summary>Memory wins duplicate keys; previous runs are evidence, never live snapshots.</summary>
public sealed class RetainedDiagnosticHistory(DiagnosticCollector collector, DiagnosticHistoryStore store, TimeProvider? timeProvider = null) : IDiagnosticHistory, IDiagnosticSinkHealth
{
    public DiagnosticPersistenceStatus Status
    {
        get
        {
            var status = store.Status;
            var disabled = collector.SinkDisabled;
            if (!disabled && collector.SinkDropped == 0) return status;
            return status with { Availability = disabled ? "unavailable" : status.Availability,
                FailureCategory = disabled ? "sinkUnavailable" : status.FailureCategory,
                Gaps = status.Gaps.Append("unpersisted").Distinct().ToArray() };
        }
    }
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public DiagnosticRun[] GetRuns() => GetRuns(CancellationToken.None);
    public DiagnosticRun[] GetRuns(CancellationToken cancellationToken) => store.GetRuns(cancellationToken)
        .Where(r => r.RunId != collector.RunId).Prepend(collector.GetRuns().Single() with { Persistence = Status.Availability }).ToArray();

    public DiagnosticEventPage? GetEvents(Guid runId, long afterSequence, int limit) => GetEvents(runId, afterSequence, limit, CancellationToken.None);
    public DiagnosticEventPage? GetEvents(Guid runId, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        var (disk, retainedDisk) = store.GetEventSnapshot(runId, afterSequence, limit, cancellationToken);
        if (runId != collector.RunId) return disk;
        var memory = collector.GetMemoryHistory();
        var cutoff = clock.GetUtcNow() - DiagnosticHistoryLimits.Lifetime;
        var retainedMemory = memory.Events.Where(e => e.Timestamp >= cutoff).ToArray();
        var events = retainedMemory.Concat(disk?.Events ?? []).Where(e => e.Sequence > afterSequence)
            .DistinctBy(e => e.Sequence).OrderBy(e => e.Sequence).Take(limit).ToArray();
        var cursor = events.LastOrDefault()?.Sequence ?? afterSequence;
        var status = Status;
        var reasons = (disk?.Gaps ?? []).Concat(status.Gaps).ToHashSet();
        if (status.Availability != "available") reasons.Add("storageUnavailable");
        if (memory.Events.Any(e => e.Timestamp < cutoff)) reasons.Add("expired");
        if (memory.DroppedEvents > 0) reasons.Add("producerDropped");
        // Missing retained evidence must be visible even on an empty tail or an intact early page.
        if (HasSequenceGap(retainedDisk, retainedMemory)) reasons.Add("sequenceGap");
        var firstAvailable = new[] { disk?.FirstAvailableSequence, retainedMemory.FirstOrDefault()?.Sequence }
            .Where(s => s.HasValue).Select(s => s!.Value).DefaultIfEmpty().Min();
        var lastAvailable = new[] { disk?.LastAvailableSequence, retainedMemory.LastOrDefault()?.Sequence }
            .Where(s => s.HasValue).Select(s => s!.Value).DefaultIfEmpty().Max();
        return new(1, runId, events, cursor, firstAvailable == 0 ? null : firstAvailable, lastAvailable == 0 ? null : lastAvailable,
            memory.SequenceWatermark, retainedMemory.Any(e => e.Sequence > cursor) ||
                (disk?.Events.Any(e => e.Sequence > cursor) ?? false) || (disk?.HasMore ?? false),
            reasons.Contains("expired") || reasons.Contains("evicted") || reasons.Contains("sequenceGap"), memory.DroppedEvents,
            events.Any(e => e.Truncated), reasons.Count > 0 ? "incomplete" : "currentRunMemoryAndDisk", reasons.Order().ToArray(),
            status.Availability, disk?.PersistedThroughSequence);
    }

    private static bool HasSequenceGap(DiagnosticEvent[] disk, DiagnosticEvent[] memory)
    {
        // Both snapshots are sequence ordered. A linear merge inspects at most 32,768 + 1,024
        // existing records, without another allocation, storage pass, or invented drop positions.
        int d = 0, m = 0;
        long previous = 0;
        while (d < disk.Length || m < memory.Length)
        {
            long next;
            if (m == memory.Length || d < disk.Length && disk[d].Sequence <= memory[m].Sequence)
                next = disk[d++].Sequence;
            else next = memory[m++].Sequence;
            if (next > previous && next - previous > 1) return true;
            previous = next;
        }
        return false;
    }
}
