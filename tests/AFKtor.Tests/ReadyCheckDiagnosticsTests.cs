using AFKtor.Core;
using AFKtor.Core.Diagnostics;
using NUnit.Framework;
using AFKtor.Diagnostics;
using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;

namespace AFKtor.Tests;

public class ReadyCheckDiagnosticsTests
{
    [Test]
    public async Task RealControllerPublishesCorrelatedScheduleAndEffectiveEdits()
    {
        var clock = new ManualClock();
        var api = new FakeReadyCheckClient();
        await using var diagnostics = new DiagnosticCollector();
        await using var controller = new ReadyCheckController(clock.DelayAsync, () => clock.Now, diagnostics);
        var session = controller.BeginSession(api);
        controller.Update(session, new(ReadyCheckSnapshot.Inactive, IsInitial: true));
        controller.SetArmed(true);
        controller.Update(session, new(new("InProgress", "None", 0)));
        controller.AcceptDelay = TimeSpan.FromSeconds(8);
        var state = diagnostics.GetState();
        Assert.Multiple(() =>
        {
            Assert.That(state.ReadyCheck.Generation, Is.EqualTo(session));
            Assert.That(state.ReadyCheck.CheckId, Is.EqualTo(1));
            Assert.That(state.Arming.Armed, Is.True);
            Assert.That(state.Settings.DelaySeconds, Is.EqualTo(8));
            Assert.That(state.Operation.AttemptId, Is.EqualTo(1));
            Assert.That(state.Operation.CapturedDelaySeconds, Is.EqualTo(3));
            Assert.That(state.Operation.Stage, Is.EqualTo(AcceptOperationStage.Waiting));
        });
        var records = await QueryEvents(diagnostics);
        Assert.That(records.Select(r => r.Code), Does.Contain("accept.scheduled"));
        var edit = records.Single(r => r.Code == "delay.changed").Details.Controller!;
        Assert.That(edit.PreviousDelaySeconds, Is.EqualTo(3));
        Assert.That(edit.Settings.DelaySeconds, Is.EqualTo(8));
    }

