using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AFKtor.Core.Diagnostics;
using AFKtor.Diagnostics;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class DiagnosticsTests
{
    [Test]
    public async Task RealListenerPublishesRestrictedDiscoveryAndCoherentState()
    {
        using var directory = new TemporaryDirectory();
        await using var collector = new DiagnosticCollector();
        await using var host = await DiagnosticHost.StartAsync(collector, directory.Path);
        Assert.That(host.Endpoint, Does.StartWith("http://127.0.0.1:"));
        var descriptor = JsonSerializer.Deserialize<DiscoveryDescriptor>(await File.ReadAllTextAsync(host.DiscoveryPath!), DiagnosticJson.Options)!;
        Assert.That(Convert.FromHexString(descriptor.Token), Has.Length.EqualTo(32));
        Assert.That(descriptor.RunId, Is.EqualTo(collector.RunId));
        if (!OperatingSystem.IsWindows())
            Assert.That(File.GetUnixFileMode(host.DiscoveryPath!), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        using var http = Authorized(descriptor);
        collector.ConnectionChanged(ConnectionStage.Connected, 7, DiagnosticReason.None);
        using var state = JsonDocument.Parse(await http.GetStringAsync("/diagnostics/state"));
        var root = state.RootElement;
        Assert.That(root.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("runId").GetGuid(), Is.EqualTo(collector.RunId));
        Assert.That(root.GetProperty("build").GetString(), Is.Not.Empty);
        Assert.That(root.GetProperty("connection").GetProperty("generation").GetInt64(), Is.EqualTo(7));
        Assert.That(root.GetProperty("connection").GetProperty("stage").GetString(), Is.EqualTo("connected"));
        foreach (var field in new[] { "readyCheck", "arming", "settings", "operation" })
            Assert.That(root.GetProperty(field).GetProperty("availability").GetString(), Is.EqualTo("unavailable"));
        Assert.That(root.GetRawText(), Does.Not.Contain(descriptor.Token));
        using var events = JsonDocument.Parse(await http.GetStringAsync("/diagnostics/events?limit=1"));
        Assert.That(events.RootElement.GetProperty("events")[0].GetProperty("code").GetString(), Is.EqualTo("app.started"));
        Assert.That(events.RootElement.GetProperty("hasMore").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("sequenceWatermark").GetInt64(), Is.EqualTo(2));
        await host.DisposeAsync();
        Assert.That(File.Exists(host.DiscoveryPath), Is.False);
    }

    [Test]
    public async Task ListenerRejectsUnauthorizedMutationInvalidCursorsAndStaleIdentity()
    {
        using var directory = new TemporaryDirectory();
        await using var collector = new DiagnosticCollector();
        await using var host = await DiagnosticHost.StartAsync(collector, directory.Path);
        using var http = new HttpClient { BaseAddress = new Uri(host.Endpoint!) };
        Assert.That((await http.GetAsync("/diagnostics/state")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        Assert.That((await http.GetAsync("/diagnostics/state")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        var descriptor = JsonSerializer.Deserialize<DiscoveryDescriptor>(await File.ReadAllTextAsync(host.DiscoveryPath!), DiagnosticJson.Options)!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", descriptor.Token);
        foreach (var query in new[] { "limit=0", "limit=101", "afterSequence=-1", "afterSequence=wat", "runId=wat", "limit=1&limit=2", "unexpected=1" })
            Assert.That((await http.GetAsync("/diagnostics/events?" + query)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), query);
        Assert.That((await http.PostAsync("/diagnostics/state", null)).StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
        http.DefaultRequestHeaders.Add("X-AFKtor-Run-Id", Guid.NewGuid().ToString());
        Assert.That((await http.GetAsync("/diagnostics/state")).StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        http.DefaultRequestHeaders.Remove("X-AFKtor-Run-Id");
        http.DefaultRequestHeaders.Add("Origin", "https://example.test");
        var response = await http.GetAsync("/diagnostics/state");
        Assert.That(response.Headers.Contains("Access-Control-Allow-Origin"), Is.False);
        Assert.That((await http.GetAsync("/diagnostics/events?runId=" + Guid.NewGuid())).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task EvictionAndEmptyTailHaveDifferentCompletenessAndOrderedPages()
    {
        using var directory = new TemporaryDirectory();
        await using var collector = new DiagnosticCollector();
        for (var i = 1; i <= DiagnosticLimits.MemoryEvents + 20; i++)
            collector.ConnectionChanged(ConnectionStage.Connecting, i, DiagnosticReason.None);
        await using var host = await DiagnosticHost.StartAsync(collector, directory.Path);
        var descriptor = JsonSerializer.Deserialize<DiscoveryDescriptor>(await File.ReadAllTextAsync(host.DiscoveryPath!), DiagnosticJson.Options)!;
        using var http = Authorized(descriptor);
        var page = JsonSerializer.Deserialize<DiagnosticEventPage>(await http.GetStringAsync("/diagnostics/events?limit=100"), DiagnosticJson.Options)!;
        Assert.That(page.HistoryEvicted, Is.True);
        Assert.That(page.Events.Length, Is.EqualTo(100));
        Assert.That(page.Events.Select(e => e.Sequence), Is.Ordered.Ascending);
        Assert.That(page.Events.All(e => e.Timestamp >= collector.StartedAt && e.ElapsedMilliseconds >= 0), Is.True);
        Assert.That(page.NextCursor, Is.EqualTo(page.Events[^1].Sequence));
        var tail = JsonSerializer.Deserialize<DiagnosticEventPage>(await http.GetStringAsync("/diagnostics/events?afterSequence=" + collector.GetState().SequenceWatermark), DiagnosticJson.Options)!;
        Assert.That(tail.Events, Is.Empty);
        Assert.That(tail.HistoryEvicted, Is.False);
        Assert.That(tail.HasMore, Is.False);
    }

    [Test]
    public async Task UnavailableDiscoveryKeepsLiveCollectorAndApiUsable()
    {
        using var directory = new TemporaryDirectory();
        var file = System.IO.Path.Combine(directory.Path, "file");
        await File.WriteAllTextAsync(file, "occupied");
        await using var collector = new DiagnosticCollector();
        await using var host = await DiagnosticHost.StartAsync(collector, file);
        Assert.That(host.Endpoint, Is.Not.Null);
        Assert.That(host.DiscoveryPath, Is.Null);
        Assert.That(collector.GetState().Health.Discovery, Is.EqualTo("unavailable"));
        collector.ConnectionChanged(ConnectionStage.Connected, 1, DiagnosticReason.None);
        Assert.That(collector.GetState().Connection.Generation, Is.EqualTo(1));
    }

    [Test]
    public async Task RuntimeStartsAndStopsHostAndRecordsShutdownWithinSharedBudget()
    {
        using var directory = new TemporaryDirectory();
        var runtime = DiagnosticRuntime.Start(directory.Path);
        var host = await runtime.Started;
        var path = host.DiscoveryPath;
        Assert.That(File.Exists(path), Is.True);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await runtime.DisposeAsync();
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2.2)));
        Assert.That(File.Exists(path), Is.False);
        var state = runtime.Collector.GetState();
        Assert.That(runtime.Collector.GetEvents(state.RunId, state.SequenceWatermark - 1, 1)!.Events.Single().Code, Is.EqualTo("app.stopped"));
    }

    [Test]
    public async Task ResponseBoundReturnsSmallSafeErrorAndReaderDeadlineReleasesHttpRequest()
    {
        using var directory = new TemporaryDirectory();
        await using var collector = new DiagnosticCollector();
        var history = new HostileHistory(collector);
        await using var host = await DiagnosticHost.StartAsync(collector, directory.Path, history);
        var descriptor = JsonSerializer.Deserialize<DiscoveryDescriptor>(await File.ReadAllTextAsync(host.DiscoveryPath!), DiagnosticJson.Options)!;
        using var http = Authorized(descriptor);
        var response = await http.GetAsync("/diagnostics/runs");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        var error = await response.Content.ReadAsStringAsync();
        Assert.That(error.Length, Is.LessThan(128));
        Assert.That(error, Does.Contain("responseTooLarge").And.Not.Contain("SECRET_"));
        try
        {
            var request = http.GetAsync("/diagnostics/events");
            await history.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.ThrowsAsync<HttpRequestException>(async () => await request.WaitAsync(TimeSpan.FromSeconds(4)));
            Assert.That((await http.GetAsync("/diagnostics/state")).IsSuccessStatusCode, Is.True);
        }
        finally { history.Release.Set(); }
    }

    [Test]
    public async Task CancelledStartupDoesNotPublishDiscoveryOrThrow()
    {
        using var directory = new TemporaryDirectory();
        await using var collector = new DiagnosticCollector();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using var host = await DiagnosticHost.StartAsync(collector, directory.Path, cancellationToken: cancelled.Token);
        Assert.That(collector.GetState().Health.Api, Is.EqualTo("unavailable"));
        Assert.That(host.DiscoveryPath, Is.Null);
        Assert.That(Directory.GetFiles(directory.Path, "instance.json", SearchOption.AllDirectories), Is.Empty);
    }

    private sealed class HostileHistory(DiagnosticCollector collector) : IDiagnosticHistory
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public DiagnosticRun[] GetRuns() => Enumerable.Repeat(new DiagnosticRun(1, collector.RunId,
            new string('a', 2000) + "SECRET_RAW_BODY", collector.StartedAt, true, "memoryOnly"), 1000).ToArray();
        public DiagnosticEventPage? GetEvents(Guid runId, long afterSequence, int limit)
        {
            Entered.TrySetResult();
            Release.Wait();
            return collector.GetEvents(runId, afterSequence, limit);
        }
    }

    private static HttpClient Authorized(DiscoveryDescriptor descriptor)
    {
        var http = new HttpClient { BaseAddress = new Uri(descriptor.Endpoint) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", descriptor.Token);
        http.DefaultRequestHeaders.Add("X-AFKtor-Run-Id", descriptor.RunId.ToString());
        return http;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "afktor-test-" + Guid.NewGuid());
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { Directory.Delete(Path, true); }
    }
}
