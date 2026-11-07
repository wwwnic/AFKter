using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class ReadyCheckControllerTests
{
    [Test]
    public async Task AcceptsOnceAtThreeSecondsDespiteDuplicatePendingEvents()
    {
        var clock = new ManualClock();
        var api = new FakeReadyCheckClient();
        await using var controller = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        var session = StartSession(controller, api);
        controller.SetArmed(true);
        controller.Update(session, Pending(0));
        clock.Advance(2);
        controller.Update(session, Pending(2));
        clock.Advance(.999);
        Assert.That(api.Accepts, Is.Zero);
        Assert.That(controller.Remaining!.Value.TotalMilliseconds, Is.EqualTo(1).Within(.001));
        clock.Advance(.001);
        Assert.That(api.Accepts, Is.EqualTo(1));
        controller.Update(session, Pending(4));
        clock.Advance(10);
        Assert.That(api.Accepts, Is.EqualTo(1));
    }

    [Test]
    public async Task ChecksDetectedDisarmedStayIneligibleAfterArming()
    {
        var clock = new ManualClock(); var api = new FakeReadyCheckClient();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        var s = StartSession(c, api);
        Assert.That(c.IsArmed, Is.False);
        c.Update(s, Pending(0));
        c.SetArmed(true);
        c.Update(s, Pending(1));
        clock.Advance(4);
        Assert.That(api.Accepts, Is.Zero);
        c.Update(s, new(ReadyCheckSnapshot.Inactive));
        c.Update(s, Pending(0));
        clock.Advance(3);
        Assert.That(api.Accepts, Is.EqualTo(1));
    }

    [TestCase("decline")]
    [TestCase("expire")]
    [TestCase("accepted")]
    [TestCase("disarm")]
    [TestCase("rollback")]
    [TestCase("disconnect")]
    public async Task CancelsBeforeDeadline(string reason)
    {
        var clock = new ManualClock(); var api = new FakeReadyCheckClient();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        var s = StartSession(c, api); c.SetArmed(true); c.Update(s, Pending(1));
        switch (reason)
        {
            case "decline": c.Update(s, new(new("InProgress", "Declined", 2))); break;
            case "accepted": c.Update(s, new(new("InProgress", "Accepted", 2))); break;
            case "expire": c.Update(s, new(new("StrangerNotReady", "None", 2))); break;
            case "disarm": c.SetArmed(false); c.SetArmed(true); c.Update(s, Pending(2)); break;
            case "rollback": c.Update(s, Pending(0)); c.Update(s, Pending(2)); break;
            case "disconnect": await c.DisconnectAsync(); break;
        }
        clock.Advance(10);
        Assert.That(api.Accepts, Is.Zero);
        Assert.That(c.Remaining, Is.Null);
    }

    [Test]
    public async Task ReplacementSessionPreservesArmingAndIgnoresOldUpdates()
    {
        var clock = new ManualClock(); var old = new FakeReadyCheckClient(); var next = new FakeReadyCheckClient();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        var s = StartSession(c, old); c.SetArmed(true); c.Update(s, Pending(0));
        var replacement = StartSession(c, next);
        c.Update(s, Pending(0));
        c.Update(replacement, Pending(0));
        clock.Advance(3);
        Assert.That(c.IsArmed, Is.True);
        Assert.That(old.Accepts, Is.Zero);
        Assert.That(next.Accepts, Is.EqualTo(1));
    }

    [Test]
    public async Task AlreadyPendingSeedIsNotAcceptedAfterReconnect()
    {
        var clock = new ManualClock(); var api = new FakeReadyCheckClient();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        c.SetArmed(true); var s = c.BeginSession(api);
        c.Update(s, new(Pending(1).Snapshot, IsInitial: true));
        c.Update(s, Pending(2)); clock.Advance(10);
        Assert.That(api.Accepts, Is.Zero);
    }

    [TestCase("declined")]
    [TestCase("reset")]
    [TestCase("failure")]
    [TestCase("malformed")]
    public async Task FinalReadMustVerifyTheSamePendingCheck(string result)
    {
        var clock = new ManualClock(); var api = new FakeReadyCheckClient();
        api.Read = result switch
        {
            "declined" => _ => Task.FromResult(new ReadyCheckSnapshot("InProgress", "Declined", 4)),
            "reset" => _ => Task.FromResult(new ReadyCheckSnapshot("InProgress", "None", 0)),
            "malformed" => _ => Task.FromException<ReadyCheckSnapshot>(new InvalidDataException()),
            _ => _ => Task.FromException<ReadyCheckSnapshot>(new HttpRequestException())
        };
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        var s = StartSession(c, api); c.SetArmed(true); c.Update(s, Pending(1)); clock.Advance(3);
        Assert.That(api.Accepts, Is.Zero);
    }

    [TestCase("disarm")]
    [TestCase("end")]
    [TestCase("replace")]
    public async Task LateReadCannotResurrectCanceledWork(string change)
    {
        var clock = new ManualClock(); var api = new FakeReadyCheckClient();
        var read = new TaskCompletionSource<ReadyCheckSnapshot>();
        api.Read = _ => read.Task; // Deliberately ignore cancellation to exercise the generation check.
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        var s = StartSession(c, api); c.SetArmed(true); c.Update(s, Pending(0)); clock.Advance(3);
        if (change == "disarm") { c.SetArmed(false); c.SetArmed(true); }
        if (change == "end") { c.Update(s, new(ReadyCheckSnapshot.Inactive)); }
        if (change == "replace") { c.BeginSession(new FakeReadyCheckClient()); }
        read.SetResult(new("InProgress", "None", 4));
        Assert.That(api.Accepts, Is.Zero);
    }

    [Test]
    public async Task AmbiguousPostFailureDoesNotRetry()
    {
        var clock = new ManualClock(); var api = new FakeReadyCheckClient { FailAccept = true };
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        var s = StartSession(c, api); c.SetArmed(true); c.Update(s, Pending(0)); clock.Advance(3);
        c.Update(s, Pending(4)); clock.Advance(10);
        Assert.That(api.Accepts, Is.EqualTo(1));
        Assert.That(c.Status, Does.Contain("failed"));
    }

    [Test]
    public async Task DisposalCancelsOwnedCountdowns()
    {
        var clock = new ManualClock(); var api = new FakeReadyCheckClient();
        var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        var s = StartSession(c, api); c.SetArmed(true); c.Update(s, Pending(0));
        await c.DisposeAsync(); clock.Advance(10);
        Assert.That(api.Accepts, Is.Zero);
        Assert.That(c.Remaining, Is.Null);
    }

    [Test]
    public async Task FirstPendingEventBeforeSeedIsNotAssumedToBeANewCheck()
    {
        var clock = new ManualClock(); var api = new FakeReadyCheckClient();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        c.SetArmed(true); var s = c.BeginSession(api);
        c.Update(s, Pending(1)); clock.Advance(3);
        Assert.That(api.Accepts, Is.Zero);
    }

    [TestCase(3, 1)]
    [TestCase(10, 4)]
    public async Task FinalTimerMustHaveAdvancedThroughTheCountdown(double delay, double finalTimer)
    {
        var clock = new ManualClock(); var api = new FakeReadyCheckClient();
        api.Read = _ => Task.FromResult(new ReadyCheckSnapshot("InProgress", "None", finalTimer));
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        var s = StartSession(c, api);
        c.Update(s, new(ReadyCheckSnapshot.Inactive, IsInitial: true));
        c.SetArmed(true); c.Update(s, Pending(0)); clock.Advance(delay);
        Assert.That(api.Accepts, Is.Zero);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(5)]
    [TestCase(30)]
    public async Task UsesConfiguredDelayWithoutAcceptingEarly(int seconds)
    {
        var clock = new ManualClock();
        var api = new FakeReadyCheckClient
        {
            Read = _ => Task.FromResult(new ReadyCheckSnapshot("InProgress", "None", seconds))
        };
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        c.AcceptDelay = TimeSpan.FromSeconds(seconds);
        var session = StartSession(c, api);
        c.SetArmed(true);
        c.Update(session, Pending(0));
        if (seconds > 0)
        {
            clock.Advance(seconds - .001);
            Assert.That(api.Accepts, Is.Zero);
            Assert.That(c.Remaining!.Value.TotalMilliseconds, Is.EqualTo(1).Within(.001));
            clock.Advance(.001);
        }
        else { clock.Advance(0); }
        Assert.That(api.Accepts, Is.EqualTo(1));
        Assert.That(c.Remaining, Is.Null);
        c.Update(session, Pending(seconds));
        clock.Advance(10);
        Assert.That(api.Accepts, Is.EqualTo(1));
    }

    [Test]
    public async Task DelayEditKeepsActiveDeadlineAndAppliesToNextCheck()
    {
        var clock = new ManualClock();
        var api = new FakeReadyCheckClient();
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        var session = StartSession(c, api);
        c.SetArmed(true);
        c.Update(session, Pending(0));
        clock.Advance(1);
        c.AcceptDelay = TimeSpan.FromSeconds(5);
        c.Update(session, Pending(1));
        Assert.That(c.Remaining!.Value.TotalSeconds, Is.EqualTo(2));
        // Final timer of 3 proves the original verification threshold is retained.
        api.Read = _ => Task.FromResult(new ReadyCheckSnapshot("InProgress", "None", 3));
        clock.Advance(2);
        Assert.That(api.Accepts, Is.EqualTo(1));
        c.Update(session, new(ReadyCheckSnapshot.Inactive));
        c.Update(session, Pending(0));
        clock.Advance(3);
        Assert.That(api.Accepts, Is.EqualTo(1));
        Assert.That(c.Remaining!.Value.TotalSeconds, Is.EqualTo(2));
        api.Read = _ => Task.FromResult(new ReadyCheckSnapshot("InProgress", "None", 5));
        clock.Advance(2);
        Assert.That(api.Accepts, Is.EqualTo(2));
    }

    [Test]
    public async Task CountdownClearsWhileFinalReadAndPostAreInFlight()
    {
        var clock = new ManualClock();
        var read = new TaskCompletionSource<ReadyCheckSnapshot>();
        var post = new TaskCompletionSource();
        var api = new FakeReadyCheckClient { Read = _ => read.Task, Accept = _ => post.Task };
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        var session = StartSession(c, api);
        c.SetArmed(true);
        c.Update(session, Pending(0));
        try
        {
            clock.Advance(3);
            Assert.That(c.Remaining, Is.Null);
            Assert.That(api.Accepts, Is.Zero);
            read.SetResult(new("InProgress", "None", 3));
            Assert.That(api.Accepts, Is.EqualTo(1));
            Assert.That(c.Remaining, Is.Null);
        }
        finally
        {
            read.TrySetResult(new("InProgress", "None", 3));
            post.TrySetResult();
        }
    }

    [TestCase(5, 4, 5)]
    [TestCase(5, 5, 10)]
    [TestCase(0, 0, 1)]
    public async Task ConfiguredDelayStillRequiresCompatibleFinalTimer(int seconds, double finalTimer, double actualWait)
    {
        var clock = new ManualClock();
        var api = new FakeReadyCheckClient
        {
            Read = _ => Task.FromResult(new ReadyCheckSnapshot("InProgress", "None", finalTimer))
        };
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        c.AcceptDelay = TimeSpan.FromSeconds(seconds);
        var session = StartSession(c, api);
        c.SetArmed(true);
        c.Update(session, Pending(0));
        clock.Advance(actualWait);
        Assert.That(api.Accepts, Is.Zero);
        Assert.That(c.Remaining, Is.Null);
    }

    [TestCase("disarmed")]
    [TestCase("initial")]
    [TestCase("declined")]
    public async Task ZeroDelayStillRequiresAnEligibleCheck(string reason)
    {
        var clock = new ManualClock();
        var api = new FakeReadyCheckClient
        {
            Read = _ => Task.FromResult(new ReadyCheckSnapshot("InProgress", "Declined", 0))
        };
        await using var c = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        c.AcceptDelay = TimeSpan.Zero;
        var session = reason == "initial" ? c.BeginSession(api) : StartSession(c, api);
        if (reason != "disarmed") { c.SetArmed(true); }
        c.Update(session, Pending(0));
        clock.Advance(0);
        Assert.That(api.Accepts, Is.Zero);
        Assert.That(c.Remaining, Is.Null);
    }

    [TestCase(-1)]
    [TestCase(31)]
    [TestCase(1.5)]
    public async Task RejectsInvalidDelayWithoutChangingTheEffectiveSetting(double seconds)
    {
        await using var c = new ReadyCheckController();
        Assert.Throws<ArgumentOutOfRangeException>(() => c.AcceptDelay = TimeSpan.FromSeconds(seconds));
        Assert.That(c.AcceptDelay.TotalSeconds, Is.EqualTo(3));
    }

    private static long StartSession(ReadyCheckController controller, IReadyCheckClient api)
    {
        var session = controller.BeginSession(api);
        controller.Update(session, new(ReadyCheckSnapshot.Inactive, IsInitial: true));
        return session;
    }

    private static ReadyCheckUpdate Pending(double seconds) => new(new("InProgress", "None", seconds));
}

