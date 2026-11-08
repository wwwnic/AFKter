using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class DraftSessionMonitorTests
{
    private static LcuConnection Connection(string secret = "first") => LcuConnection.Parse($"LeagueClient:123:12345:{secret}:https")!;

    // Catches an idle read begun before an arming edge establishing a fresh admission baseline.
    [TestCase(false)]
    [TestCase(true)]
    public async Task ArmingDuringIdleReadRequiresFreshEvidence(bool initiallyEnabled)
    {
        var pending = new TaskCompletionSource<DraftSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new HeldFirstClient(started, pending);
        var observer = new DraftObserver();
        observer.SetEnabled(initiallyEnabled);
        await using var monitor = new DraftSessionMonitor(_ => Task.FromResult<LcuConnection?>(Connection()),
            _ => client, observer);
        var refreshing = monitor.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (initiallyEnabled) observer.SetEnabled(false);
        observer.SetEnabled(true);
        pending.SetResult(null);
        await refreshing;

        client.Snapshot = DraftObserverTests.Draft();
        await monitor.RefreshAsync();
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.MidDraft));

        client.Snapshot = null;
        await monitor.RefreshAsync();
        client.Snapshot = DraftObserverTests.Draft(102);
        await monitor.RefreshAsync();
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.Admitted));
    }

    // Catches losing arming across credential changes or admitting a reconnect baseline.
    [Test]
    public async Task ReplacedConnectionStartsObserveOnlyAndFailuresDisconnect()
    {
        var connection = Connection();
        var next = new ReplayClient();
        var observer = new DraftObserver();
        observer.SetEnabled(true);
        await using var monitor = new DraftSessionMonitor(_ => Task.FromResult<LcuConnection?>(connection), _ => next, observer);
        next.Snapshot = null;
        await monitor.RefreshAsync();
        next.Snapshot = DraftObserverTests.Draft();
        await monitor.RefreshAsync();
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.Admitted));
        connection = Connection("restarted");
        await monitor.RefreshAsync();
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.MidDraft));
        next.Fail = true;
        await monitor.RefreshAsync();
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.Disconnected));
    }

    // Catches a late read publishing after shutdown has invalidated work.
    [Test]
    public async Task ShutdownInvalidatesReadBeforeItReturns()
    {
        var pending = new TaskCompletionSource<DraftSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new DraftObserver();
        await using var monitor = new DraftSessionMonitor(_ => Task.FromResult<LcuConnection?>(Connection()),
            _ => new PendingClient(started, pending), observer);
        var refreshing = monitor.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = monitor.StopAsync();
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.Disconnected));
        pending.SetResult(DraftObserverTests.Draft());
        await Task.WhenAll(refreshing, stopping);
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.Disconnected));
    }

    private sealed class ReplayClient : IDraftClient
    {
        public DraftSnapshot? Snapshot { get; set; }
        public bool Fail { get; set; }
        public Task<DraftSnapshot?> ReadAsync(CancellationToken cancellationToken)
            => Fail ? Task.FromException<DraftSnapshot?>(new InvalidDataException("malformed")) : Task.FromResult(Snapshot);
    }
    private sealed class PendingClient(TaskCompletionSource started, TaskCompletionSource<DraftSnapshot?> pending) : IDraftClient
    {
        public Task<DraftSnapshot?> ReadAsync(CancellationToken cancellationToken) { started.TrySetResult(); return pending.Task; }
    }
    private sealed class HeldFirstClient(TaskCompletionSource started, TaskCompletionSource<DraftSnapshot?> pending) : IDraftClient
    {
        private bool first = true;
        public DraftSnapshot? Snapshot { get; set; }
        public Task<DraftSnapshot?> ReadAsync(CancellationToken cancellationToken)
        {
            if (!first) return Task.FromResult(Snapshot);
            first = false;
            started.TrySetResult();
            return pending.Task;
        }
    }
}
