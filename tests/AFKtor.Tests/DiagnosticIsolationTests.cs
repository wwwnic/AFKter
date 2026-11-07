using System.Diagnostics;
using System.Reflection;
using AFKtor.Core.Diagnostics;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class DiagnosticIsolationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ThrowingAndHungSinksCannotBlockProducerOrShutdown(bool hung)
    {
        var sink = new FailingSink(hung);
        var collector = new DiagnosticCollector(sink);
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 20000; i++) collector.ConnectionChanged(ConnectionStage.Connected, i, DiagnosticReason.None);
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
        Assert.That(collector.GetState().Connection.Generation, Is.EqualTo(19999));
        Assert.That(collector.GetState().Health.SinkDropped, Is.GreaterThan(0));
        watch.Restart();
        await collector.DisposeAsync();
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2.2)));
        Assert.That(collector.GetState().Health.Storage, Is.EqualTo("unavailable"));
        Assert.That(collector.GetEvents(collector.RunId, 0, 100)!.Events.Any(e => e.Code == "app.stopped"), Is.False,
            "The first page should be bounded; app.stopped is at its tail.");
        var last = collector.GetEvents(collector.RunId, collector.GetState().SequenceWatermark - 1, 1)!;
        Assert.That(last.Events.Single().Code, Is.EqualTo("app.stopped"));
        sink.Release.TrySetResult();
    }

    [Test]
    public async Task InvalidObservationIsRejectedWithoutAdvancingState()
    {
        await using var collector = new DiagnosticCollector();
        collector.Publish(new((DiagnosticCode)999));
        var state = collector.GetState();
        Assert.That(state.Health.ProducerDropped, Is.EqualTo(1));
        Assert.That(state.SequenceWatermark, Is.EqualTo(1));
        Assert.That(state.Connection.Stage, Is.EqualTo(ConnectionStage.Unknown));
        Assert.That(collector.GetEvents(collector.RunId, 0, 100)!.Completeness, Is.EqualTo("incomplete"));
        Assert.That(state.Health.QueueDepth, Is.Zero);
    }

    [Test]
    public async Task ContendedProducerReturnsWithoutWaitingAndReportsLoss()
    {
        await using var collector = new DiagnosticCollector();
        // Hold the real critical section without adding a production test hook.
        // A dedicated producer thread makes contention independent of CPU count or pool scheduling.
        var gate = typeof(DiagnosticCollector).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(collector)!;
        using var returned = new ManualResetEventSlim();
        Exception? producerError = null;
        var producer = new Thread(() =>
        {
            try { collector.ConnectionChanged(ConnectionStage.Connected, 1, DiagnosticReason.None); }
            catch (Exception error) { producerError = error; }
            finally { returned.Set(); }
        }) { IsBackground = true };
        bool returnedWhileLocked;
        lock (gate)
        {
            producer.Start();
            returnedWhileLocked = returned.Wait(TimeSpan.FromSeconds(2));
        }
        Assert.That(producer.Join(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(producerError, Is.Null);
        Assert.That(returnedWhileLocked, Is.True, "The producer must return while another thread owns the collector lock.");
        var state = collector.GetState();
        Assert.That(state.Health.ProducerDropped, Is.EqualTo(1));
        Assert.That(state.SequenceWatermark, Is.EqualTo(1));
        Assert.That(state.Connection.Stage, Is.EqualTo(ConnectionStage.Unknown));
        Assert.That(collector.GetEvents(collector.RunId, 0, 100)!.Completeness, Is.EqualTo("incomplete"));

        collector.ConnectionChanged(ConnectionStage.Connected, 2, DiagnosticReason.None);
        state = collector.GetState();
        Assert.That(state.Connection.Generation, Is.EqualTo(2));
        Assert.That(state.SequenceWatermark, Is.EqualTo(2));
        Assert.That(state.Health.ProducerDropped, Is.EqualTo(1));
    }

    private sealed class FailingSink(bool hung) : IDiagnosticSink
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask WriteAsync(DiagnosticEvent record, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            if (hung) Release.Task.GetAwaiter().GetResult(); // Intentionally ignores cancellation and blocks before returning.
            throw new IOException("SECRET_SINK_EXCEPTION");
        }
    }
}
