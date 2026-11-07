using System.Text.Json;

namespace AFKtor.Diagnostics;

public sealed partial class DiagnosticHistoryStore
{
    private List<FileInfo> Inventory()
    {
        var files = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (files.Count == DiagnosticHistoryLimits.DirectoryEntries) throw new IOException();
            files.Add(new FileInfo(path));
        }
        return files;
    }

    private void Cleanup(int incomingBytes, CancellationToken token)
    {
        var cutoff = clock.GetUtcNow() - DiagnosticHistoryLimits.Lifetime;
        foreach (var segment in segments.Where(s => s.Oldest < cutoff).ToArray())
        { token.ThrowIfCancellationRequested(); RemoveSegment(segment, "expired"); }
        var files = Inventory();
        foreach (var damaged in files.Where(f => f.Name is "metadata.json" or "metadata.tmp")
            .Where(f => f.Length > DiagnosticHistoryLimits.MetadataBytes).ToArray())
        {
            token.ThrowIfCancellationRequested();
            File.Delete(damaged.FullName); files.Remove(damaged); Corrupt(Guid.Empty);
        }
        // Reserve both old and replacement metadata before every event/metadata write. These are
        // real files and included in Inventory too; conservative double reservation is intentional.
        var total = files.Sum(f => f.Length);
        var reserve = 2L * DiagnosticHistoryLimits.MetadataBytes;
        foreach (var file in files.Where(f => f.Name is not ("owner.lock" or "metadata.json" or "metadata.tmp"))
            .OrderBy(f => segments.FirstOrDefault(s => s.Path == f.FullName)?.Oldest ?? new DateTimeOffset(f.LastWriteTimeUtc)))
        {
            token.ThrowIfCancellationRequested();
            var segment = segments.FirstOrDefault(s => s.Path == file.FullName);
            var expired = segment is null && file.LastWriteTimeUtc < cutoff.UtcDateTime;
            if (!expired && total + incomingBytes + reserve <= DiagnosticHistoryLimits.TotalBytes) continue;
            var size = file.Length;
            if (segment is not null) RemoveSegment(segment, expired ? "expired" : "evicted");
            else { File.Delete(file.FullName); deleted++; gaps.Add(expired ? "expired" : "evicted"); dirty = true; }
            total -= size;
        }
        if (total + incomingBytes + reserve > DiagnosticHistoryLimits.TotalBytes) throw new IOException();
    }

    private void RemoveSegment(Segment segment, string reason)
    {
        File.Delete(segment.Path); // Never drop accounting/index entries until deletion succeeds.
        segments.Remove(segment);
        if (runs.TryGetValue(segment.RunId, out var run)) { run.Gaps.Add(reason); dirty = true; }
        gaps.Add(reason); dirty = true; deleted++;
    }

    private void SaveMetadata(CancellationToken token)
    {
        if (!dirty) return;
        token.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Manifest(runs.Values.OrderBy(r => r.StartedAt).ToArray(), gaps.Order().ToArray()), DiagnosticJson.Options);
        if (bytes.Length > DiagnosticHistoryLimits.MetadataBytes) throw new IOException();
        var path = Path.Combine(directory, "metadata.json");
        var temporary = Path.Combine(directory, "metadata.tmp");
        // Cleanup reserves enough for both files; interrupted replacement remains accounted on restart.
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(flushToDisk: true); }
        token.ThrowIfCancellationRequested();
        File.Move(temporary, path, overwrite: true);
        dirty = false;
    }
}
