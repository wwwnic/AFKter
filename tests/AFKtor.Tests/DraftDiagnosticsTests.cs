using System.Text.Json;
using AFKtor.Core;
using AFKtor.Core.Diagnostics;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class DraftDiagnosticsTests
{
    // Catches omitting draft state from the API or retaining unbounded/raw external draft identities.
    [Test]
    public async Task ReadModelAndBoundedHistoryExposeDraftProgression()
    {
        await using var collector = new DiagnosticCollector();
        var observer = new DraftObserver(collector);
        observer.SetEnabled(true);
        var generation = observer.BeginSession();
        observer.Observe(generation, 1, null);
        observer.Observe(generation, 2, DraftObserverTests.Draft());
        Assert.That(collector.GetState().Draft?.Admission, Is.EqualTo(DraftAdmission.Admitted));
        for (var i = 3; i < 1100; i++) observer.Observe(generation, i, DraftObserverTests.Draft(remaining: i));
        observer.Disconnect();
        var history = collector.GetMemoryHistory();
        Assert.That(history.Events.Length, Is.LessThanOrEqualTo(DiagnosticLimits.MemoryEvents));
        Assert.That(history.Events[^1].Code, Is.EqualTo("draft.observed"));
        Assert.That(history.Events[^1].Details.Draft?.Admission, Is.EqualTo(DraftAdmission.Disconnected));
        Assert.That(history.Events.All(e => JsonSerializer.SerializeToUtf8Bytes(e).Length <= DiagnosticLimits.EventBytes), Is.True);
    }
}
