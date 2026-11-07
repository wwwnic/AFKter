using System.Text.Json;
using AFKtor.Core.Diagnostics;

namespace AFKtor.Diagnostics;

public sealed partial class DiagnosticHistoryStore
{
    private static readonly HashSet<string> KnownGaps = ["expired", "evicted", "corruption", "unpersisted", "sequenceGap"];

    private bool EnsureInitialized(CancellationToken token)
    {
        if (clock.GetUtcNow() < retryAfter) return false;
        if (initialized) return true;
        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            ownership ??= new FileStream(Path.Combine(directory, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { Fail("ownershipUnavailable"); return false; }
        try
        {
            segments.Clear(); runs.Clear();
            LoadMetadata();
            // Count every file, even unknown/damaged leftovers, before allocating a recovery index.
            Cleanup(0, token);
            foreach (var file in Inventory().Where(f => f.Extension == ".jsonl").OrderBy(f => f.LastWriteTimeUtc))
            {
                token.ThrowIfCancellationRequested();
                Recover(file, token);
                while (segments.Count > DiagnosticHistoryLimits.Segments)
                    RemoveSegment(segments.MinBy(s => s.Oldest)!, "evicted");
            }
            Cleanup(0, token);
            SaveMetadata(token);
            initialized = true;
            Healthy();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { Fail("storageUnavailable"); return false; }
    }

    private void Recover(FileInfo file, CancellationToken token)
    {
        var runId = Guid.TryParse(file.Name.Split('-')[0], out var parsed) ? parsed : Guid.Empty;
        var segment = new Segment(file.FullName, runId) { Bytes = file.Length, Appendable = false };
        segments.Add(segment);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) { Corrupt(runId); return; }
        using var stream = file.OpenRead();
        var line = new byte[DiagnosticLimits.EventBytes];
        var input = new byte[4096];
        var length = 0;
        var overflow = false;
        int read;
        while ((read = stream.Read(input)) != 0)
        {
            token.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++)
            {
                if (input[i] != (byte)'\n')
                {
                    if (length < line.Length) line[length++] = input[i]; else overflow = true;
                    continue;
                }
                if (overflow || length == 0 || length + 1 > DiagnosticLimits.EventBytes) Corrupt(runId);
                else
                {
                    try
                    {
                        var record = JsonSerializer.Deserialize<DiagnosticEvent>(line.AsSpan(0, length), DiagnosticJson.Options);
                        if (record is null || record.SchemaVersion != 1 || record.RunId == Guid.Empty || record.Sequence < 1 ||
                            record.RunId != runId || record.Build is null || record.Build.Length > 128 || record.Details is null ||
                            !double.IsFinite(record.ElapsedMilliseconds)) throw new JsonException();
                        while (segments.Sum(s => s.Events.Count) >= DiagnosticHistoryLimits.Events)
                        {
                            var oldest = segments.Where(s => s != segment).MinBy(s => s.Oldest);
                            if (oldest is null) { Corrupt(runId); return; }
                            RemoveSegment(oldest, "evicted");
                        }
                        {
                            var run = Register(record);
                            run.Watermark = Math.Max(run.Watermark, record.Sequence);
                            segment.Events.Add(record);
                        }
                    }
                    catch (JsonException) { Corrupt(runId); }
                }
                length = 0; overflow = false;
            }
        }
        if (length != 0 || overflow) Corrupt(runId);
    }

    private void Corrupt(Guid runId)
    {
        gaps.Add("corruption");
        if (runs.TryGetValue(runId, out var run)) run.Gaps.Add("corruption");
        dirty = true;
    }

    private RunMetadata Register(DiagnosticEvent record)
    {
        if (runs.TryGetValue(record.RunId, out var run)) return run;
        while (runs.Count >= DiagnosticHistoryLimits.Runs)
        {
            var oldest = runs.Values.MinBy(r => r.StartedAt)!;
            foreach (var segment in segments.Where(s => s.RunId == oldest.RunId).ToArray()) RemoveSegment(segment, "evicted");
            runs.Remove(oldest.RunId);
            gaps.Add("evicted");
        }
        run = new() { RunId = record.RunId, Build = Bound(record.Build, 128), StartedAt = record.Timestamp };
        runs.Add(run.RunId, run); dirty = true;
        return run;
    }

    private void LoadMetadata()
    {
        var path = Path.Combine(directory, "metadata.json");
        if (!File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).Length > DiagnosticHistoryLimits.MetadataBytes) throw new JsonException();
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(path), DiagnosticJson.Options);
            if (manifest is null || manifest.Runs.Length > DiagnosticHistoryLimits.Runs || manifest.Gaps.Length > KnownGaps.Count) throw new JsonException();
            foreach (var run in manifest.Runs)
            {
                if (run.RunId == Guid.Empty || run.Build is null || run.Build.Length > 128 || run.Gaps is null ||
                    run.Gaps.Any(g => !KnownGaps.Contains(g))) throw new JsonException();
                runs[run.RunId] = run;
            }
            foreach (var gap in manifest.Gaps) if (KnownGaps.Contains(gap)) gaps.Add(gap);
        }
        catch (Exception) { runs.Clear(); Corrupt(Guid.Empty); }
    }

    private static (DiagnosticEvent Record, byte[] Bytes) Encode(DiagnosticEvent record)
    {
        byte[] Serialize(DiagnosticEvent value)
        {
            using var stream = new RecordBuffer();
            JsonSerializer.Serialize(stream, value, DiagnosticJson.Options);
            stream.WriteByte((byte)'\n');
            return stream.ToArray();
        }
        // Bound text before JSON serialization so its internal UTF-8 buffer cannot grow with
        // an oversized future caller's safe fields. The fallback still enforces encoded size.
        var bounded = record with { Build = Bound(record.Build, 128), Code = Bound(record.Code, 128),
            Source = Bound(record.Source, 64), Severity = Bound(record.Severity, 32) };
        if (bounded != record) bounded = bounded with { Truncated = true };
        record = bounded;
        try { return (record, Serialize(record)); }
        catch (RecordTooLargeException)
        {
            // The whole typed detail object is omitted on overflow; no source-specific knowledge here.
            record = record with { Build = Bound(record.Build, 128), Code = Bound(record.Code, 128),
                Source = Bound(record.Source, 64), Severity = Bound(record.Severity, 32),
                Details = new(ConnectionStage.Unknown, 0, DiagnosticReason.None), Truncated = true };
            try { return (record, Serialize(record)); }
            catch (RecordTooLargeException)
            { record = record with { Build = "truncated", Code = "truncated", Source = "truncated", Severity = "information" }; return (record, Serialize(record)); }
        }
    }
    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max];
    private sealed class RecordTooLargeException : Exception;
    private sealed class RecordBuffer : MemoryStream
    {
        private void Check(int count) { if (Length + count > DiagnosticLimits.EventBytes) throw new RecordTooLargeException(); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }
}
