using System.Threading.Channels;
using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class ReadyCheckSessionMonitorTests
{
    [Test]
    public async Task ReconnectPreservesArmingAndRejectsOldCallbacks()
    {
        var clock = new ManualClock();
        await using var controller = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        var old = new SessionClient(); var next = new SessionClient();
        var connection = LcuConnection.Parse("LeagueClient:1:1001:one:https");
        await using var monitor = new ReadyCheckSessionMonitor(_ => Task.FromResult(connection),
            c => c.ProcessId == 1 ? old : next, controller);
        await monitor.RefreshAsync();
        await old.Started.Task;
        Assert.That(controller.IsArmed, Is.False);
        Assert.That(monitor.IsConnected, Is.True);
        controller.SetArmed(true); old.EmitPending(0);
        connection = LcuConnection.Parse("LeagueClient:2:1002:two:https");
        await monitor.RefreshAsync(); await next.Started.Task;
        old.EmitPending(0); next.EmitPending(0); clock.Advance(3);
        Assert.That(old.Accepts, Is.Zero);
        Assert.That(next.Accepts, Is.EqualTo(1));
        Assert.That(controller.IsArmed, Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StreamLossImmediatelyCancelsCountdownAndReconnectsOnRefresh(bool malformed)
    {
        var clock = new ManualClock();
        await using var controller = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        var api = new SessionClient();
        await using var monitor = new ReadyCheckSessionMonitor(
            _ => Task.FromResult(LcuConnection.Parse("LeagueClient:1:1001:one:https")), _ => api, controller);
        await monitor.RefreshAsync(); await api.Started.Task;
        controller.SetArmed(true); api.EmitPending(0);
        api.Failure.SetException(malformed ? new InvalidDataException("bad payload") : new IOException("stream closed"));
        await monitor.SessionCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(monitor.IsConnected, Is.False);
        clock.Advance(3);
        Assert.That(api.Accepts, Is.Zero);
    }

    [Test]
    public async Task StopCancelsPendingAcceptAndAwaitsStreamShutdown()
    {
        var clock = new ManualClock();
        await using var controller = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        var api = new SessionClient();
        var monitor = new ReadyCheckSessionMonitor(
            _ => Task.FromResult(LcuConnection.Parse("LeagueClient:1:1001:one:https")), _ => api, controller);
        await monitor.RefreshAsync(); await api.Started.Task;
        controller.SetArmed(true); api.EmitPending(0);
        await monitor.DisposeAsync(); clock.Advance(3);
        Assert.That(api.Accepts, Is.Zero);
        Assert.That(monitor.SessionCompletion.IsCompleted, Is.True);
    }

    [Test]
    public async Task DiscoveryDoesNotDependOnCallingSynchronizationContext()
    {
        await using var controller = new ReadyCheckController();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new ReadyCheckSessionMonitor(async _ =>
        {
            entered.SetResult();
            await release.Task; // Models the tray's asynchronous discovery delegate.
            return null;
        }, _ => new SessionClient(), controller);
        var context = new CountingContext();
        var previous = SynchronizationContext.Current;
        Task refreshing;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            refreshing = monitor.RefreshAsync();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.SetResult();
        await refreshing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(context.Posts, Is.Zero, "Synchronous UI disposal must not wait for a queued UI continuation.");
    }

    private sealed class CountingContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    private sealed class SessionClient : IReadyCheckClient
    {
        private Action<ReadyCheckUpdate>? receive;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Accepts { get; private set; }
        public void EmitPending(double timer) => receive!(new(new("InProgress", "None", timer)));
        public Task<ReadyCheckSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(new ReadyCheckSnapshot("InProgress", "None", 5));
        public Task AcceptAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Accepts++; return Task.CompletedTask; }
        public async Task RunAsync(Action<ReadyCheckUpdate> onUpdate, CancellationToken token)
        {
            receive = onUpdate;
            receive(new(ReadyCheckSnapshot.Inactive, IsInitial: true));
            Started.TrySetResult();
            await Failure.Task.WaitAsync(token);
        }
    }
}