    [Test]
    public async Task InitialDisarmedDuplicatesAndStaleUpdatesHaveSafeEvidence()
    {
        var clock = new ManualClock();
        await using var diagnostics = new DiagnosticCollector();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now, diagnostics);
        var session = c.BeginSession(new FakeReadyCheckClient());
        c.Update(session, new(new("InProgress", "None", 1), IsInitial: true));
        c.Update(session, new(ReadyCheckSnapshot.Inactive));
        c.Update(session, new(new("InProgress", "None", 0)));
        for (var i = 0; i < 50; i++) { clock.Advance(.01); c.Update(session, new(new("InProgress", "None", 0))); }
        Assert.That(diagnostics.GetState().ReadyCheck.DuplicateCount, Is.EqualTo(50));
        c.Update(session, new(new("InProgress", "None", 1)));
        c.Update(session - 1, new(new("SECRET_BODY", "SECRET_TOKEN", 999)));
        var records = await QueryEvents(diagnostics);
        Assert.Multiple(() =>
        {
            Assert.That(records.Select(r => r.Code), Does.Contain("readyCheck.baseline"));
            Assert.That(records.Select(r => r.Code), Does.Contain("readyCheck.initialSkipped"));
            Assert.That(records.Select(r => r.Code), Does.Contain("readyCheck.disarmedSkipped"));
            Assert.That(records.Length, Is.LessThan(20));
            Assert.That(records.Single(r => r.Code == "readyCheck.duplicates").Details.Controller!.ReadyCheck.DuplicateCount, Is.EqualTo(50));
            Assert.That(records.Last().Code, Is.EqualTo("readyCheck.staleUpdate"));
            Assert.That(diagnostics.GetState().ReadyCheck.Timer, Is.EqualTo(1));
        });
        Assert.That(System.Text.Json.JsonSerializer.Serialize(records), Does.Not.Contain("SECRET"));
    }

    [TestCase(3, 0, 1, 3, EligibilityFailure.InsufficientTimer)]
    [TestCase(0, 0, 0, 1, EligibilityFailure.InsufficientTimer)]
    [TestCase(3, 4, 3, 3, EligibilityFailure.TimerBackwards | EligibilityFailure.InsufficientTimer)]
    public async Task VerificationExplainsEachTimerFailure(int delay, double initial, double returned, double wait, EligibilityFailure expected)
    {
        var clock = new ManualClock();
        var api = new FakeReadyCheckClient { Read = _ => Task.FromResult(new ReadyCheckSnapshot("InProgress", "None", returned)) };
        await using var diagnostics = new DiagnosticCollector();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now, diagnostics);
        c.AcceptDelay = TimeSpan.FromSeconds(delay);
        var session = c.BeginSession(api);
        c.Update(session, new(ReadyCheckSnapshot.Inactive, IsInitial: true)); c.SetArmed(true);
        c.Update(session, new(new("InProgress", "None", initial))); clock.Advance(wait);
        var decision = (await QueryEvents(diagnostics)).Single(r => r.Code == "accept.verification").Details.Controller!;
        Assert.Multiple(() =>
        {
            Assert.That(decision.Failures, Is.EqualTo(expected));
            Assert.That(decision.InitialTimer, Is.EqualTo(initial));
            Assert.That(decision.LastTimer, Is.EqualTo(initial));
            Assert.That(decision.ReturnedTimer, Is.EqualTo(returned));
            Assert.That(decision.LocalElapsedSeconds, Is.EqualTo(wait));
            Assert.That(decision.RequiredTimer, Is.EqualTo(initial + wait));
            Assert.That(api.Accepts, Is.Zero);
            Assert.That(diagnostics.GetState().Operation.Stage, Is.EqualTo(AcceptOperationStage.Finished));
        });
    }

    [TestCase("disarmed", AcceptCancellationReason.Disarmed)]
    [TestCase("replaced", AcceptCancellationReason.SessionReplaced)]
    [TestCase("ended", AcceptCancellationReason.CheckEnded)]
    [TestCase("backwards", AcceptCancellationReason.TimerBackwards)]
    [TestCase("disconnect", AcceptCancellationReason.Disconnected)]
    [TestCase("dispose", AcceptCancellationReason.Disposed)]
    public async Task CancellationRecordsExactCauseAndDecisionIdentity(string action, AcceptCancellationReason expected)
    {
        var clock = new ManualClock(); var api = new FakeReadyCheckClient();
        await using var diagnostics = new DiagnosticCollector();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now, diagnostics);
        var session = c.BeginSession(api); c.Update(session, new(ReadyCheckSnapshot.Inactive)); c.SetArmed(true);
        c.Update(session, new(new("InProgress", "None", 2)));
        switch (action)
        {
            case "disarmed": c.SetArmed(false); break;
            case "replaced": c.BeginSession(new FakeReadyCheckClient()); break;
            case "ended": c.Update(session, new(ReadyCheckSnapshot.Inactive)); break;
            case "backwards": c.Update(session, new(new("InProgress", "None", 1))); break;
            case "disconnect": await c.DisconnectAsync(); break;
            case "dispose": await c.DisposeAsync(); break;
        }
        var cancellation = (await QueryEvents(diagnostics)).Single(r => r.Code == "accept.cancelled").Details.Controller!;
        Assert.Multiple(() =>
        {
            Assert.That(cancellation.Cancellation, Is.EqualTo(expected));
            Assert.That(cancellation.EventGeneration, Is.EqualTo(session));
            Assert.That(cancellation.EventCheckId, Is.EqualTo(1));
            Assert.That(cancellation.EventAttemptId, Is.EqualTo(1));
            Assert.That(cancellation.Operation.Stage, Is.EqualTo(AcceptOperationStage.Waiting));
            Assert.That(cancellation.Operation.Outcome, Is.EqualTo(AcceptRequestOutcome.None));
            Assert.That(api.Accepts, Is.Zero);
        });
    }

    [Test]
    public async Task LateFinalReadIsStaleAndCannotOverwriteNewSessionState()
    {
        var clock = new ManualClock(); var final = new TaskCompletionSource<ReadyCheckSnapshot>();
        var api = new FakeReadyCheckClient { Read = _ => final.Task };
        await using var diagnostics = new DiagnosticCollector();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now, diagnostics);
        var old = c.BeginSession(api); c.Update(old, new(ReadyCheckSnapshot.Inactive)); c.SetArmed(true);
        c.Update(old, new(new("InProgress", "None", 0))); clock.Advance(3);
        var current = c.BeginSession(new FakeReadyCheckClient()); c.Update(current, new(ReadyCheckSnapshot.Inactive));
        final.SetResult(new("InProgress", "None", 9));
        await c.DisconnectAsync();
        var verification = (await QueryEvents(diagnostics)).Single(r => r.Code == "accept.verification").Details.Controller!;
        Assert.That(verification.Failures.HasFlag(EligibilityFailure.StaleOperation), Is.True);
        Assert.That(verification.EventGeneration, Is.EqualTo(old));
        Assert.That(verification.ReadyCheck.Generation, Is.GreaterThan(old));
        Assert.That(api.Accepts, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DispatchOutcomeRemainsUnconfirmedAndNeverRetries(bool timeout)
    {
        var clock = new ManualClock();
        var api = new FakeReadyCheckClient { Accept = _ => timeout ? Task.FromException(new TaskCanceledException("SECRET_TIMEOUT")) : Task.CompletedTask };
        await using var diagnostics = new DiagnosticCollector();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now, diagnostics);
        var session = c.BeginSession(api); c.Update(session, new(ReadyCheckSnapshot.Inactive)); c.SetArmed(true);
        c.Update(session, new(new("InProgress", "None", 0))); clock.Advance(3);
        c.Update(session, new(new("InProgress", "None", 4))); clock.Advance(30);
        var records = await QueryEvents(diagnostics);
        var outcome = records.Single(r => r.Code == (timeout ? "accept.failed" : "accept.completed")).Details.Controller!;
        Assert.That(api.Accepts, Is.EqualTo(1));
        Assert.That(records.Count(r => r.Code == "accept.dispatched"), Is.EqualTo(1));
        Assert.That(outcome.Operation.Outcome, Is.EqualTo(timeout ? AcceptRequestOutcome.Unknown : AcceptRequestOutcome.CompletedUnconfirmed));
        Assert.That(outcome.LatencyMilliseconds, Is.Zero);
        Assert.That(System.Text.Json.JsonSerializer.Serialize(records), Does.Not.Contain("SECRET"));
    }

    [TestCase(HttpStatusCode.NoContent)]
    [TestCase(HttpStatusCode.Conflict)]
    public async Task RealTransportStatusAndSafeFailuresSurviveGenericPersistence(HttpStatusCode status)
    {
        var clock = new ManualClock();
        using var http = new HttpClient(new StatusHandler(status));
        var api = new LcuReadyCheckClient(LcuConnection.Parse("LeagueClient:123:54321:SECRET_PASSWORD:https")!, http);
        await using var diagnostics = new DiagnosticCollector();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now, diagnostics);
        var session = c.BeginSession(api); c.Update(session, new(ReadyCheckSnapshot.Inactive)); c.SetArmed(true);
        c.Update(session, new(new("InProgress", "None", 0))); clock.Advance(3);
        var records = await QueryEvents(diagnostics);
        var completed = records.Single(r => r.Code == (status == HttpStatusCode.NoContent ? "accept.completed" : "accept.failed"));
        Assert.That(completed.Details.Controller!.HttpStatus, Is.EqualTo((int)status));
        var path = Path.Combine(Path.GetTempPath(), "afktor-controller-history-" + Guid.NewGuid());
        try
        {
            await using (var store = new DiagnosticHistoryStore(path))
                foreach (var record in records) await store.WriteAsync(record, default);
            await using var recovered = new DiagnosticHistoryStore(path);
            var restored = recovered.GetEvents(diagnostics.RunId, 0, 100)!;
            Assert.That(restored.Events, Has.Length.EqualTo(records.Length));
            Assert.That(restored.Events.Any(r => r.Truncated), Is.False);
            Assert.That(restored.Events.Single(r => r.Code == completed.Code).Details.Controller!.HttpStatus, Is.EqualTo((int)status));
            Assert.That(string.Concat(Directory.GetFiles(path, "*.jsonl").Select(File.ReadAllText)), Does.Not.Contain("SECRET"));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Test]
    public async Task CancellationAfterDispatchPreservesUncertaintyAndOldAttemptOnLateCompletion()
    {
        var clock = new ManualClock(); var post = new TaskCompletionSource();
        var api = new FakeReadyCheckClient { Accept = _ => post.Task };
        await using var diagnostics = new DiagnosticCollector();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now, diagnostics);
        var old = c.BeginSession(api); c.Update(old, new(ReadyCheckSnapshot.Inactive)); c.SetArmed(true);
        c.Update(old, new(new("InProgress", "None", 0))); clock.Advance(3);
        var current = c.BeginSession(new FakeReadyCheckClient()); c.Update(current, new(ReadyCheckSnapshot.Inactive));
        clock.Advance(.2); post.SetResult();
        var records = await QueryEvents(diagnostics);
        var cancel = records.Single(r => r.Code == "accept.cancelled").Details.Controller!;
        var complete = records.Single(r => r.Code == "accept.completed").Details.Controller!;
        Assert.That(cancel.EventStage, Is.EqualTo(AcceptOperationStage.Dispatched));
        Assert.That(cancel.EventOutcome, Is.EqualTo(AcceptRequestOutcome.Unknown));
        Assert.That(complete.EventGeneration, Is.EqualTo(old));
        Assert.That(complete.EventAttemptId, Is.EqualTo(1));
        Assert.That(complete.EventOutcome, Is.EqualTo(AcceptRequestOutcome.CompletedUnconfirmed));
        Assert.That(complete.LatencyMilliseconds, Is.EqualTo(200).Within(.01));
        Assert.That(diagnostics.GetState().Operation.AttemptId, Is.Null);
        Assert.That(diagnostics.GetState().ReadyCheck.Generation, Is.EqualTo(current));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BrokenSaturatedSinkCannotChangeEligibilityDeadlineOrCancellation(bool hung)
    {
        var sink = new BrokenSink(hung);
        await using var diagnostics = new DiagnosticCollector(sink);
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var clock = new ManualClock(); var api = new FakeReadyCheckClient();
            await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now, diagnostics);
            var session = c.BeginSession(api); c.Update(session, new(ReadyCheckSnapshot.Inactive)); c.SetArmed(true);
            c.Update(session, new(new("InProgress", "None", 0)));
            for (var i = 0; i < 3000; i++) diagnostics.ConnectionChanged(ConnectionStage.Connected, i, DiagnosticReason.None);
            clock.Advance(2.9); Assert.That(api.Accepts, Is.Zero);
            Assert.That(c.Remaining!.Value.TotalSeconds, Is.EqualTo(.1).Within(.001));
            clock.Advance(.1); Assert.That(api.Accepts, Is.EqualTo(1));
            c.Update(session, new(ReadyCheckSnapshot.Inactive)); c.Update(session, new(new("InProgress", "None", 0)));
            c.SetArmed(false); clock.Advance(10); Assert.That(api.Accepts, Is.EqualTo(1));
            Assert.That(diagnostics.GetState().Health.SinkDropped, Is.GreaterThan(0));
        }
        finally { sink.Release.TrySetResult(); }
    }

    [Test]
    public async Task ContendedDiagnosticsCannotBlockControllerOrDispatch()
    {
        await using var diagnostics = new DiagnosticCollector();
        var gate = typeof(DiagnosticCollector).GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(diagnostics)!;
        var clock = new ManualClock(); var api = new FakeReadyCheckClient();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now, diagnostics);
        using var returned = new ManualResetEventSlim();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var session = c.BeginSession(api); c.Update(session, new(ReadyCheckSnapshot.Inactive)); c.SetArmed(true);
                c.Update(session, new(new("InProgress", "None", 0))); clock.Advance(3);
            }
            catch (Exception error) { failure = error; }
            finally { returned.Set(); }
        });
        bool finished;
        lock (gate) { thread.Start(); finished = returned.Wait(TimeSpan.FromSeconds(2)); }
        Assert.That(thread.Join(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(failure, Is.Null); Assert.That(finished, Is.True); Assert.That(api.Accepts, Is.EqualTo(1));
        Assert.That(diagnostics.GetState().Health.ProducerDropped, Is.GreaterThan(0));
    }

    [Test]
    public async Task WaitingSnapshotReportsRemainingDurationAndDeadline()
    {
        var clock = new ManualClock(); var api = new FakeReadyCheckClient();
        await using var diagnostics = new DiagnosticCollector();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now, diagnostics);
        var session = c.BeginSession(api); c.Update(session, new(ReadyCheckSnapshot.Inactive)); c.SetArmed(true);
        c.Update(session, new(new("InProgress", "None", 0)));
        clock.Advance(1); c.AcceptDelay = TimeSpan.FromSeconds(8);
        Assert.That(diagnostics.GetState().Operation.RemainingSeconds, Is.EqualTo(2).Within(.1));
        Assert.That(diagnostics.GetState().Operation.Deadline, Is.EqualTo(DateTimeOffset.UnixEpoch.AddSeconds(3)));
        clock.Advance(2);
        Assert.That(diagnostics.GetState().Operation.RemainingSeconds, Is.Null);
    }

    [Test]
    public async Task LongestScalarEvidenceStillHasBoundedEncodedRecords()
    {
        var clock = new ManualClock(); var api = new FakeReadyCheckClient();
        await using var diagnostics = new DiagnosticCollector();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now, diagnostics);
        var session = c.BeginSession(api); c.Update(session, new(ReadyCheckSnapshot.Inactive)); c.SetArmed(true);
        c.Update(session, new(new("InProgress", "None", double.MaxValue)));
        c.Update(session, new(new("InProgress", "None", double.MaxValue)));
        clock.Advance(3);
        var records = await QueryEvents(diagnostics);
        foreach (var record in records)
        {
            // Build identities are bounded to 128 encoded UTF-8 bytes at collector construction.
            var worstBuild = record with { Build = new string('a', 128) };
            Assert.That(JsonSerializer.SerializeToUtf8Bytes(worstBuild, DiagnosticJson.Options).Length + 1,
                Is.LessThanOrEqualTo(DiagnosticLimits.EventBytes), record.Code);
        }
    }

    [Test]
    public async Task StaleUpdateCannotClaimTheCurrentCheckIdentity()
    {
        var clock = new ManualClock(); await using var diagnostics = new DiagnosticCollector();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now, diagnostics);
        var old = c.BeginSession(new FakeReadyCheckClient());
        var current = c.BeginSession(new FakeReadyCheckClient()); c.Update(current, new(ReadyCheckSnapshot.Inactive));
        c.Update(current, new(new("InProgress", "None", 1)));
        c.Update(old, new(new("InProgress", "None", 2)));
        var stale = (await QueryEvents(diagnostics)).Single(r => r.Code == "readyCheck.staleUpdate").Details.Controller!;
        Assert.That(stale.EventGeneration, Is.EqualTo(old));
        Assert.That(stale.EventCheckId, Is.Null);
        Assert.That(stale.ReadyCheck.CheckId, Is.Not.Null);
    }

    [Test]
    public async Task StaleFinalReadUsesItsOwnCheckTimerAndReportsAllFailedConditions()
    {
        var clock = new ManualClock(); var read = new TaskCompletionSource<ReadyCheckSnapshot>();
        var api = new FakeReadyCheckClient { Read = _ => read.Task };
        await using var diagnostics = new DiagnosticCollector();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now, diagnostics);
        var session = c.BeginSession(api); c.Update(session, new(ReadyCheckSnapshot.Inactive)); c.SetArmed(true);
        c.Update(session, new(new("InProgress", "None", 5))); clock.Advance(3);
        c.Update(session, new(ReadyCheckSnapshot.Inactive));
        // A disarmed second check in the same session must not replace the first attempt's last timer.
        c.SetArmed(false); c.Update(session, new(new("InProgress", "None", 100)));
        read.SetResult(new("Invalid", "Declined", 1));
        var decision = (await QueryEvents(diagnostics)).Single(r => r.Code == "accept.verification").Details.Controller!;
        Assert.That(decision.EventCheckId, Is.EqualTo(1));
        Assert.That(decision.ReadyCheck.CheckId, Is.EqualTo(2));
        Assert.That(decision.LastTimer, Is.EqualTo(5));
        Assert.That(decision.Failures, Is.EqualTo(EligibilityFailure.StaleOperation | EligibilityFailure.NotPending |
            EligibilityFailure.TimerBackwards | EligibilityFailure.InsufficientTimer));
    }

    private static async Task<DiagnosticEvent[]> QueryEvents(DiagnosticCollector diagnostics)
    {
        var path = Path.Combine(Path.GetTempPath(), "afktor-controller-api-" + Guid.NewGuid());
        try
        {
            await using var host = await DiagnosticHost.StartAsync(diagnostics, path);
            var descriptor = JsonSerializer.Deserialize<DiscoveryDescriptor>(await File.ReadAllTextAsync(host.DiscoveryPath!), DiagnosticJson.Options)!;
            using var http = new HttpClient { BaseAddress = new Uri(host.Endpoint!) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", descriptor.Token);
            var stateJson = await http.GetStringAsync("/diagnostics/state");
            var state = JsonSerializer.Deserialize<DiagnosticState>(stateJson, DiagnosticJson.Options)!;
            Assert.That(state.ReadyCheck, Is.EqualTo(diagnostics.GetState().ReadyCheck));
            var text = await http.GetStringAsync("/diagnostics/events?limit=100");
            Assert.That(stateJson + text, Does.Not.Contain(descriptor.Token));
            Assert.That(stateJson + text, Does.Not.Contain("SECRET"));
            var page = JsonSerializer.Deserialize<DiagnosticEventPage>(text, DiagnosticJson.Options)!;
            Assert.That(page.Completeness, Is.EqualTo("currentRunMemoryOnly"));
            foreach (var record in page.Events)
                Assert.That(JsonSerializer.SerializeToUtf8Bytes(record, DiagnosticJson.Options).Length + 1,
                    Is.LessThanOrEqualTo(DiagnosticLimits.EventBytes), record.Code);
            return page.Events;
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Get ? HttpStatusCode.OK : status)
            { Content = new StringContent(request.Method == HttpMethod.Get
                ? "{\"state\":\"InProgress\",\"playerResponse\":\"None\",\"timer\":3,\"SECRET_BODY\":true}"
                : "SECRET_BODY") });
    }

    private sealed class BrokenSink(bool hung) : IDiagnosticSink
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask WriteAsync(DiagnosticEvent record, CancellationToken token)
        {
            Entered.TrySetResult();
            if (hung) await Release.Task;
            throw new IOException("SECRET_SINK");
        }
    }
}
