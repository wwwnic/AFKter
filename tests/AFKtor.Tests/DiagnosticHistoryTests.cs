using System.Text;
using System.Text.Json;
using AFKtor.Core.Diagnostics;
using AFKtor.Diagnostics;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class DiagnosticHistoryTests
{
    [Test]
    public async Task RestartRecoversOrderedEvidenceWithoutInventingShutdown()
    {
        using var directory = new Temp();
        var run = Guid.NewGuid();
        var clock = new Clock();
        await using (var store = new DiagnosticHistoryStore(directory.Path, clock))
        {
            await store.WriteAsync(Event(run, 1, clock.GetUtcNow()), default);
            await store.WriteAsync(Event(run, 2, clock.GetUtcNow()), default);
        }
        await using var recovered = new DiagnosticHistoryStore(directory.Path, clock);
        Assert.That(recovered.GetRuns().Single().RunId, Is.EqualTo(run));
        var page = recovered.GetEvents(run, 0, 100)!;
        Assert.That(page.Events.Select(e => e.Sequence), Is.EqualTo(new long[] { 1, 2 }));
        Assert.That(page.Completeness, Is.EqualTo("persistedPrefixOnly"));
        Assert.That(page.Events.Any(e => e.Code == "app.stopped"), Is.False);
        Assert.That(page.NextCursor, Is.EqualTo(2));
    }

    [Test]
    public async Task CorruptAndIncompleteLinesAreSkippedAndReportedAfterRestart()
    {
        using var directory = new Temp();
        var run = Guid.NewGuid(); var clock = new Clock();
        await using (var store = new DiagnosticHistoryStore(directory.Path, clock))
            await store.WriteAsync(Event(run, 1, clock.GetUtcNow()), default);
        var path = Directory.GetFiles(directory.Path, "*.jsonl").Single();
        await File.AppendAllTextAsync(path, "{broken}\n{incomplete");
        await using var recovered = new DiagnosticHistoryStore(directory.Path, clock);
        var page = recovered.GetEvents(run, 0, 100)!;
        Assert.That(page.Events, Has.Length.EqualTo(1));
        Assert.That(page.Gaps, Does.Contain("corruption"));
        Assert.That(page.Completeness, Is.EqualTo("incomplete"));
    }

    [Test]
    public async Task ExpiryRemovesMixedAgeSegmentAndLeavesBoundedRunTombstoneAcrossRestart()
    {
        using var directory = new Temp(); var clock = new Clock(); var run = Guid.NewGuid();
        await using (var store = new DiagnosticHistoryStore(directory.Path, clock))
        {
            await store.WriteAsync(Event(run, 1, clock.GetUtcNow()), default);
            clock.Advance(TimeSpan.FromDays(1));
            await store.WriteAsync(Event(run, 2, clock.GetUtcNow()), default);
            clock.Advance(TimeSpan.FromDays(6.1));
            var page = store.GetEvents(run, 0, 100)!;
            Assert.That(page.Events, Is.Empty);
            Assert.That(page.Gaps, Does.Contain("expired"));
            Assert.That(page.HistoryEvicted, Is.True);
        }
        await using var recovered = new DiagnosticHistoryStore(directory.Path, clock);
        Assert.That(recovered.GetEvents(run, 0, 100)!.Gaps, Does.Contain("expired"));
    }

    [Test]
    public async Task ConcurrentOwnerDegradesAndReleasedOwnershipCanRecover()
    {
        using var directory = new Temp(); var clock = new Clock(); var run = Guid.NewGuid();
        var first = new DiagnosticHistoryStore(directory.Path, clock);
        await first.WriteAsync(Event(run, 1, clock.GetUtcNow()), default);
        await using var second = new DiagnosticHistoryStore(directory.Path, clock);
        await second.WriteAsync(Event(Guid.NewGuid(), 1, clock.GetUtcNow()), default);
        Assert.That(second.Status.Availability, Is.EqualTo("unavailable"));
        Assert.That(second.Status.FailureCategory, Is.EqualTo("ownershipUnavailable"));
        await first.DisposeAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        await second.WriteAsync(Event(run, 2, clock.GetUtcNow()), default);
        Assert.That(second.Status.Availability, Is.EqualTo("available"));
        Assert.That(second.GetEvents(run, 0, 100)!.Events.Select(e => e.Sequence), Is.EqualTo(new long[] { 1, 2 }));
    }

    [Test]
    public async Task OversizedMultibyteRecordsAreExplicitlyTruncatedWithinEncodedLimit()
    {
        using var directory = new Temp(); var clock = new Clock(); var run = Guid.NewGuid();
        await using var store = new DiagnosticHistoryStore(directory.Path, clock);
        await store.WriteAsync(Event(run, 1, clock.GetUtcNow()) with { Build = new string('界', 4000) }, default);
        var page = store.GetEvents(run, 0, 100)!;
        Assert.That(page.Events.Single().Truncated, Is.True);
        Assert.That(page.Truncated, Is.True);
        Assert.That(File.ReadAllBytes(Directory.GetFiles(directory.Path, "*.jsonl").Single()).Length, Is.LessThanOrEqualTo(DiagnosticLimits.EventBytes));
    }

    [Test]
    public async Task TotalBudgetCountsDamagedLeftoversAndEvictsBeforeWriting()
    {
        using var directory = new Temp(); var clock = new Clock(); var run = Guid.NewGuid();
        await File.WriteAllBytesAsync(System.IO.Path.Combine(directory.Path, "damaged.leftover"), new byte[20_000_000]);
        await using var store = new DiagnosticHistoryStore(directory.Path, clock);
        await store.WriteAsync(Event(run, 1, clock.GetUtcNow()), default);
        Assert.That(Directory.GetFiles(directory.Path).Sum(p => new FileInfo(p).Length), Is.LessThanOrEqualTo(20_000_000));
        Assert.That(store.GetEvents(run, 0, 100)!.Events, Has.Length.EqualTo(1));
        Assert.That(store.Status.Gaps, Does.Contain("evicted"));
    }

    [Test]
    public async Task MergerUsesDiskAndMemoryOnceAndNeverReturnsExpiredMemory()
    {
        using var directory = new Temp(); var clock = new Clock(DateTimeOffset.UtcNow);
        await using var store = new DiagnosticHistoryStore(directory.Path, clock);
        await using var collector = new DiagnosticCollector();
        var first = collector.GetEvents(collector.RunId, 0, 100)!.Events.Single();
        await store.WriteAsync(first, default);
        collector.ConnectionChanged(ConnectionStage.Connected, 1, DiagnosticReason.None);
        var merged = new RetainedDiagnosticHistory(collector, store, clock);
        Assert.That(merged.GetEvents(collector.RunId, 0, 100)!.Events.Select(e => e.Sequence), Is.EqualTo(new long[] { 1, 2 }));
        clock.Advance(TimeSpan.FromDays(8));
        Assert.That(merged.GetEvents(collector.RunId, 0, 100)!.Events, Is.Empty);
    }

    [Test]
    public async Task RuntimePersistsPriorRunAndAuthenticatedApiMergesCurrentHistory()
    {
        using var directory = new Temp();
        Guid prior;
        await using (var runtime = DiagnosticRuntime.Start(directory.Path))
        {
            prior = runtime.Collector.RunId;
            await runtime.Started;
            runtime.Collector.ConnectionChanged(ConnectionStage.Connected, 42, DiagnosticReason.None);
        }
        await using var restarted = DiagnosticRuntime.Start(directory.Path);
        var host = await restarted.Started;
        var descriptor = JsonSerializer.Deserialize<DiscoveryDescriptor>(await File.ReadAllTextAsync(host.DiscoveryPath!), DiagnosticJson.Options)!;
        using var http = new HttpClient { BaseAddress = new Uri(host.Endpoint!) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", descriptor.Token);
        var page = JsonSerializer.Deserialize<DiagnosticEventPage>(await http.GetStringAsync($"/diagnostics/events?runId={prior}"), DiagnosticJson.Options)!;
        Assert.That(page.Events.Select(e => e.Code), Is.EqualTo(new[] { "app.started", "connection.changed", "app.stopped" }));
        Assert.That(page.Completeness, Is.EqualTo("persistedPrefixOnly"));
        var state = JsonSerializer.Deserialize<DiagnosticState>(await http.GetStringAsync("/diagnostics/state"), DiagnosticJson.Options)!;
        Assert.That(state.RunId, Is.Not.EqualTo(prior));
        Assert.That(state.Health.Storage, Is.EqualTo("available"));
    }

    [Test]
    public async Task FailedDeletionStopsWritesAndBackoffBoundsRepeatedFailures()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("Uses Unix directory permissions; native Windows deletion semantics require manual validation."); return; }
        using var directory = new Temp(); var clock = new Clock(); var run = Guid.NewGuid();
        await using var store = new DiagnosticHistoryStore(directory.Path, clock);
        await store.WriteAsync(Event(run, 1, clock.GetUtcNow()), default);
        var leftover = System.IO.Path.Combine(directory.Path, "damaged.leftover");
        await File.WriteAllBytesAsync(leftover, new byte[19_500_000]);
        var before = Directory.GetFiles(directory.Path).Sum(p => new FileInfo(p).Length);
        File.SetUnixFileMode(directory.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await store.WriteAsync(Event(run, 2, clock.GetUtcNow()), default);
            Assert.That(store.Status.Availability, Is.EqualTo("unavailable"));
            Assert.That(Directory.GetFiles(directory.Path).Sum(p => new FileInfo(p).Length), Is.EqualTo(before));
            File.SetUnixFileMode(directory.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            for (var i = 0; i < 50; i++) await store.WriteAsync(Event(run, i + 3, clock.GetUtcNow()), default);
            Assert.That(Directory.GetFiles(directory.Path).Sum(p => new FileInfo(p).Length), Is.EqualTo(before), "Backoff must skip all I/O retries before its deadline");
            Assert.That(store.Status.Dropped, Is.EqualTo(51));
            clock.Advance(TimeSpan.FromSeconds(2));
            await store.WriteAsync(Event(run, 53, clock.GetUtcNow()), default);
            Assert.That(store.Status.Availability, Is.EqualTo("available"));
            Assert.That(Directory.GetFiles(directory.Path).Sum(p => new FileInfo(p).Length), Is.LessThanOrEqualTo(20_000_000));
            Assert.That(store.GetEvents(run, 0, 100)!.Gaps, Does.Contain("unpersisted"));
        }
        finally { File.SetUnixFileMode(directory.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Test]
    public async Task UnwritableInitializationDoesNotBlockRuntimeOrLoseLiveEvidence()
    {
        using var directory = new Temp();
        var occupied = System.IO.Path.Combine(directory.Path, "file");
        await File.WriteAllTextAsync(occupied, "occupied");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var runtime = DiagnosticRuntime.Start(occupied);
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(100)));
        runtime.Collector.ConnectionChanged(ConnectionStage.Connected, 2, DiagnosticReason.None);
        await runtime.Started;
        Assert.That(runtime.Collector.GetState().Connection.Generation, Is.EqualTo(2));
        await WaitUntil(() => runtime.Collector.GetState().Health.Storage != "starting");
        Assert.That(runtime.Collector.GetState().Health.Storage, Is.EqualTo("unavailable"));
        watch.Restart();
        await runtime.DisposeAsync();
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2.2)));
    }

    [Test]
    public async Task CancelledHistoryQueryDoesNotAcquireStorageOwnership()
    {
        using var directory = new Temp();
        await using var store = new DiagnosticHistoryStore(directory.Path);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.GetRuns(cancellation.Token));
        Assert.That(Directory.GetFiles(directory.Path), Is.Empty);
    }

    [Test]
    public async Task ExpiredMemoryPrefixCannotHideRetainedEventsBeyondRequestedPage()
    {
        using var directory = new Temp(); var clock = new Clock(DateTimeOffset.UtcNow);
        await using var store = new DiagnosticHistoryStore(directory.Path, clock);
        await using var collector = new DiagnosticCollector();
        for (var i = 0; i < 4; i++) collector.ConnectionChanged(ConnectionStage.Connected, i + 1, DiagnosticReason.None);
        // Simulate a long-running collector across a seven-day boundary without waiting in real time.
        var field = typeof(DiagnosticCollector).GetField("events", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var queue = (Queue<DiagnosticEvent>)field.GetValue(collector)!;
        var records = queue.ToArray(); queue.Clear();
        foreach (var record in records) queue.Enqueue(record.Sequence <= 3 ? record with { Timestamp = clock.GetUtcNow() - TimeSpan.FromDays(8) } : record);
        var history = new RetainedDiagnosticHistory(collector, store, clock);
        var first = history.GetEvents(collector.RunId, 0, 2)!;
        Assert.That(first.Events.Select(e => e.Sequence), Is.EqualTo(new long[] { 4, 5 }));
        Assert.That(first.FirstAvailableSequence, Is.EqualTo(4));
        Assert.That(first.LastAvailableSequence, Is.EqualTo(5));
        Assert.That(first.NextCursor, Is.EqualTo(5));
        Assert.That(first.HasMore, Is.False);
        Assert.That(first.Gaps, Does.Contain("expired"));
        Assert.That(history.GetEvents(collector.RunId, 5, 2)!.Events, Is.Empty);
    }

    [Test]
    public async Task QueriesDuringWriteBackoffStillReadIndexedEvidenceAndReportUnavailable()
    {
        using var directory = new Temp(); var clock = new Clock(); var run = Guid.NewGuid();
        await using var store = new DiagnosticHistoryStore(directory.Path, clock);
        await store.WriteAsync(Event(run, 1, clock.GetUtcNow()), default);
        // Holding the metadata replacement path forces a real persistence failure after the record write.
        using (var locked = new FileStream(System.IO.Path.Combine(directory.Path, "metadata.tmp"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            await store.WriteAsync(Event(run, 2, clock.GetUtcNow()), default);
        Assert.That(store.Status.Availability, Is.EqualTo("unavailable"));
        var page = store.GetEvents(run, 0, 100);
        Assert.That(page, Is.Not.Null);
        Assert.That(page!.Events.Select(e => e.Sequence), Is.EqualTo(new long[] { 1, 2 }));
        Assert.That(page.Storage, Is.EqualTo("unavailable"));
        Assert.That(page.Gaps, Does.Contain("unpersisted"));
    }

    [Test]
    public async Task RecoveryKeepsNewestEvidenceWhenSegmentIndexLimitIsReached()
    {
        using var directory = new Temp(); var clock = new Clock(); var run = Guid.NewGuid();
        for (var i = 1; i <= DiagnosticHistoryLimits.Segments + 1; i++)
        {
            var path = System.IO.Path.Combine(directory.Path, $"{run:N}-{i:D20}.jsonl");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Event(run, i, clock.GetUtcNow() + TimeSpan.FromSeconds(i)), DiagnosticJson.Options) + "\n");
            File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime + TimeSpan.FromSeconds(i));
        }
        await using var store = new DiagnosticHistoryStore(directory.Path, clock);
        var page = store.GetEvents(run, DiagnosticHistoryLimits.Segments, 100)!;
        Assert.That(page.Events.Single().Sequence, Is.EqualTo(DiagnosticHistoryLimits.Segments + 1));
        Assert.That(page.FirstAvailableSequence, Is.EqualTo(2));
        Assert.That(page.Gaps, Does.Contain("evicted"));
        Assert.That(Directory.GetFiles(directory.Path, "*.jsonl"), Has.Length.EqualTo(DiagnosticHistoryLimits.Segments));
    }

    [Test]
    public async Task OfflineExpiryIsCleanedAtStartupAndPeriodicCleanupRunsWithoutQueries()
    {
        using var directory = new Temp(); var clock = new Clock(); var run = Guid.NewGuid();
        await using (var store = new DiagnosticHistoryStore(directory.Path, clock))
            await store.WriteAsync(Event(run, 1, clock.GetUtcNow()), default);
        clock.Advance(TimeSpan.FromDays(8));
        await using var restarted = new DiagnosticHistoryStore(directory.Path, clock);
        Assert.That(restarted.GetEvents(run, 0, 10)!.Events, Is.Empty);
        Assert.That(Directory.GetFiles(directory.Path, "*.jsonl"), Is.Empty);
        await restarted.WriteAsync(Event(run, 2, clock.GetUtcNow()), default);
        // A manual timer drives the actual maintenance worker, with no history query or write.
        await WaitUntil(() => clock.HasTimer);
        clock.Advance(TimeSpan.FromDays(8));
        await WaitUntil(() => Directory.GetFiles(directory.Path, "*.jsonl").Length == 0);
        Assert.That(restarted.Status.Gaps, Does.Contain("expired"));
    }

    [Test]
    public async Task UnknownRunDuringUnavailableStorageReturnsExplicitUnavailableError()
    {
        using var directory = new Temp();
        var historyPath = System.IO.Path.Combine(directory.Path, "occupied");
        await File.WriteAllTextAsync(historyPath, "occupied");
        await using var runtime = DiagnosticRuntime.Start(directory.Path, historyDirectory: historyPath);
        var host = await runtime.Started;
        var descriptor = JsonSerializer.Deserialize<DiscoveryDescriptor>(await File.ReadAllTextAsync(host.DiscoveryPath!), DiagnosticJson.Options)!;
        using var http = new HttpClient { BaseAddress = new Uri(host.Endpoint!) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", descriptor.Token);
        var response = await http.GetAsync($"/diagnostics/events?runId={Guid.NewGuid()}");
        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.ServiceUnavailable));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("storageUnavailable"));
    }

    [Test]
    public async Task DeletedEvidenceWithRetainedWatermarkReportsKnownGapAfterRestart()
    {
        using var directory = new Temp(); var clock = new Clock(); var run = Guid.NewGuid();
        await using (var store = new DiagnosticHistoryStore(directory.Path, clock))
            await store.WriteAsync(Event(run, 1, clock.GetUtcNow()), default);
        File.Delete(Directory.GetFiles(directory.Path, "*.jsonl").Single());
        await using var restarted = new DiagnosticHistoryStore(directory.Path, clock);
        var page = restarted.GetEvents(run, 0, 100)!;
        Assert.That(page.Events, Is.Empty);
        Assert.That(page.SequenceWatermark, Is.EqualTo(1));
        Assert.That(page.Gaps, Does.Contain("sequenceGap"));
        Assert.That(page.Completeness, Is.EqualTo("incomplete"));
    }

    [Test]
    public async Task OversizedDamagedManifestIsReclaimedWithinSharedBudget()
    {
        using var directory = new Temp(); var clock = new Clock(); var run = Guid.NewGuid();
        await File.WriteAllBytesAsync(System.IO.Path.Combine(directory.Path, "metadata.json"), new byte[20_000_001]);
        await using var store = new DiagnosticHistoryStore(directory.Path, clock);
        await store.WriteAsync(Event(run, 1, clock.GetUtcNow()), default);
        Assert.That(store.Status.Availability, Is.EqualTo("available"));
        Assert.That(store.Status.Gaps, Does.Contain("corruption"));
        Assert.That(Directory.GetFiles(directory.Path).Sum(p => new FileInfo(p).Length), Is.LessThanOrEqualTo(DiagnosticHistoryLimits.TotalBytes));
    }

    [Test]
    public async Task MergerPagesAcrossMemoryEvictionWithoutDuplicatesOrFalseGap()
    {
        using var directory = new Temp(); var clock = new Clock(DateTimeOffset.UtcNow);
        await using var collector = new DiagnosticCollector();
        var prefix = new List<DiagnosticEvent> { collector.GetEvents(collector.RunId, 0, 1)!.Events.Single() };
        for (var i = 1; i < 1100; i++)
        {
            collector.ConnectionChanged(ConnectionStage.Connected, i, DiagnosticReason.None);
            if (i < 150) prefix.Add(collector.GetEvents(collector.RunId, i, 1)!.Events.Single());
        }
        var path = System.IO.Path.Combine(directory.Path, $"{collector.RunId:N}-prefix.jsonl");
        await File.WriteAllTextAsync(path, string.Concat(prefix.Select(e => JsonSerializer.Serialize(e, DiagnosticJson.Options) + "\n")));
        await using var store = new DiagnosticHistoryStore(directory.Path, clock);
        var history = new RetainedDiagnosticHistory(collector, store, clock);
        var seen = new List<long>(); long cursor = 0;
        DiagnosticEventPage page;
        do
        {
            page = history.GetEvents(collector.RunId, cursor, 100)!;
            seen.AddRange(page.Events.Select(e => e.Sequence)); cursor = page.NextCursor;
            Assert.That(page.Gaps, Does.Not.Contain("sequenceGap"));
            Assert.That(page.FirstAvailableSequence, Is.EqualTo(1));
        } while (page.HasMore && seen.Count <= 1200);
        Assert.That(seen, Is.EqualTo(Enumerable.Range(1, 1100).Select(i => (long)i)));
    }

    [Test]
    public async Task DisabledSinkCannotClaimRecoveredPersistenceOrHideUnionGapOnAnyPage()
    {
        using var directory = new Temp(); var clock = new Clock(DateTimeOffset.UtcNow);
        await using var store = new DiagnosticHistoryStore(directory.Path, clock);
        var sink = new DeferredSink(store);
        await using var collector = new DiagnosticCollector(sink);
        try
        {
            await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            for (var i = 1; i < 100; i++) collector.ConnectionChanged(ConnectionStage.Connected, i, DiagnosticReason.None);
            foreach (var record in collector.GetMemoryHistory().Events) await store.WriteAsync(record, default);
            await WaitUntil(() => collector.GetState().Health.FailureCategory == "sinkUnavailable");
            sink.Release.TrySetResult();
            await sink.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var history = new RetainedDiagnosticHistory(collector, store, clock);
            var beforeEviction = history.GetEvents(collector.RunId, 100, 50)!;
            var runBeforeEviction = history.GetRuns().Single(r => r.IsCurrent);

            for (var i = 100; i < 2023; i++) collector.ConnectionChanged(ConnectionStage.Connected, i, DiagnosticReason.None);
            Assert.That(store.GetEvents(collector.RunId, 0, 100)!.Events.Select(e => e.Sequence), Is.EqualTo(Enumerable.Range(1, 100).Select(i => (long)i)));
            Assert.That(store.Status.Availability, Is.EqualTo("available"), "The store can recover independently of the permanently disabled collector sink.");
            Assert.That(store.Status.Gaps, Is.Empty);
            Assert.That(collector.GetMemoryHistory().FirstAvailableSequence, Is.EqualTo(1000));

            await using var host = await DiagnosticHost.StartAsync(collector, directory.Path, history);
            var descriptor = JsonSerializer.Deserialize<DiscoveryDescriptor>(await File.ReadAllTextAsync(host.DiscoveryPath!), DiagnosticJson.Options)!;
            using var http = new HttpClient { BaseAddress = new Uri(host.Endpoint!) };
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", descriptor.Token);
            var tail = JsonSerializer.Deserialize<DiagnosticEventPage>(await http.GetStringAsync("/diagnostics/events?afterSequence=2023"), DiagnosticJson.Options)!;
            var early = JsonSerializer.Deserialize<DiagnosticEventPage>(await http.GetStringAsync("/diagnostics/events?limit=50"), DiagnosticJson.Options)!;
            var state = JsonSerializer.Deserialize<DiagnosticState>(await http.GetStringAsync("/diagnostics/state"), DiagnosticJson.Options)!;
            using var runs = JsonDocument.Parse(await http.GetStringAsync("/diagnostics/runs"));
            var currentRun = runs.RootElement.GetProperty("runs").EnumerateArray().Single(r => r.GetProperty("isCurrent").GetBoolean());
            Assert.Multiple(() =>
            {
                Assert.That(beforeEviction.Storage, Is.EqualTo("unavailable"));
                Assert.That(beforeEviction.Gaps, Does.Contain("unpersisted"));
                Assert.That(beforeEviction.Completeness, Is.EqualTo("incomplete"));
                Assert.That(runBeforeEviction.Persistence, Is.EqualTo("unavailable"));
                Assert.That(tail.Events, Is.Empty);
                Assert.That(tail.NextCursor, Is.EqualTo(2023));
                Assert.That(tail.HasMore, Is.False);
                Assert.That(early.Events.Select(e => e.Sequence), Is.EqualTo(Enumerable.Range(1, 50).Select(i => (long)i)));
                Assert.That(early.HasMore, Is.True);
                foreach (var page in new[] { tail, early })
                {
                    Assert.That(page.Storage, Is.EqualTo("unavailable"));
                    Assert.That(page.Gaps, Does.Contain("unpersisted"));
                    Assert.That(page.Gaps, Does.Contain("sequenceGap"), "Known holes in the retained union must not depend on the requested page.");
                    Assert.That(page.Completeness, Is.EqualTo("incomplete"));
                    Assert.That(page.HistoryEvicted, Is.True);
                    Assert.That(page.FirstAvailableSequence, Is.EqualTo(1));
                    Assert.That(page.LastAvailableSequence, Is.EqualTo(2023));
                    Assert.That(page.PersistedThroughSequence, Is.EqualTo(100));
                    Assert.That(page.SequenceWatermark, Is.EqualTo(2023));
                    Assert.That(page.DroppedEvents, Is.Zero, "Sink loss does not fabricate producer drops or their sequence positions.");
                }
                Assert.That(state.Health.Storage, Is.EqualTo("unavailable"));
                Assert.That(state.Health.Persistence!.Availability, Is.EqualTo("unavailable"));
                Assert.That(state.Health.SinkDropped, Is.GreaterThan(0));
                Assert.That(currentRun.GetProperty("persistence").GetString(), Is.EqualTo("unavailable"));
            });
        }
        finally { sink.Release.TrySetResult(); }
    }

    private sealed class DeferredSink(DiagnosticHistoryStore store) : IDiagnosticSink
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask WriteAsync(DiagnosticEvent record, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task; // Deliberately outlives the collector deadline, then allows the store to recover.
            await store.WriteAsync(record, CancellationToken.None);
            Finished.TrySetResult();
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.That(condition(), Is.True);
    }

    internal static DiagnosticEvent Event(Guid run, long sequence, DateTimeOffset at) =>
        new(1, run, "test", sequence, at, sequence, "future.typed.event", "information", "test", new(ConnectionStage.Unknown, 0, DiagnosticReason.None));
    internal sealed class Clock(DateTimeOffset? initial = null) : TimeProvider
    {
        private DateTimeOffset now = initial ?? new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        private readonly List<ManualTimer> timers = [];
        public bool HasTimer { get { lock (timers) return timers.Any(t => !t.IsDisposed); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state); lock (timers) timers.Add(timer); return timer;
        }
        public void Advance(TimeSpan amount)
        {
            now += amount;
            ManualTimer[] copy; lock (timers) copy = timers.ToArray();
            foreach (var timer in copy) timer.Fire();
        }
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public bool IsDisposed => disposed;
            public void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    internal sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "afktor-history-" + Guid.NewGuid());
        public Temp() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