internal sealed class ManualClock
{
    private readonly List<(DateTimeOffset Due, TaskCompletionSource Source)> waits = [];
    public DateTimeOffset Now { get; private set; } = DateTimeOffset.UnixEpoch;
    public Task DelayAsync(TimeSpan delay, CancellationToken token)
    {
        var source = new TaskCompletionSource();
        var registration = token.Register(() => source.TrySetCanceled(token));
        waits.Add((Now + delay, source));
        return AwaitAsync(source.Task, registration);
    }
    private static async Task AwaitAsync(Task task, CancellationTokenRegistration registration)
    {
        using (registration) { await task.ConfigureAwait(false); }
    }
    public void Advance(double seconds)
    {
        Now += TimeSpan.FromSeconds(seconds);
        foreach (var wait in waits.Where(w => w.Due <= Now).ToArray())
        {
            waits.Remove(wait); wait.Source.TrySetResult();
        }
    }
}

internal sealed class FakeReadyCheckClient : IReadyCheckClient
{
    public int Accepts { get; private set; }
    public bool FailAccept { get; init; }
    public Func<CancellationToken, Task>? Accept { get; init; }
    public Func<CancellationToken, Task<ReadyCheckSnapshot>> Read { get; set; }
        = _ => Task.FromResult(new ReadyCheckSnapshot("InProgress", "None", 6));
    public Task<ReadyCheckSnapshot> ReadAsync(CancellationToken token) => Read(token);
    public Task AcceptAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Accepts++;
        return FailAccept ? Task.FromException(new HttpRequestException()) : Accept?.Invoke(token) ?? Task.CompletedTask;
    }
    public Task RunAsync(Action<ReadyCheckUpdate> receive, CancellationToken token) => Task.Delay(Timeout.Infinite, token);
}
