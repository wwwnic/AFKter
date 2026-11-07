using System.Diagnostics;
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
    public async Task InvalidEnumsCannotSmuggleTextAndParallelProducersReportLoss()
    {
        await using var collector = new DiagnosticCollector();
        collector.Publish(new((DiagnosticCode)999));
        Assert.That(collector.GetState().Health.ProducerDropped, Is.EqualTo(1));
        Parallel.For(0, 10000, i => collector.ConnectionChanged(ConnectionStage.Connected, i, DiagnosticReason.None));
        var state = collector.GetState();
        Assert.That(state.Health.ProducerDropped, Is.GreaterThan(1));
        Assert.That(collector.GetEvents(collector.RunId, 0, 100)!.Completeness, Is.EqualTo("incomplete"));
        Assert.That(state.Health.QueueDepth, Is.Zero);
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
